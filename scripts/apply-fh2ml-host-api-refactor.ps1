$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$utf8 = [Text.UTF8Encoding]::new($false)

function Read-Lf([string]$Path) {
    return ([IO.File]::ReadAllText($Path)).Replace("`r`n", "`n")
}

function Write-Utf8([string]$Path, [string]$Content) {
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Content.Replace("`r`n", "`n"), $utf8)
}

function Replace-Exact([string]$Text, [string]$Old, [string]$New, [string]$Label) {
    $textLf = $Text.Replace("`r`n", "`n")
    $oldLf = $Old.Replace("`r`n", "`n")
    $newLf = $New.Replace("`r`n", "`n")
    if (-not $textLf.Contains($oldLf)) {
        throw "Expected source block not found: $Label"
    }
    return $textLf.Replace($oldLf, $newLf)
}

$nativeDir = Join-Path $repoRoot 'native\FH2ML.AzimuthRPC'
$sourcePath = Join-Path $nativeDir 'AzimuthRpcMod.cpp'
$source = Read-Lf $sourcePath

$source = Replace-Exact $source @'
#include <windows.h>
#include "LocationResolver.h"
#include <tlhelp32.h>
'@ @'
#include <windows.h>
#include "FH2ML.h"
#include "LocationResolver.h"
#include <tlhelp32.h>
'@ 'FH2ML SDK include'

$source = Replace-Exact $source 'constexpr std::uintptr_t kPreferredGameBase = 0x140000000ull;' '' 'obsolete preferred game base'
$source = Replace-Exact $source 'constexpr char kVersion[] = "0.9.5";' 'constexpr char kVersion[] = "0.9.6";' 'native version'

$source = Replace-Exact $source @'
std::atomic<bool> g_started{false};
std::atomic<bool> g_stop{false};
'@ @'
std::atomic<bool> g_started{false};
std::atomic<bool> g_stop{false};
const FH2ML_HostAPI* g_hostApi = nullptr;
'@ 'FH2ML host API state'

$source = Replace-Exact $source @'
void Trace(const std::string& message)
{
    const std::string debug = "[AzimuthRPC] " + message + "\n";
    OutputDebugStringA(debug.c_str());
    AppendLog(message);
}
'@ @'
void Trace(const std::string& message)
{
    const std::string debug = "[AzimuthRPC] " + message + "\n";
    OutputDebugStringA(debug.c_str());
    AppendLog(message);

    if (g_hostApi != nullptr && g_hostApi->Log != nullptr)
        g_hostApi->Log("azimuth-rpc", message.c_str());
}
'@ 'host-backed logging'

$findPattern = '(?s)std::uintptr_t FindGameBase\(\)\n\{.*?\n\}\n\nstd::int32_t ReadI32'
$findReplacement = @'
std::uintptr_t GetGameBaseFromHost()
{
    if (g_hostApi == nullptr || g_hostApi->GetGameModule == nullptr)
        return 0;

    const std::uintptr_t base = g_hostApi->GetGameModule();
    return IsSupportedGameImage(base) ? base : 0;
}

std::int32_t ReadI32
'@
$findMatches = [regex]::Matches($source, $findPattern)
if ($findMatches.Count -ne 1) {
    throw "Expected exactly one FindGameBase implementation, found $($findMatches.Count)"
}
$source = [regex]::Replace($source, $findPattern, $findReplacement, 1)

$source = Replace-Exact $source 'gameBase = FindGameBase();' 'gameBase = GetGameBaseFromHost();' 'worker game-base lookup'
$source = Replace-Exact $source 'FH2 mapped image found at 0x%llX' 'FH2ML game module acquired at 0x%llX' 'worker game-base diagnostic'

$initPattern = '(?s)extern "C" __declspec\(dllexport\) int FH2ML_Initialize\(const void\* api\)\n\{.*?\n\}\n\nBOOL WINAPI DllMain'
$initReplacement = @'
extern "C" __declspec(dllexport) int FH2ML_Initialize(const FH2ML_HostAPI* api)
{
    // FH2ML ABI v1 has no hot-unload contract. Validate the complete v1 host
    // table up front, register this mod, then hand the worker the host-owned
    // mapped FH2 module instead of scanning the process address space.
    if (api == nullptr ||
        api->size < sizeof(FH2ML_HostAPI) ||
        api->version != FH2ML_API_VERSION ||
        api->Log == nullptr ||
        api->RegisterMod == nullptr ||
        api->GetGameModule == nullptr)
    {
        return 0;
    }

    bool expected = false;
    if (!g_started.compare_exchange_strong(expected, true))
        return 1;

    g_stop.store(false, std::memory_order_relaxed);

    if (api->RegisterMod("azimuth-rpc", kVersion) == 0)
    {
        api->Log("azimuth-rpc", "FH2ML RegisterMod rejected AzimuthRPC");
        g_started.store(false, std::memory_order_relaxed);
        return 0;
    }

    g_hostApi = api;
    const std::uintptr_t gameBase = GetGameBaseFromHost();
    if (gameBase == 0)
    {
        api->Log("azimuth-rpc", "FH2ML GetGameModule returned no supported FH2 1.0.0.64 image");
        g_hostApi = nullptr;
        g_started.store(false, std::memory_order_relaxed);
        return 0;
    }

    char registration[160]{};
    std::snprintf(
        registration,
        sizeof(registration),
        "Registered with FH2ML API v%u; game module=0x%llX",
        static_cast<unsigned int>(api->version),
        static_cast<unsigned long long>(gameBase));
    Trace(registration);

    HANDLE thread = CreateThread(nullptr, 0, RpcThread, nullptr, 0, nullptr);
    if (thread == nullptr)
    {
        Trace("Failed to start FH2ML native RPC worker");
        g_hostApi = nullptr;
        g_started.store(false, std::memory_order_relaxed);
        return 0;
    }

    CloseHandle(thread);
    return 1;
}

BOOL WINAPI DllMain
'@
$initMatches = [regex]::Matches($source, $initPattern)
if ($initMatches.Count -ne 1) {
    throw "Expected exactly one legacy FH2ML_Initialize implementation, found $($initMatches.Count)"
}
$source = [regex]::Replace($source, $initPattern, $initReplacement, 1)

if ($source.Contains('VirtualQuery(')) {
    throw 'Native source still contains VirtualQuery process scanning after FH2ML API refactor'
}
if ($source.Contains('(void)api;')) {
    throw 'Native source still ignores the FH2ML host API'
}
Write-Utf8 $sourcePath $source

$cmakePath = Join-Path $nativeDir 'CMakeLists.txt'
$cmake = Read-Lf $cmakePath
$cmake = Replace-Exact $cmake @'
add_library(AzimuthRPCMod SHARED AzimuthRpcMod.cpp LocationResolver.cpp)
target_compile_features(AzimuthRPCMod PRIVATE cxx_std_20)
'@ @'
add_library(AzimuthRPCMod SHARED AzimuthRpcMod.cpp LocationResolver.cpp)
target_include_directories(AzimuthRPCMod PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/fh2ml-sdk")
target_compile_features(AzimuthRPCMod PRIVATE cxx_std_20)
'@ 'FH2ML SDK include directory'
Write-Utf8 $cmakePath $cmake

$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$props = Read-Lf $propsPath
if (-not $props.Contains('0.9.5')) { throw 'Expected v0.9.5 in Directory.Build.props' }
Write-Utf8 $propsPath ($props.Replace('0.9.5', '0.9.6'))

$manifestPath = Join-Path $nativeDir 'mod.json'
$manifest = Read-Lf $manifestPath
$manifest = Replace-Exact $manifest '"version": "0.9.5"' '"version": "0.9.6"' 'native manifest version'
Write-Utf8 $manifestPath $manifest

$releaseNotes = @'
# AzimuthRPC v0.9.6

FH2ML SDK integration for the native mod.

- The FH2ML native DLL now consumes the official FH2ML Host API v1 instead of ignoring the initializer argument.
- Registers `azimuth-rpc` and its version through `RegisterMod` during native initialization.
- Mirrors native diagnostics into the FH2ML host log through `Log` while keeping the existing AzimuthRPC log.
- Uses `GetGameModule()` as the authoritative FH2 base address and no longer scans the process address space with `VirtualQuery`.
- Validates the FH2ML API table and the supported FH2 1.0.0.64 PE image before starting the RPC worker.
- Vendors the MIT-licensed FH2ML SDK header used to build the native DLL.
- Adds a CI contract check so the host-API integration cannot silently regress back to process scanning.

The standalone app behavior is unchanged apart from the shared version bump.

## Supported game build

Forza Horizon 2 Xbox One build 1.0.0.64 via Azimuth.

## Reporting issues

Logs are stored at `%LOCALAPPDATA%\AzimuthRPC\logs\AzimuthRPC.log`. If rotation occurs, the previous log is retained as `AzimuthRPC.log.1`. Native FH2ML diagnostics are also mirrored into the FH2ML log.
'@
Write-Utf8 (Join-Path $repoRoot 'RELEASE_NOTES.md') $releaseNotes

$sdkHeader = @'
// SPDX-License-Identifier: MIT
// FH2ML SDK header; see LICENSE.
#ifndef FH2ML_API_H
#define FH2ML_API_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#define FH2ML_API_VERSION 1u
typedef struct FH2ML_Patch {
    uint32_t size;
    const char *owner;
    const char *type;
    uintptr_t address;
    uint32_t length;
    const uint8_t *original;
    const uint8_t *replacement;
} FH2ML_Patch;
typedef struct FH2ML_ModInfo {
    uint32_t size;
    uint32_t loaded;
    uintptr_t module;
    char version[64];
} FH2ML_ModInfo;
typedef struct FH2ML_HostAPI {
    uint32_t size;
    uint32_t version;
    void (*Log)(const char *owner, const char *message);
    int (*RegisterMod)(const char *id, const char *version);
    int (*RegisterPatch)(const FH2ML_Patch *patch);
    int (*QueryMod)(const char *id, FH2ML_ModInfo *info);
    uintptr_t (*GetGameModule)(void);
} FH2ML_HostAPI;
/* Optional DLL export: int FH2ML_Initialize(const FH2ML_HostAPI *api).
   Return nonzero on success. Do not wait for other threads during startup.
   Strings are UTF-8 and copied on registration. No hot-unload contract. */
typedef int (*FH2ML_InitializeFn)(const FH2ML_HostAPI *api);
#ifdef __cplusplus
}
#endif
#endif
'@
Write-Utf8 (Join-Path $nativeDir 'fh2ml-sdk\FH2ML.h') $sdkHeader

$sdkLicense = @'
MIT License

Copyright (c) 2026 Refilewski

Permission is hereby granted, free of charge, to any person obtaining a copy
of the FH2ML SDK header files in this directory (FH2ML.h and
FH2ML_Adapter.h), to deal in those header files without restriction,
including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the header files,
and to permit persons to whom the header files are furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the header files.

THE HEADER FILES ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE HEADER FILES OR
THE USE OR OTHER DEALINGS IN THE HEADER FILES.

This license applies only to the SDK header files in this directory. It does
not license the FH2ML loader, runtime, manager, scripts, documentation, or
other repository contents.
'@
Write-Utf8 (Join-Path $nativeDir 'fh2ml-sdk\LICENSE') $sdkLicense

$contractTest = @'
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePath = Join-Path $root 'native\FH2ML.AzimuthRPC\AzimuthRpcMod.cpp'
$source = Get-Content $sourcePath -Raw

$required = @(
    '#include "FH2ML.h"',
    'FH2ML_Initialize(const FH2ML_HostAPI* api)',
    'api->RegisterMod("azimuth-rpc", kVersion)',
    'api->GetGameModule()',
    'g_hostApi->Log("azimuth-rpc", message.c_str())'
)

foreach ($needle in $required) {
    if (-not $source.Contains($needle)) {
        throw "Missing FH2ML host API contract marker: $needle"
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
'@
Write-Utf8 (Join-Path $repoRoot 'tests\native-fh2ml-api-contract.ps1') $contractTest

$workflowPath = Join-Path $repoRoot '.github\workflows\build-pr.yml'
$workflow = Read-Lf $workflowPath
$workflow = Replace-Exact $workflow @'
          ./scripts/audit-public-tree.ps1
          ./tests/public-history-tests.ps1
'@ @'
          ./scripts/audit-public-tree.ps1
          ./tests/public-history-tests.ps1
          ./tests/native-fh2ml-api-contract.ps1
'@ 'native FH2ML contract CI step'
Write-Utf8 $workflowPath $workflow

$readmePath = Join-Path $nativeDir 'README.md'
$readme = Read-Lf $readmePath
if (-not $readme.Contains('## FH2ML Host API integration')) {
    $readme = $readme.TrimEnd() + @'

## FH2ML Host API integration

The native DLL is built against the vendored FH2ML SDK v1 header in `fh2ml-sdk/`.
During `FH2ML_Initialize` it validates the host table, registers `azimuth-rpc`,
mirrors diagnostics through the FH2ML logger, and obtains the mapped FH2 image
from `GetGameModule()`. It does not scan the process address space for the game.

The vendored FH2ML SDK header is licensed separately under MIT; see
`fh2ml-sdk/LICENSE`.
'@ + "`n"
    Write-Utf8 $readmePath $readme
}

Write-Host 'FH2ML Host API refactor applied successfully.'
