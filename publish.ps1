param(
    [string]$Runtime = 'win-x64',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    [xml]$props = Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props')
    $version = [string]$props.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($version)) { throw 'Version not found in Directory.Build.props' }
    if ([string]::IsNullOrWhiteSpace($Output)) { $Output = "artifacts/AzimuthRPC-$version-win-x64" }

    & "$PSScriptRoot/build.ps1" -SmokeTest
    if ($LASTEXITCODE -ne 0) { throw 'Validation failed' }

    $outputPath = [IO.Path]::GetFullPath($Output)
    $allowedOutputRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (-not $outputPath.StartsWith($allowedOutputRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Output must be a subdirectory of this repository artifacts directory'
    }
    if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Recurse -Force }

    dotnet publish 'src/AzimuthRPC.App/AzimuthRPC.App.csproj' `
        -c Release `
        -r $Runtime `
        --self-contained true `
        --nologo `
        -p:PublishSingleFile=true `
        -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:PublishTrimmed=false `
        -o $Output

    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

    $exe = Join-Path $Output 'AzimuthRPC.exe'
    if (-not (Test-Path $exe)) { throw "Published executable not found: $exe" }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $Output 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $Output 'README.md') -Force

    Write-Host "AzimuthRPC $version published: $exe"
    Get-Item $exe | Format-List Name,Length,LastWriteTime
} finally {
    Pop-Location
}
