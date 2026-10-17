$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePath = Join-Path $root 'native\FH2ML.AzimuthRPC\AzimuthRpcMod.cpp'
$source = Get-Content $sourcePath -Raw

$required = @(
    '#include "FH2ML.h"',
    'FH2ML_Initialize(const FH2ML_HostAPI* api)',
    'api->RegisterMod("azimuth-rpc", kVersion)',
    'g_hostApi->GetGameModule()',
    'g_hostApi->Log("azimuth-rpc", message.c_str())'
)

foreach ($needle in $required) {
    if (-not $source.Contains($needle)) {
        throw "Missing FH2ML host API contract marker: $needle"
    }
}

# Initialization diagnostics must be written even when FH2ML is incompatible.
$startIndex = $source.IndexOf('FH2ML_Initialize(const FH2ML_HostAPI* api)')
$initTraceIndex = $source.IndexOf('Trace(std::string("AzimuthRPC ") + kVersion + " FH2ML_Initialize entered")', $startIndex)
$firstGuardIndex = $source.IndexOf('if (api == nullptr)', $startIndex)
if ($startIndex -lt 0 -or $initTraceIndex -lt 0 -or $firstGuardIndex -lt 0 -or
    $initTraceIndex -gt $firstGuardIndex) {
    throw 'FH2ML initialization must log entry before checking the host API'
}
foreach ($diagnostic in @(
    'host API pointer is null',
    'host API table too small',
    'API version mismatch',
    'required Log, RegisterMod or GetGameModule callback missing',
    'GetGameModule returned no supported FH2'
)) {
    if (-not $source.Contains($diagnostic)) {
        throw "Missing native startup diagnostics: $diagnostic"
    }
}

if ($source.Contains('VirtualQuery(')) {
    throw 'Native FH2ML mod must use GetGameModule instead of VirtualQuery process scanning'
}
if ($source.Contains('(void)api;')) {
    throw 'Native FH2ML initializer must not ignore the host API'
}

$header = Get-Content (Join-Path $root 'native\FH2ML.AzimuthRPC\fh2ml-sdk\FH2ML.h') -Raw
if (-not $header.Contains('#define FH2ML_API_VERSION 1u') -or
    -not $header.Contains('uintptr_t (*GetGameModule)(void);')) {
    throw 'Vendored FH2ML SDK header does not expose the expected v1 contract'
}

Write-Host 'FH2ML native host API contract verified.'