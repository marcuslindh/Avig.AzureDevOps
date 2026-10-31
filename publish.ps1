#Requires -Version 7.0
<#
.SYNOPSIS
    Tests, packs and shape-checks Avig.AzureDevOps.

.DESCRIPTION
    Proves that the version in the csproj builds into the package it should: the tests pass,
    the package packs, and it holds exactly what it should. The package is left in
    src\Avig.AzureDevOps\bin\Release. The script neither changes the version nor copies or
    pushes the package anywhere.

    Publishing is release.ps1's job: it sets the next version, runs this script, commits and
    tags, and the tag runs .github/workflows/publish.yml, which runs this script again on a
    GitHub runner and pushes the package to nuget.org. AI Code Review and Splice fetch it from
    there.

    The shape check allows exactly the dependencies the package should have. A new dependency ends up
    in every program that uses the package, and should therefore be added here on purpose -
    not sneak in via a PackageReference that forgot PrivateAssets.

.PARAMETER SkipTests
    Skip the test run. Use when you have already run them.

.PARAMETER SkipVerify
    Skip the shape check of the nupkg. Do not do that without a specific reason.
#>
param(
    [switch]$SkipTests,
    [switch]$SkipVerify
)

$ErrorActionPreference = "Stop"

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

# --- [1/4] Version ---
Write-Host "[1/4] Version" -ForegroundColor Yellow

$csproj = Get-Content $project -Raw
$versionMatch = ([regex]'<Version>([^<]+)</Version>').Match($csproj)
if (-not $versionMatch.Success) {
    Write-Host "      ERROR: <Version> is missing in $project" -ForegroundColor Red
    exit 1
}

$version = $versionMatch.Groups[1].Value
Write-Host "      $version" -ForegroundColor Green

# --- [2/4] Tests ---
if ($SkipTests) {
    Write-Host "[2/4] Tests skipped" -ForegroundColor DarkYellow
} else {
    Write-Host "[2/4] Testing..." -ForegroundColor Yellow
    dotnet test $tests -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host "      Tests failed - nothing packed." -ForegroundColor Red
        exit 1
    }
    Write-Host "      All tests green" -ForegroundColor Green
}

# --- [3/4] Pack ---
Write-Host "[3/4] Packing..." -ForegroundColor Yellow

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

# --- [4/4] Verify the package's shape ---
if ($SkipVerify) {
    Write-Host "[4/4] Shape check skipped" -ForegroundColor DarkYellow
} else {
    Write-Host "[4/4] Verifying the package contents..." -ForegroundColor Yellow

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

Write-Host ""
Write-Host "=== $name v$version - $packDir\$($package.Name) ===" -ForegroundColor Cyan
Write-Host ""
