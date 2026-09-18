<#
.SYNOPSIS
  Check that a built bundle is complete, recorded, and free of host assemblies.
#>
param([Parameter(Mandatory)][string]$ArtifactDirectory)

$ErrorActionPreference = 'Stop'
$artifacts = [IO.Path]::GetFullPath($ArtifactDirectory)
$manifest = Get-Content (Join-Path $artifacts 'release-manifest.json') -Raw | ConvertFrom-Json
$bundle = [IO.Path]::GetFullPath((Join-Path $artifacts 'blender'))

if ($manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid plugin version: $($manifest.version)" }
if ($manifest.minHostVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid minHostVersion.' }
if ($manifest.pluginId -cne 'blender') { throw "Unexpected plugin id: $($manifest.pluginId)" }

$plugin = Get-Content (Join-Path $bundle '.craft-plugin/plugin.json') -Raw | ConvertFrom-Json
if ($plugin.version -cne $manifest.version) { throw 'Manifest version does not match the bundle.' }
if ($plugin.dotnet.entryAssembly -cne './lib/DotCraft.Blender.dll') { throw 'Unexpected entry assembly.' }
if ($plugin.dotnet.entryType -cne 'DotCraft.Blender.Plugin') { throw 'Unexpected entry type.' }

foreach ($entry in $manifest.pluginFiles) {
    $path = [IO.Path]::GetFullPath((Join-Path $bundle $entry.path))
    if (!$path.StartsWith($bundle + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Recorded path escapes the bundle: $($entry.path)"
    }
    if ((Get-FileHash $path -Algorithm SHA256).Hash -ine $entry.sha256) {
        throw "Plugin content hash mismatch: $($entry.path)"
    }
}
foreach ($file in Get-ChildItem $bundle -File -Recurse -Force) {
    $relative = [IO.Path]::GetRelativePath($bundle, $file.FullName).Replace('\', '/')
    if ($relative -cnotin $manifest.pluginFiles.path) { throw "Unrecorded bundle content: $relative" }
}

foreach ($required in
    'lib/DotCraft.Blender.dll',
    'lib/DotCraft.Blender.Attach.dll',
    'lib/DotCraft.Blender.deps.json',
    'bridge/bootstrap.py',
    'bridge/dotcraft_bridge/server.py',
    'skills/blender/SKILL.md',
    'skills/blender/agents/openai.yaml') {
    if (!(Test-Path (Join-Path $bundle $required))) { throw "Missing bundle content: $required" }
}

# The host supplies these by simple name, so a bundled copy is ignored at load time.
foreach ($name in 'DotCraft.Core', 'DotCraft.Agents', 'DotCraft.Runtime', 'DotCraft.Harness', 'DotCraft.Generators') {
    if (Get-ChildItem $bundle -File -Recurse -Filter "$name.dll") { throw "Host assembly was bundled: $name" }
}

if (Get-ChildItem $bundle -Recurse -Directory -Filter '__pycache__') { throw 'Compiled Python caches were bundled.' }

Write-Output "Package integrity verified ($($manifest.pluginFiles.Count) files, version $($manifest.version))."
