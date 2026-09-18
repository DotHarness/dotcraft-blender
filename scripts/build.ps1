<#
.SYNOPSIS
  Assemble the complete DotCraft plugin bundle and record what went into it.

.DESCRIPTION
  A .NET plugin bundle must already contain its entry assembly, its .deps.json and every private
  dependency: DotCraft neither restores packages nor compiles on activation.
#>
param(
    [string]$OutputDirectory = '.artifacts/release',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'plugins/blender'
$project = Join-Path $root 'tools/src/DotCraft.Blender.Plugin/DotCraft.Blender.Plugin.csproj'

$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$bundle = Join-Path $output 'blender'
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null

dotnet build $project -c $Configuration --nologo -v q
if ($LASTEXITCODE) { throw 'Plugin build failed.' }

foreach ($item in '.craft-plugin', 'assets', 'skills') {
    Copy-Item (Join-Path $source $item) (Join-Path $bundle $item) -Recurse
}

# The host shares its own DotCraft and framework assemblies by simple name, so a bundle copy is dead weight.
$published = Join-Path $root "tools/src/DotCraft.Blender.Plugin/bin/$Configuration/net10.0"
$lib = New-Item -ItemType Directory -Force (Join-Path $bundle 'lib')
Get-ChildItem $published -File |
    Where-Object { $_.Extension -in '.dll', '.json' } |
    Where-Object { $_.Name -notmatch '^(DotCraft\.(Core|Agents|Runtime|Harness|Generators)|Microsoft\.Extensions\.AI)' } |
    Copy-Item -Destination $lib

# The plugin resolves the add-on from ContentRoot, so it travels with the bundle.
$bridge = New-Item -ItemType Directory -Force (Join-Path $bundle 'bridge')
Copy-Item (Join-Path $root 'bridge/bootstrap.py') $bridge
Copy-Item (Join-Path $root 'bridge/dotcraft_bridge') $bridge -Recurse
Get-ChildItem $bridge -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force

$manifest = Get-Content (Join-Path $source '.craft-plugin/plugin.json') -Raw | ConvertFrom-Json
$commit = (git -C $root rev-parse HEAD).Trim()
$sourceDirty = [bool](git -C $root status --porcelain)
$pluginFiles = @(Get-ChildItem $bundle -Recurse -File -Force | ForEach-Object {
    @{
        path   = [IO.Path]::GetRelativePath($bundle, $_.FullName).Replace('\', '/')
        sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})

@{
    schemaVersion = 1
    pluginId      = $manifest.id
    version       = $manifest.version
    minHostVersion = $manifest.dotnet.minHostVersion
    commit        = $commit
    sourceDirty   = $sourceDirty
    platform      = 'any'
    pluginFiles   = $pluginFiles
} | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $output 'release-manifest.json') -Encoding utf8

& "$PSScriptRoot/verify-package.ps1" -ArtifactDirectory $output
Write-Host "Bundle ready at $bundle (version $($manifest.version), $($pluginFiles.Count) files)" -ForegroundColor Green
