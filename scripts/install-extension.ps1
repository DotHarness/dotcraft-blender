<#
.SYNOPSIS
  Build the bridge add-on and install it into a Blender the user already has.

.DESCRIPTION
  For attaching to a Blender the user opened. Blender must be closed while this runs, and
  restarted afterwards.
#>
param(
    [string]$Blender,
    [string]$Repo = "user_default"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root "bridge/dotcraft_bridge"
$output = Join-Path $root ".out/dotcraft_bridge.zip"

if (-not $Blender) {
    $Blender = Get-ChildItem "$env:ProgramFiles\Blender Foundation" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "blender.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}
if (-not $Blender) { throw "No Blender found. Pass -Blender <path to blender.exe>." }

New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
& $Blender --command extension build --source-dir $source --output-filepath $output
if ($LASTEXITCODE -ne 0) { throw "extension build failed" }

& $Blender --command extension install-file --repo $Repo --enable $output
if ($LASTEXITCODE -ne 0) { throw "extension install-file failed" }

Write-Host "Installed. Restart Blender; the bridge starts with it." -ForegroundColor Green
