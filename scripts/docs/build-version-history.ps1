#!/usr/bin/env pwsh
# Generates docfx/articles/version-history.md from the repo-root CHANGELOG.md,
# injecting a stable HTML anchor (<a id="v<version>"></a>) above every version
# heading "## [<version>]". The anchor id derives ONLY from the version token,
# so heading-format changes never break deep links. The app composes the same
# anchor in AppVersionInfo (v<version>) — keep the two in sync.
#
# Run from anywhere BEFORE building the docs:
#   pwsh scripts/docs/build-version-history.ps1
#   dotnet docfx docfx/docfx.json
# The generated article is gitignored; docfx's toc references it, so a docs
# build without this script fails fast on the missing href (intended).
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$changelog = Join-Path $repoRoot 'CHANGELOG.md'
$outFile = Join-Path $repoRoot 'docfx/articles/version-history.md'

if (-not (Test-Path $changelog)) {
    Write-Error "CHANGELOG.md not found at $changelog"
    exit 1
}

$anchored = 0
$out = foreach ($line in Get-Content $changelog) {
    if ($line -match '^##\s+\[(?<ver>[^\]]+)\]' -and $Matches['ver'] -ne 'Unreleased') {
        $anchored++
        "<a id=`"v$($Matches['ver'])`"></a>"
    }
    $line
}

if ($anchored -eq 0) {
    Write-Error 'No version headings (## [x.y.z]) found in CHANGELOG.md — malformed changelog?'
    exit 1
}

Set-Content -Path $outFile -Value $out -Encoding utf8
Write-Host "version-history.md generated ($anchored version anchors)."
