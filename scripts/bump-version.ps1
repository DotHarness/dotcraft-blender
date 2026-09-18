<#
.SYNOPSIS
  Set the release version everywhere this repository records it.
#>
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version '$Version'. Expected X.Y.Z." }

$root = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$targets = @(
    @{ Path = 'plugins/blender/.craft-plugin/plugin.json'; Pattern = '(?m)^(\s*"version"\s*:\s*")([^"]+)(")'; Value = $Version }
    @{ Path = 'bridge/dotcraft_bridge/blender_manifest.toml'; Pattern = '(?m)^(version\s*=\s*")([^"]+)(")'; Value = $Version }
    @{ Path = 'bridge/dotcraft_bridge/__init__.py'; Pattern = '(?m)^(\s*"version":\s*\()([^)]+)(\))'; Value = ($Version -replace '\.', ', ') }
)

$updates = foreach ($target in $targets) {
    $path = Join-Path $root $target.Path
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Version file not found: $($target.Path)" }
    $pattern = [regex]$target.Pattern
    $content = [IO.File]::ReadAllText($path)
    if ($pattern.Matches($content).Count -ne 1) { throw "Expected exactly one version in $($target.Path)." }
    [pscustomobject]@{
        Relative = $target.Path
        Path     = $path
        Pattern  = $pattern
        Value    = $target.Value
        Content  = $pattern.Replace($content, '${1}' + $target.Value + '${3}', 1)
    }
}

$utf8NoBom = [Text.UTF8Encoding]::new($false)
foreach ($update in $updates) {
    [IO.File]::WriteAllText($update.Path, $update.Content, $utf8NoBom)
}

foreach ($update in $updates) {
    $written = $update.Pattern.Match([IO.File]::ReadAllText($update.Path))
    if (!$written.Success -or $written.Groups[2].Value -cne $update.Value) {
        throw "Version verification failed: $($update.Relative)"
    }
    Write-Output "Updated $($update.Relative) -> $Version"
}
