param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build AzimuthRPC.sln -c Release -m:1 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

    dotnet build 'src/AzimuthRPC.App/AzimuthRPC.App.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Public app build failed' }

    dotnet build 'src/AzimuthRPC.DiscordBridge/AzimuthRPC.DiscordBridge.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Linux Discord bridge build failed' }

    foreach ($suite in @('FH2UiMode.Tests', 'AzimuthRPC.Core.Tests')) {
        dotnet "tests/$suite/bin/Release/net8.0/$suite.dll"
        if ($LASTEXITCODE -ne 0) { throw "$suite failed" }
    }

    if ($SmokeTest) {
        $exe = Join-Path $PSScriptRoot 'src/AzimuthRPC.App/bin/Release/net8.0-windows/AzimuthRPC.exe'
        $process = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(15000)) { throw 'Public tray smoke test timed out' }
        if ($process.ExitCode -ne 0) { throw "Public tray smoke test failed: $($process.ExitCode)" }
    }
} finally { Pop-Location }
