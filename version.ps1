# The version scheme, used by release.ps1. Dot-source it: . (Join-Path $PSScriptRoot "version.ps1")
#
# The version counts like an odometer where every digit runs 0-9: 1.0.0, 1.0.1 ... 1.0.9,
# 1.1.0 ... 1.9.9, 2.0.0. Every step raises it by one; a breaking change gets no jump of its
# own. The numbering started over at 1.0.0 before the first publish to nuget.org.

# The highest value minor and patch take. Change this one number to change the whole scheme.
$VersionDigitCeiling = 9

# The next version: patch up by one, and a digit that passes the ceiling becomes 0 and carries
# to the one to its left. 1.0.9 -> 1.1.0, 1.9.9 -> 2.0.0.
function Get-NextVersion([string]$current) {
    $parts = $current.Split('.')
    if ($parts.Count -ne 3 -or ($parts | Where-Object { $_ -notmatch '^\d+$' })) {
        throw "The version $current is not major.minor.patch."
    }

    [int]$major = $parts[0]
    [int]$minor = $parts[1]
    [int]$patch = $parts[2]

    if ($minor -gt $VersionDigitCeiling -or $patch -gt $VersionDigitCeiling) {
        throw "The version $current has a digit above $VersionDigitCeiling; minor and patch run 0-$VersionDigitCeiling."
    }

    $patch++
    if ($patch -gt $VersionDigitCeiling) {
        $patch = 0
        $minor++
    }

    if ($minor -gt $VersionDigitCeiling) {
        $minor = 0
        $major++
    }

    return "$major.$minor.$patch"
}
