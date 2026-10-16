param(
    [string]$Generator = '',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot '..\..'))
$buildDir = Join-Path $projectRoot 'build'

$manifestPath = Join-Path $projectRoot 'mod.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$modVersion = [string]$manifest.version
if ([string]::IsNullOrWhiteSpace($modVersion)) { throw 'Native mod version not found in mod.json' }

if ([string]::IsNullOrWhiteSpace($Output)) {
    $packageDir = Join-Path $repoRoot "artifacts\FH2ML-AzimuthRPC-$modVersion"
} elseif ([System.IO.Path]::IsPathRooted($Output)) {
    $packageDir = $Output
} else {
    $packageDir = Join-Path $repoRoot $Output
}

$configureArgs = @('-S', $projectRoot, '-B', $buildDir)
if (-not [string]::IsNullOrWhiteSpace($Generator)) {
    $configureArgs += @('-G', $Generator, '-A', 'x64')
}

cmake @configureArgs
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed' }

cmake --build $buildDir --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native mod build failed' }

$packageDir = [IO.Path]::GetFullPath($packageDir)
$allowedPackageRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $packageDir.StartsWith($allowedPackageRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must be a subdirectory of this repository artifacts directory'
}
if (Test-Path -LiteralPath $packageDir) { Remove-Item -LiteralPath $packageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
Copy-Item (Join-Path $buildDir 'Release\AzimuthRPC.dll') (Join-Path $packageDir 'AzimuthRPC.dll') -Force
Copy-Item $manifestPath (Join-Path $packageDir 'mod.json') -Force
Copy-Item (Join-Path $projectRoot 'README.md') (Join-Path $packageDir 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $packageDir 'LICENSE') -Force

Write-Host "FH2ML AzimuthRPC $modVersion package: $packageDir"
