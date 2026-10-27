$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Get-Content (Join-Path $root 'native\FH2ML.AzimuthRPC\AzimuthRpcMod.cpp') -Raw
$header = Get-Content (Join-Path $root 'native\FH2ML.AzimuthRPC\platform-sdk\fh2mod.h') -Raw
foreach ($needle in @('#include "fh2mod.h"', 'fh2mod_init(const FH2ModApi* api)',
    'api->register_mod(&info)', 'g_platformApi->game_base()', 'g_platformApi->log(g_registeredMod, message.c_str())',
    'FindFallbackCar(audio, car, recoveredAt)')) {
    if (-not $source.Contains($needle)) { throw "Missing platform contract: $needle" }
}
if ($source.Contains('FH2ML_Initialize') -or $source.Contains('g_hostApi')) { throw 'Legacy host ABI remains' }
if (-not $header.Contains('#define FH2MOD_API_VERSION 8')) { throw 'Wrong platform SDK header' }
$start = $source.IndexOf('fh2mod_init(const FH2ModApi* api)')
$trace = $source.IndexOf('fh2mod_init entered', $start)
$guard = $source.IndexOf('if (api == nullptr)', $start)
if ($start -lt 0 -or $trace -lt 0 -or $guard -lt 0 -or $trace -gt $guard) {
    throw 'The initializer must log entry before validating host API'
}
if ($source.Contains('VirtualQuery(')) { throw 'Use the platform game base, not process scanning' }
Write-Host 'FH2 Mod Platform API integration verified.'
