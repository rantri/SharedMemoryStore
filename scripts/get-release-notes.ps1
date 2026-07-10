[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$changelogPath = Join-Path $root "CHANGELOG.md"
$lines = Get-Content -LiteralPath $changelogPath
$headingPattern = "^##\s+$([regex]::Escape($Version))(?:\s+-\s+\d{4}-\d{2}-\d{2})?\s*$"
$start = -1

for ($index = 0; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match $headingPattern) {
        $start = $index
        break
    }
}

if ($start -lt 0) {
    throw "CHANGELOG.md does not contain a level-two heading for version $Version."
}

$end = $lines.Count
for ($index = $start + 1; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match '^##\s+') {
        $end = $index
        break
    }
}

$notes = ($lines[($start + 1)..($end - 1)] -join [Environment]::NewLine).Trim()
if ([string]::IsNullOrWhiteSpace($notes)) {
    throw "CHANGELOG.md has no release notes for version $Version."
}

$resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    [System.IO.Path]::GetFullPath($OutputPath)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $root $OutputPath))
}

$outputDirectory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
Set-Content -LiteralPath $resolvedOutput -Value $notes -Encoding utf8
Write-Host "Wrote release notes for $Version to $resolvedOutput"
