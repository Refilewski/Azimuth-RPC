param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build AzimuthRPC.sln -c Release -m:1 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

    dotnet build 'src/AzimuthRPC.App/AzimuthRPC.App.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Public app build failed' }

    foreach ($suite in @('FH2UiMode.Tests', 'AzimuthRPC.Core.Tests')) {
        dotnet "tests/$suite/bin/Release/net8.0/$suite.dll"
        if ($LASTEXITCODE -ne 0) { throw "$suite failed" }
    }

    if ($SmokeTest) {
        $exe = Join-Path $PSScriptRoot 'src/AzimuthRPC.App/bin/Release/net8.0-windows/AzimuthRPC.exe'
        $process = Start-Process -FilePath $exe -ArgumentList '--smoke-test', '--keep-logs' -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(15000)) { throw 'Public tray smoke test timed out' }
        if ($process.ExitCode -ne 0) { throw "Public tray smoke test failed: $($process.ExitCode)" }
        # A zero exit code is not enough: confirm the EXE actually wrote its
        # bootstrap diagnostics (especially important for tray-only apps).
        $candidates = @(
            (Join-Path $env:LOCALAPPDATA 'AzimuthRPC\logs\AzimuthRPC.log'),
            (Join-Path ([IO.Path]::GetTempPath()) 'AzimuthRPC\AzimuthRPC.log')
        )
        $startupLogged = $false
        $verifiedLog = $null
        foreach ($candidate in $candidates) {
            if ((Test-Path -LiteralPath $candidate) -and
                ((Get-Content -LiteralPath $candidate -Raw) -match 'standalone started \(smoke test\)')) {
                $startupLogged = $true
                $verifiedLog = $candidate
                Write-Host "Verified standalone startup log at: $candidate"
                break
            }
        }
        if (-not $startupLogged) {
            throw "Public tray smoke test did not create a startup log in either AppData or TEMP"
        }

        # Run a normal clean exit and verify the active/rotated files vanish.
        $cleanupProcess = Start-Process -FilePath $exe -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
        if (-not $cleanupProcess.WaitForExit(15000)) { throw 'Public tray cleanup smoke test timed out' }
        if ($cleanupProcess.ExitCode -ne 0) { throw "Public tray cleanup smoke test failed: $($cleanupProcess.ExitCode)" }
        foreach ($candidate in $candidates) {
            if ($candidate -eq $verifiedLog -and ((Test-Path -LiteralPath $candidate) -or (Test-Path -LiteralPath "$candidate.1"))) {
                throw "Public tray clean exit left log files behind: $candidate"
            }
        }
        Write-Host "Verified clean shutdown removed standalone active/rotated logs."
    }
} finally { Pop-Location }
