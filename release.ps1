#Requires -Version 7.0
<#
.SYNOPSIS
    Cuts a release: sets the version, verifies the package, commits and tags.

.DESCRIPTION
    The tag is what publishes. Pushing vX.Y.Z runs .github/workflows/publish.yml, which builds
    the package again on a runner and pushes it to nuget.org through Trusted Publishing.

    This script does everything up to that point and then stops, because a tag that has not left
    the machine can still be deleted and a published version cannot be replaced. Pass -Push to
    go all the way in one command.

    The version follows version.ps1: one step per release, every digit 0-9, so 1.0.9 is
    followed by 1.1.0 and 1.9.9 by 2.0.0. The first release, before any v* tag exists, is the
    version already in the csproj - 1.0.0. After that every release steps from the csproj's
    version. A local publish.ps1 that raised the csproj in between therefore makes the release
    skip that number on nuget.org, which is the point: a number never names two different builds.

    The package is tested, packed and shape-checked BEFORE the tag is created, so a release that
    cannot be built never becomes a tag. If that verification fails, the csproj is restored.

.PARAMETER Version
    Set an explicit version instead of the next step, e.g. 2.0.0 or 2.0.0-rc.1.

.PARAMETER Push
    Push the branch and the tag when everything succeeded, which starts the publish workflow,
    and copy the package to the local feed.

.PARAMETER DryRun
    Report what would happen and change nothing.

.PARAMETER Branch
    The branch a release may be cut from. Defaults to main.

.PARAMETER Feed
    The local feed the released package is copied to after a push.

.EXAMPLE
    ./release.ps1
    The next version, verified, committed and tagged locally. Nothing leaves the machine.

.EXAMPLE
    ./release.ps1 -Push
    The same, pushed: the workflow publishes it to nuget.org.

.EXAMPLE
    ./release.ps1 -DryRun
    Says which version a run would produce, and changes nothing.
#>
param(
    [string]$Version,
    [switch]$Push,
    [switch]$DryRun,
    [string]$Branch = "main",
    [string]$Feed = "G:\LocalNugetRepo"
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "version.ps1")

$name    = "Avig.AzureDevOps"
$root    = $PSScriptRoot
$project = Join-Path $root "src\$name\$name.csproj"
$packDir = Join-Path $root "src\$name\bin\Release"
$publish = Join-Path $root "publish.ps1"

function Fail([string]$Message) {
    Write-Host "      $Message" -ForegroundColor Red
    exit 1
}

function Save-Text([string]$Path, [string]$Text) {
    # UTF-8 without BOM, and the line endings already in the string. .gitattributes pins this
    # repository to CRLF, so nothing here may normalise to LF.
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

Write-Host ""
Write-Host "=== $name release ===" -ForegroundColor Cyan
if ($DryRun) {
    Write-Host "    DRY RUN - nothing is written, committed or pushed" -ForegroundColor Yellow
}

Write-Host ""

# --- [1/5] The repository has to be in a state a tag can describe ---
Write-Host "[1/5] Checking the working tree..." -ForegroundColor Yellow

$currentBranch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
if ($currentBranch -ne $Branch) {
    Fail "On branch '$currentBranch', not '$Branch'. Release from $Branch, or pass -Branch."
}

if ((git -C $root status --porcelain)) {
    Fail "The working tree has uncommitted changes. A tag must describe a committed state."
}

git -C $root fetch origin --tags --quiet
if ($LASTEXITCODE -ne 0) {
    Fail "Could not reach origin."
}

$behind = [int](git -C $root rev-list --count "$Branch..origin/$Branch")
if ($behind -gt 0) {
    Fail "$Branch is $behind commit(s) behind origin/$Branch. Pull first."
}

Write-Host "      clean, on $Branch, up to date with origin" -ForegroundColor Green

# --- [2/5] Work out the new version ---
Write-Host "[2/5] Version" -ForegroundColor Yellow

$csproj = Get-Content $project -Raw
$currentMatch = ([regex]'<Version>([^<]+)</Version>').Match($csproj)
if (-not $currentMatch.Success) {
    Fail "Found no <Version> in the csproj."
}

$current = $currentMatch.Groups[1].Value
$released = @(git -C $root tag -l "v*")

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$') {
        Fail "'$Version' is not a version. Expected 1.2.3 or 1.2.3-rc.1."
    }

    $next = $Version
    $reason = "explicit"
} elseif ($released.Count -eq 0) {
    # Nothing has been released yet: the first release is the version the csproj already has.
    $next = $current
    $reason = "first release"
} else {
    try {
        $next = Get-NextVersion $current
    } catch {
        Fail $_.Exception.Message
    }

    $reason = "next step"
}

if ($released.Count -gt 0 -and $next -eq $current) {
    Fail "$next is already the current version."
}

$currentCore = [version]($current -split '-')[0]
$nextCore = [version]($next -split '-')[0]
if ($nextCore -lt $currentCore) {
    Fail "$next is lower than the current $current. Versions on nuget.org only go up."
}

$tag = "v$next"

if ((git -C $root tag -l $tag)) {
    Fail "Tag $tag already exists. A released version cannot be replaced - pick another."
}

if ((git -C $root ls-remote --tags origin "refs/tags/$tag")) {
    Fail "Tag $tag already exists on origin. A published version cannot be replaced."
}

Write-Host "      $current -> $next   (tag $tag, $reason)" -ForegroundColor Green

$changesCsproj = $next -ne $current

if ($changesCsproj -and -not $DryRun) {
    $updatedCsproj = ([regex]'<Version>[^<]*</Version>').Replace($csproj, "<Version>$next</Version>", 1)
    Save-Text $project $updatedCsproj
}

# --- [3/5] Prove the release is buildable before it becomes a tag ---
Write-Host "[3/5] Verifying the package..." -ForegroundColor Yellow

if ($DryRun) {
    Write-Host "      skipped in a dry run" -ForegroundColor Yellow
} else {
    # The feed is left alone here: the version may already be there from a local publish, and the
    # package goes to the feed only once the release has been pushed.
    & $publish -NoBump -SkipFeed
    if ($LASTEXITCODE -ne 0) {
        Write-Host "      publish.ps1 failed - restoring the csproj, no tag created." -ForegroundColor Red
        git -C $root checkout -- $project
        exit 1
    }

    Write-Host "      tests, pack and shape check passed" -ForegroundColor Green
}

# --- [4/5] Commit and tag ---
Write-Host "[4/5] Commit and tag" -ForegroundColor Yellow

if ($DryRun) {
    if ($changesCsproj) {
        Write-Host "      would commit 'Release $tag' and tag $tag" -ForegroundColor Yellow
    } else {
        Write-Host "      would tag the current commit $tag (the csproj already says $next)" -ForegroundColor Yellow
    }
} else {
    if ($changesCsproj) {
        git -C $root add -- $project
        git -C $root commit --quiet -m "Release $tag"
        if ($LASTEXITCODE -ne 0) {
            Fail "Commit failed."
        }
    }

    git -C $root tag -a $tag -m "$name $next"
    if ($LASTEXITCODE -ne 0) {
        Fail "Tagging failed. Any release commit is still there; remove it or retag by hand."
    }

    Write-Host "      tagged $tag" -ForegroundColor Green
}

# --- [5/5] The push is what publishes ---
Write-Host "[5/5] Push" -ForegroundColor Yellow

if ($DryRun) {
    Write-Host "      would push $Branch and $tag" -ForegroundColor Yellow
} elseif ($Push) {
    git -C $root push origin $Branch
    if ($LASTEXITCODE -ne 0) {
        Fail "Pushing $Branch failed. The tag is still local; nothing published."
    }

    git -C $root push origin $tag
    if ($LASTEXITCODE -ne 0) {
        Fail "Pushing $tag failed. Nothing published."
    }

    Write-Host "      pushed - the publish workflow is starting" -ForegroundColor Green

    # The same version in the local feed, for AI Code Review and Splice. Never over one that is
    # already there: whoever has cached it would keep running the other build.
    if (Test-Path $Feed) {
        $existing = Join-Path $Feed "$name.$next.nupkg"
        if (Test-Path $existing) {
            Write-Host "      $name $next is already in $Feed from a local publish - left as it is" -ForegroundColor Yellow
        } else {
            Get-ChildItem -Path $packDir -Filter "$name.$next.*nupkg" | ForEach-Object {
                Copy-Item $_.FullName -Destination $Feed
                Write-Host "      $Feed\$($_.Name)" -ForegroundColor Green
            }
        }
    }
} else {
    Write-Host "      not pushed (pass -Push to do it here)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== $tag prepared ===" -ForegroundColor Cyan
Write-Host ""

if ($DryRun) {
    Write-Host "Nothing was changed. Run it again without -DryRun." -ForegroundColor Cyan
} elseif ($Push) {
    Write-Host "Watch it publish:" -ForegroundColor Cyan
    Write-Host "    https://github.com/marcuslindh/$name/actions" -ForegroundColor Gray
} else {
    Write-Host "Nothing has left this machine yet. To publish:" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "    git push origin $Branch && git push origin $tag" -ForegroundColor Gray
    Write-Host ""
    Write-Host "To undo instead:" -ForegroundColor Cyan
    Write-Host ""
    if ($changesCsproj) {
        Write-Host "    git tag -d $tag; git reset --hard HEAD~1" -ForegroundColor Gray
    } else {
        Write-Host "    git tag -d $tag" -ForegroundColor Gray
    }
}

Write-Host ""
