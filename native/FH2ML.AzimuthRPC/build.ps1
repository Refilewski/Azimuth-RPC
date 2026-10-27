param(
    [string]$Generator = '',
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..\..'))
$buildDir = Join-Path $projectRoot 'build'
[xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Missing AzimuthRPC version' }

$packageDir = if ([string]::IsNullOrWhiteSpace($Output)) {
    Join-Path $repoRoot "artifacts\FH2ModPlatform-AzimuthRPC-$version"
} elseif ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $repoRoot $Output }

$configureArgs = @('-S', $projectRoot, '-B', $buildDir)
if ($Generator) { $configureArgs += @('-G', $Generator, '-A', 'x64') }
cmake @configureArgs
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed' }
cmake --build $buildDir --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native DLL build failed' }

$packageDir = [IO.Path]::GetFullPath($packageDir)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $packageDir.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must stay inside repository artifacts directory'
}

# Package Trinculo54's official pinned platform binary, never our own proxy.
$platformVersion = '2.7.3'
$archive = Join-Path $buildDir "FH2-Mod-Platform-v$platformVersion.zip"
$expanded = Join-Path $buildDir "platform-v$platformVersion"
$uri = "https://github.com/Trinculo54/fh2-mod-platform/releases/download/v$platformVersion/FH2-Mod-Platform-v$platformVersion.zip"
Invoke-WebRequest -Uri $uri -OutFile $archive
if (Test-Path $expanded) { Remove-Item $expanded -Recurse -Force }
Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
function Find-PlatformFile([string]$name) {
    $found = @(Get-ChildItem -Path $expanded -Recurse -File | Where-Object { $_.Name -ieq $name })
    if ($found.Count -ne 1) { throw "Expected one $name in official release; found $($found.Count)" }
    return $found[0].FullName
}
$platformDll = Find-PlatformFile 'd3dcompiler_47.dll'
$minHookLicense = Find-PlatformFile 'LICENSE-MinHook.txt'

if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $packageDir 'fh2-mods') | Out-Null
Copy-Item -LiteralPath (Join-Path $buildDir 'Release\AzimuthRPC.dll') -Destination (Join-Path $packageDir 'fh2-mods\AzimuthRPC.dll')
Copy-Item -LiteralPath $platformDll -Destination (Join-Path $packageDir 'd3dcompiler_47.dll')
Copy-Item -LiteralPath $minHookLicense -Destination (Join-Path $packageDir 'LICENSE-MinHook.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot 'platform-sdk\LICENSE') -Destination (Join-Path $packageDir 'LICENSE-FH2ModPlatform.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $packageDir 'LICENSE')
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $packageDir 'README.md')
Write-Host "FH2 Mod Platform AzimuthRPC $version package: $packageDir"
