#Requires -Version 7.0
<#
.SYNOPSIS
    Builds, tests, packs and shape-checks Avig.AzureDevOps.

.DESCRIPTION
    The same steps as Avig.Result: version, tests, pack, shape check and copy to the
    local feed. The script never pushes anywhere. AI Code Review and Splice fetch
    the package from the feed via their nuget.config.

    Publishing to nuget.org is release.ps1's job: it sets the version, tags it, and the tag
    runs .github/workflows/publish.yml, which calls this script with -NoBump on a GitHub runner.
    The runner has no local feed, and then the copy is skipped.

    The shape check allows exactly the dependencies the package should have. A new dependency ends up
    in every program that uses the package, and should therefore be added here on purpose -
    not sneak in via a PackageReference that forgot PrivateAssets.

.PARAMETER NoBump
    Keep the current version instead of raising it.

.PARAMETER SkipFeed
    Neither check nor copy to the local feed. release.ps1 uses it to verify a release whose
    version may already be in the feed from a local publish.

.NOTES
    The version scheme is in version.ps1: an odometer where every digit runs 0-9, so 1.0.9 is
    followed by 1.1.0 and 1.9.9 by 2.0.0.

.PARAMETER SkipTests
    Skip the test run. Use when you have already run them.

.PARAMETER SkipVerify
    Skip the shape check of the nupkg. Do not do that without a specific reason.

.PARAMETER Feed
    The directory the package is copied to.
#>
param(
    [switch]$NoBump,
    [switch]$SkipTests,
    [switch]$SkipVerify,
    [switch]$SkipFeed,
    [string]$Feed = "G:\LocalNugetRepo"
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "version.ps1")

$name    = "Avig.AzureDevOps"
$root    = $PSScriptRoot
$project = Join-Path $root "src\$name\$name.csproj"
$tests   = Join-Path $root "tests\$name.Tests\$name.Tests.csproj"
$packDir = Join-Path $root "src\$name\bin\Release"

# The dependencies the package may have. Avig.Result because Result<T> is part of the public
# surface, Http for the typed clients and Logging for the logging. Where the token is kept is
# up to the application, so nothing here touches the file system or encryption.
$allowedDependencies = @(
    "Avig.Result",
    "Microsoft.Extensions.Http",
    "Microsoft.Extensions.Logging.Abstractions"
)

Write-Host ""
Write-Host "=== $name ===" -ForegroundColor Cyan
Write-Host ""

# --- [1/5] Version ---
Write-Host "[1/5] Version" -ForegroundColor Yellow

# XmlDocument.Load reads the file's own encoding declaration. Get-Content without -Encoding does
# not, and then every a-ring and dash gets doubled on every bump.
$csproj = New-Object System.Xml.XmlDocument
$csproj.PreserveWhitespace = $true
$csproj.Load((Resolve-Path $project))

$currentVersion = [string](($csproj.Project.PropertyGroup | Where-Object { $_.Version }).Version)
if (-not $currentVersion) {
    Write-Host "      ERROR: <Version> is missing in $project" -ForegroundColor Red
    exit 1
}

if ($NoBump) {
    $version = $currentVersion
    Write-Host "      $version (no bump)" -ForegroundColor Yellow
} else {
    $version = Get-NextVersion $currentVersion
    foreach ($pg in $csproj.Project.PropertyGroup) {
        if ($pg.Version) { $pg.Version = $version }
    }
    $csproj.Save((Resolve-Path $project))
    Write-Host "      $currentVersion -> $version" -ForegroundColor Yellow
}

# A package that is already in the feed must never be replaced under the same number: whoever
# has cached it keeps their copy and runs different code from whoever fetches it fresh.
# Path.Combine and not Join-Path: Join-Path fails on a drive that does not exist, and a GitHub
# runner has no G:.
$existing = [System.IO.Path]::Combine($Feed, "$name.$version.nupkg")
if (-not $SkipFeed -and (Test-Path $existing)) {
    Write-Host "      ERROR: $name $version already exists in the feed." -ForegroundColor Red
    Write-Host "             Raise <Version> instead of overwriting it." -ForegroundColor Red
    exit 1
}

# --- [2/5] Tests ---
if ($SkipTests) {
    Write-Host "[2/5] Tests skipped" -ForegroundColor DarkYellow
} else {
    Write-Host "[2/5] Testing..." -ForegroundColor Yellow
    dotnet test $tests -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host "      Tests failed - nothing packed." -ForegroundColor Red
        exit 1
    }
    Write-Host "      All tests green" -ForegroundColor Green
}

# --- [3/5] Pack ---
Write-Host "[3/5] Packing..." -ForegroundColor Yellow

if (Test-Path $packDir) {
    Get-ChildItem -Path $packDir -Filter "*.nupkg" -ErrorAction SilentlyContinue | Remove-Item -Force
    Get-ChildItem -Path $packDir -Filter "*.snupkg" -ErrorAction SilentlyContinue | Remove-Item -Force
}

dotnet pack $project -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "      Pack failed" -ForegroundColor Red
    exit 1
}

$package = Get-ChildItem -Path $packDir -Filter "$name.$version.nupkg" | Select-Object -First 1
if (-not $package) {
    Write-Host "      Found no package for version $version" -ForegroundColor Red
    exit 1
}
Write-Host "      $($package.Name)" -ForegroundColor Green

# --- [4/5] Verify the package's shape ---
if ($SkipVerify) {
    Write-Host "[4/5] Shape check skipped" -ForegroundColor DarkYellow
} else {
    Write-Host "[4/5] Verifying the package contents..." -ForegroundColor Yellow

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entries = $zip.Entries | ForEach-Object { $_.FullName }
        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -eq "$name.nuspec" }
        if (-not $nuspecEntry) {
            Write-Host "      ERROR: nuspec is missing from the package" -ForegroundColor Red
            exit 1
        }
        $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
        try { $nuspec = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally {
        $zip.Dispose()
    }

    foreach ($required in @("lib/net10.0/$name.dll", "lib/net10.0/$name.xml", "README.md")) {
        if (-not ($entries | Where-Object { $_ -eq $required })) {
            Write-Host "      ERROR: $required is missing from the package" -ForegroundColor Red
            exit 1
        }
    }

    $deps = @($nuspec.package.metadata.dependencies.group.dependency) | Where-Object { $_ -ne $null }
    $unexpected = @($deps | Where-Object { $allowedDependencies -notcontains $_.id })
    if ($unexpected.Count -gt 0) {
        Write-Host "      ERROR: the package pulls in dependencies that are not allowed:" -ForegroundColor Red
        $unexpected | ForEach-Object { Write-Host "        $($_.id) $($_.version)" -ForegroundColor Red }
        Write-Host "      If the dependency is intentional, add it to `$allowedDependencies." -ForegroundColor Red
        exit 1
    }

    if (-not $nuspec.package.metadata.license) {
        Write-Host "      ERROR: license is missing from the nuspec" -ForegroundColor Red
        exit 1
    }

    Write-Host "      dll, xml documentation, README and license present; dependencies: $(($deps | ForEach-Object { $_.id }) -join ', ')" -ForegroundColor Green
}

# --- [5/5] Copy to the local feed ---
# The feed is this machine's. A GitHub runner has none, and then the package simply stays in
# bin\Release, where the publish workflow picks it up.
if ($SkipFeed) {
    Write-Host "[5/5] Feed skipped - the package stays in bin\Release" -ForegroundColor DarkYellow
} elseif (-not (Test-Path $Feed)) {
    Write-Host "[5/5] $Feed does not exist - the package stays in bin\Release" -ForegroundColor DarkYellow
} else {
    Write-Host "[5/5] Copying to the feed..." -ForegroundColor Yellow
    Get-ChildItem -Path $packDir -Filter "$name.$version.*nupkg" | ForEach-Object {
        Copy-Item $_.FullName -Destination $Feed -Force
        Write-Host "      $Feed\$($_.Name)" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "=== $name v$version ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Raise the version in AI Code Review and Splice to pick it up." -ForegroundColor Gray
