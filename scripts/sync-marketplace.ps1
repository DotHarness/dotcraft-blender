<#
.SYNOPSIS
  Stage a verified bundle into a checkout of DotHarness/dotcraft-plugins.

.DESCRIPTION
  Replaces plugins/blender, records its provenance, registers the marketplace entry, and runs the
  registry's own validator. Nothing here commits or pushes.
#>
param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$MarketplaceRoot
)

$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/verify-package.ps1" -ArtifactDirectory $ArtifactDirectory

$root = [IO.Path]::GetFullPath($MarketplaceRoot)
$target = [IO.Path]::GetFullPath((Join-Path $root 'plugins/blender'))
if (!$target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Invalid marketplace target.'
}

if (Test-Path $target) { Remove-Item -LiteralPath $target -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $ArtifactDirectory 'blender') -Destination $target -Recurse
Copy-Item -LiteralPath (Join-Path $ArtifactDirectory 'release-manifest.json') -Destination "$target/release-provenance.json"

$indexPath = Join-Path $root '.craft/plugins/marketplace.json'
$index = Get-Content $indexPath -Raw | ConvertFrom-Json
$entry = [pscustomobject]@{
    name     = 'blender'
    source   = @{ source = 'local'; path = './plugins/blender' }
    policy   = @{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }
    category = 'Engineering'
}
$index.plugins = @($index.plugins | Where-Object name -cne 'blender') + $entry
$index | ConvertTo-Json -Depth 30 | Set-Content $indexPath -Encoding utf8NoBOM

& node "$root/scripts/validate-registry.mjs"
if ($LASTEXITCODE) { throw 'Marketplace registry validation failed.' }
Write-Output 'Marketplace staged.'
