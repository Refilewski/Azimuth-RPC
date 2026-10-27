#include <windows.h>
#include "fh2mod.h"
#include "LocationResolver.h"
#include <tlhelp32.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstddef>
#include <cctype>
#include <ctime>
#include <cstdio>
#include <cstring>
#include <string>
#include <type_traits>
#include <vector>

namespace
{

constexpr std::uint32_t kExpectedTimestamp = 0x565501EDu;
constexpr std::uint32_t kExpectedImageSize = 0x03A18000u;
constexpr std::uint32_t kExpectedEntryRva = 0x00F18058u;
constexpr std::uint16_t kExpectedMachine = IMAGE_FILE_MACHINE_AMD64;
constexpr std::uint16_t kExpectedOptionalMagic = IMAGE_NT_OPTIONAL_HDR64_MAGIC;

constexpr std::uintptr_t kAudioEngineRva = 0x0365D3D8ull;
constexpr std::uintptr_t kCForzaRva = 0x034ABA60ull;

constexpr std::uintptr_t kRegistryPointerOffset = 0x630ull;
constexpr std::uintptr_t kLoadingRecordOffset = 0x7A0ull;
constexpr std::uintptr_t kPauseRecordOffset = 0xC00ull;
constexpr std::uintptr_t kMapRecordOffset = 0xE80ull;

constexpr std::uintptr_t kGameManagerPointerOffset = 0x48ull;
constexpr std::uintptr_t kSimulationManagerPointerOffset = 0x50ull;
constexpr std::uintptr_t kGameManagerVtableRva = 0x02C5F8C8ull;
constexpr std::uintptr_t kSimulationManagerVtableRva = 0x02C5FD70ull;

constexpr std::size_t kCarIdOffset = 0x278;
constexpr std::size_t kCarIdBytes = 64;
constexpr char kDiscordApplicationId[] = "1445162899315363924";
constexpr char kVersion[] = "0.9.10";
constexpr char kPlatformVersion[] = "2.7.3";
constexpr DWORD kDiscordReconnectMs = 5000;
constexpr DWORD kPresenceUpdateMs = 4000;
constexpr LONGLONG kMaxLogBytes = 2ll * 1024ll * 1024ll;

std::atomic<bool> g_started{false};
std::atomic<bool> g_stop{false};
const FH2ModApi* g_platformApi = nullptr;
FH2Mod* g_registeredMod = nullptr;

struct Vec3
{
    float x{};
    float y{};
    float z{};
};

enum class UiMode
{
    Unknown,
    PauseMenu,
    PhotoMode,
    WorldMap,
    HorizonHub,
    Garage,
    Loading,
};

struct Snapshot
{
    bool valid{};
    std::uintptr_t gameBase{};
    UiMode uiMode{UiMode::Unknown};
    bool inWorld{};
    std::string carRaw;
    std::string carDisplay;
    float speedKmh{};
    bool hasSpeed{};
    Vec3 position{};
    bool hasPosition{};
};

struct IpcFrame
{
    std::int32_t opcode{};
    std::string payload;
};

std::wstring LogPath()
{
    wchar_t localAppData[32768]{};
    const DWORD length = GetEnvironmentVariableW(
        L"LOCALAPPDATA",
        localAppData,
        static_cast<DWORD>(_countof(localAppData)));

    if (length > 0 && length < _countof(localAppData))
    {
        std::wstring appDirectory(localAppData, length);
        appDirectory += L"\\AzimuthRPC";
        CreateDirectoryW(appDirectory.c_str(), nullptr);

        std::wstring logDirectory = appDirectory + L"\\logs";
        CreateDirectoryW(logDirectory.c_str(), nullptr);
        return logDirectory + L"\\AzimuthRPC.log";
    }

    return L"AzimuthRPC.log";
}

void AppendLog(const std::string& message)
{
    static const std::wstring path = LogPath();

    auto openLog = [&]()
    {
        return CreateFileW(
            path.c_str(),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
    };

    HANDLE file = openLog();
    if (file == INVALID_HANDLE_VALUE)
        return;

    LARGE_INTEGER size{};
    if (GetFileSizeEx(file, &size) != FALSE && size.QuadPart > kMaxLogBytes)
    {
        CloseHandle(file);
        const std::wstring rotated = path + L".1";
        DeleteFileW(rotated.c_str());
        MoveFileExW(path.c_str(), rotated.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
        file = openLog();
        if (file == INVALID_HANDLE_VALUE)
            return;
    }

    LARGE_INTEGER zero{};
    SetFilePointerEx(file, zero, nullptr, FILE_END);

    SYSTEMTIME now{};
    GetLocalTime(&now);
    char prefix[64]{};
    std::snprintf(
        prefix,
        sizeof(prefix),
        "[%04u-%02u-%02u %02u:%02u:%02u.%03u] ",
        static_cast<unsigned int>(now.wYear),
        static_cast<unsigned int>(now.wMonth),
        static_cast<unsigned int>(now.wDay),
        static_cast<unsigned int>(now.wHour),
        static_cast<unsigned int>(now.wMinute),
        static_cast<unsigned int>(now.wSecond),
        static_cast<unsigned int>(now.wMilliseconds));

    std::string line = prefix;
    line += message;
    line += "\r\n";

    DWORD written = 0;
    WriteFile(file, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
    CloseHandle(file);
}

void Trace(const std::string& message)
{
    const std::string debug = "[AzimuthRPC] " + message + "\n";
    OutputDebugStringA(debug.c_str());
    AppendLog(message);

    if (g_platformApi != nullptr && g_registeredMod != nullptr && g_platformApi->log != nullptr)
        g_platformApi->log(g_registeredMod, message.c_str());
}

std::string WideToUtf8(const wchar_t* value)
{
    if (value == nullptr || *value == L'\0')
        return {};

    const int required = WideCharToMultiByte(CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (required <= 1)
        return {};

    std::vector<char> buffer(static_cast<std::size_t>(required));
    if (WideCharToMultiByte(CP_UTF8, 0, value, -1, buffer.data(), required, nullptr, nullptr) <= 0)
        return {};

    return std::string(buffer.data());
}

void LogLoadedModules()
{
    HANDLE snapshot = CreateToolhelp32Snapshot(
        TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32,
        GetCurrentProcessId());
    if (snapshot == INVALID_HANDLE_VALUE)
    {
        Trace("Loaded modules: enumeration failed");
        return;
    }

    MODULEENTRY32W module{};
    module.dwSize = sizeof(module);
    std::string modules;

    if (Module32FirstW(snapshot, &module) != FALSE)
    {
        do
        {
            const std::string name = WideToUtf8(module.szModule);
            if (!name.empty())
            {
                if (!modules.empty())
                    modules += ", ";
                modules += name;
            }
        } while (Module32NextW(snapshot, &module) != FALSE);
    }

    CloseHandle(snapshot);
    Trace(modules.empty() ? "Loaded modules: none enumerated" : "Loaded modules: " + modules);
}

bool ReadBytes(const std::uintptr_t address, void* destination, const std::size_t size)
{
    if (address < 0x10000ull || destination == nullptr || size == 0)
        return false;

    SIZE_T bytesRead = 0;
    return ReadProcessMemory(
               GetCurrentProcess(),
               reinterpret_cast<LPCVOID>(address),
               destination,
               size,
               &bytesRead) != FALSE &&
           bytesRead == size;
}

template <typename T>
bool ReadValue(const std::uintptr_t address, T& value)
{
    static_assert(std::is_trivially_copyable_v<T>);
    return ReadBytes(address, &value, sizeof(T));
}

bool IsPointer(const std::uintptr_t value)
{
    return value >= 0x10000ull &&
           value <= 0x00007FFFFFFFE000ull &&
           (value & 7ull) == 0;
}

bool IsSupportedGameImage(const std::uintptr_t base)
{
    IMAGE_DOS_HEADER dos{};
    if (!ReadValue(base, dos) || dos.e_magic != IMAGE_DOS_SIGNATURE)
        return false;
    if (dos.e_lfanew <= 0 || dos.e_lfanew > 0x2000)
        return false;

    IMAGE_NT_HEADERS64 nt{};
    if (!ReadValue(base + static_cast<std::uintptr_t>(dos.e_lfanew), nt))
        return false;

    return nt.Signature == IMAGE_NT_SIGNATURE &&
           nt.FileHeader.Machine == kExpectedMachine &&
           nt.FileHeader.TimeDateStamp == kExpectedTimestamp &&
           nt.OptionalHeader.Magic == kExpectedOptionalMagic &&
           nt.OptionalHeader.SizeOfImage == kExpectedImageSize &&
           nt.OptionalHeader.AddressOfEntryPoint == kExpectedEntryRva;
}

std::uintptr_t GetGameBaseFromHost()
{
    if (g_platformApi == nullptr || g_platformApi->game_base == nullptr)
        return 0;

    const std::uintptr_t base = reinterpret_cast<std::uintptr_t>(g_platformApi->game_base());
    return IsSupportedGameImage(base) ? base : 0;
}

std::int32_t ReadI32(const std::uint8_t* data, const std::size_t offset)
{
    std::int32_t value = 0;
    std::memcpy(&value, data + offset, sizeof(value));
    return value;
}

float ReadF32(const std::uint8_t* data, const std::size_t offset)
{
    float value = 0.0f;
    std::memcpy(&value, data + offset, sizeof(value));
    return value;
}

Vec3 ReadVec3(const std::uint8_t* data, const std::size_t offset)
{
    return {ReadF32(data, offset), ReadF32(data, offset + 4), ReadF32(data, offset + 8)};
}

bool FiniteVec(const Vec3& value, const float bound)
{
    return std::isfinite(value.x) &&
           std::isfinite(value.y) &&
           std::isfinite(value.z) &&
           std::fabs(value.x) <= bound &&
           std::fabs(value.y) <= bound &&
           std::fabs(value.z) <= bound;
}

float Length(const Vec3& value)
{
    return std::sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
}

bool StartsWith(const std::string& value, const char* prefix)
{
    const std::size_t length = std::strlen(prefix);
    return value.size() >= length && value.compare(0, length, prefix) == 0;
}

std::string FriendlyCarName(const std::string& raw)
{
    if (raw == "LAM_Huracan_15")
        return "Lamborghini Huracan LP 610-4";
    if (raw == "TOY_SupraRZ_98")
        return "Toyota Supra RZ";

    struct Brand
    {
        const char* code;
        const char* name;
    };

    static constexpr Brand brands[] = {
        {"LAM_", "Lamborghini "},
        {"FER_", "Ferrari "},
        {"FOR_", "Ford "},
        {"BMW_", "BMW "},
        {"AUD_", "Audi "},
        {"CHE_", "Chevrolet "},
        {"DOD_", "Dodge "},
        {"NIS_", "Nissan "},
        {"HON_", "Honda "},
        {"MAZ_", "Mazda "},
        {"MCL_", "McLaren "},
        {"MER_", "Mercedes-Benz "},
        {"POR_", "Porsche "},
        {"SUB_", "Subaru "},
        {"TOY_", "Toyota "},
        {"VOL_", "Volvo "},
        {"VW_", "Volkswagen "},
        {"AST_", "Aston Martin "},
        {"ALF_", "Alfa Romeo "},
        {"BUG_", "Bugatti "},
        {"JAG_", "Jaguar "},
        {"LOT_", "Lotus "},
        {"MAS_", "Maserati "},
        {"RUF_", "RUF "},
        {"TES_", "Tesla "},
        {"LEX_", "Lexus "},
        {"MITS_", "Mitsubishi "},
        {"PEU_", "Peugeot "},
        {"REN_", "Renault "},
        {"MIN_", "MINI "},
    };

    std::string model = raw;
    std::string prefix;

    for (const Brand& brand : brands)
    {
        if (StartsWith(raw, brand.code))
        {
            prefix = brand.name;
            model = raw.substr(std::strlen(brand.code));
            break;
        }
    }

    std::replace(model.begin(), model.end(), '_', ' ');

    if (model.size() >= 3 &&
        model[model.size() - 3] == ' ' &&
        std::isdigit(static_cast<unsigned char>(model[model.size() - 2])) != 0 &&
        std::isdigit(static_cast<unsigned char>(model[model.size() - 1])) != 0)
    {
        model.resize(model.size() - 3);
    }

    return prefix.empty() ? model : prefix + model;
}

std::string HexBytes(const std::uint8_t* data, const std::size_t size)
{
    std::string result;
    result.reserve(size * 3);

    for (std::size_t i = 0; i < size; ++i)
    {
        char byte[4]{};
        std::snprintf(byte, sizeof(byte), "%02X", static_cast<unsigned int>(data[i]));
        if (i != 0)
            result.push_back(' ');
        result += byte;
    }

    return result;
}

std::string AsciiBytes(const std::uint8_t* data, const std::size_t size)
{
    std::string result;
    result.reserve(size);

    for (std::size_t i = 0; i < size; ++i)
    {
        const unsigned char c = data[i];
        result.push_back(c >= 0x20 && c <= 0x7E ? static_cast<char>(c) : '.');
    }

    return result;
}

bool ReadCarId(const std::uint8_t* raw, std::string& value, std::string& reason, const std::size_t offset = kCarIdOffset)
{
    value.clear();
    reason.clear();

    std::size_t length = 0;
    while (length < kCarIdBytes && raw[offset + length] != 0)
        ++length;

    if (length == 0)
    {
        reason = "empty";
        return false;
    }
    if (length < 5)
    {
        reason = "too-short";
        return false;
    }
    if (length >= kCarIdBytes)
    {
        reason = "unterminated";
        return false;
    }

    for (std::size_t i = 0; i < length; ++i)
    {
        const unsigned char c = raw[offset + i];
        if (!((c >= 'A' && c <= 'Z') ||
              (c >= 'a' && c <= 'z') ||
              (c >= '0' && c <= '9') ||
              c == '_'))
        {
            char detail[64]{};
            std::snprintf(
                detail,
                sizeof(detail),
                "invalid-char@%zu=0x%02X",
                i,
                static_cast<unsigned int>(c));
            reason = detail;
            return false;
        }
    }

    value.assign(reinterpret_cast<const char*>(raw + offset), length);
    if (value.find('_') == std::string::npos)
    {
        reason = "missing-underscore";
        value.clear();
        return false;
    }

    reason = "ok";
    return true;
}

bool LooksLikeFallbackCarId(const std::string& id)
{
    const auto first = id.find('_');
    const auto last = id.rfind('_');
    if (first < 2 || first > 5 || last == first || last < first + 2)
        return false;
    for (std::size_t i = 0; i < first; ++i)
        if (id[i] < 'A' || id[i] > 'Z')
            return false;
    const auto length = id.size() - last - 1;
    if (length != 2 && length != 4)
        return false;
    for (std::size_t i = last + 1; i < id.size(); ++i)
        if (id[i] < '0' || id[i] > '9')
            return false;
    return true;
}

// Recover only a single plausible year-suffixed car ID inside the known object.
bool FindFallbackCar(const std::uintptr_t audio, std::string& car, std::size_t& offset)
{
    std::array<std::uint8_t, 0x600> buffer{};
    if (!ReadBytes(audio, buffer.data(), buffer.size()))
        return false;
    std::string found;
    std::size_t foundAt = 0;
    for (std::size_t at = 0x200; at <= 0x500; ++at)
    {
        if (at > 0 && buffer[at - 1] != 0)
            continue;
        std::string candidate, reason;
        if (!ReadCarId(buffer.data(), candidate, reason, at) || !LooksLikeFallbackCarId(candidate))
            continue;
        if (!found.empty() && found != candidate)
            return false;
        if (found.empty())
        {
            found = candidate;
            foundAt = at;
        }
    }
    if (found.empty()) return false;
    car = found;
    offset = foundAt;
    return true;
}

bool BytesEqual(const std::uint8_t* data, const std::size_t offset, const char* text)
{
    const std::size_t length = std::strlen(text) + 1;
    return std::memcmp(data + offset, text, length) == 0;
}

UiMode DecodeMixerModes(const std::uintptr_t registry)
{
    std::array<std::uint8_t, 29> loading{};
    std::array<std::uint8_t, 28> pause{};
    std::array<std::uint8_t, 32> map{};

    if (!ReadBytes(registry + kLoadingRecordOffset, loading.data(), loading.size()) ||
        !ReadBytes(registry + kPauseRecordOffset, pause.data(), pause.size()) ||
        !ReadBytes(registry + kMapRecordOffset, map.data(), map.size()))
    {
        return UiMode::Unknown;
    }

    const bool loadingShape =
        ReadI32(loading.data(), 0) == 0x3F800000 &&
        ReadI32(loading.data(), 12) == 1 &&
        BytesEqual(loading.data(), 16, "BASE_Loading");

    if (loadingShape && ReadI32(loading.data(), 4) == 0x3F800000)
        return UiMode::Loading;

    const bool pauseShape =
        ReadI32(pause.data(), 0) == 0x3F800000 &&
        ReadI32(pause.data(), 12) == 2 &&
        BytesEqual(pause.data(), 16, "BASE_Paused");

    const bool mapShape =
        ReadI32(map.data(), 0) == 0x3F800000 &&
        ReadI32(map.data(), 12) == 2 &&
        BytesEqual(map.data(), 16, "BASE_Map_Paused");

    if (!pauseShape || !mapShape)
        return UiMode::Unknown;

    const std::int32_t pauseActive = ReadI32(pause.data(), 4);
    const std::int32_t pauseSubmode = ReadI32(pause.data(), 8);
    const std::int32_t mapActive = ReadI32(map.data(), 4);
    const std::int32_t mapSubmode = ReadI32(map.data(), 8);

    if (pauseActive == 0 && pauseSubmode == 0 &&
        mapActive == 0x3F800000 && mapSubmode == 1)
    {
        return UiMode::WorldMap;
    }

    if (mapActive == 0 && mapSubmode == 0 && pauseActive == 0x3F800000)
    {
        if (pauseSubmode == 1)
            return UiMode::PauseMenu;
        if (pauseSubmode == 2)
            return UiMode::PhotoMode;
    }

    return UiMode::Unknown;
}

UiMode ReadUiMode(
    const std::uintptr_t gameBase,
    const std::uintptr_t audio,
    const std::array<std::uint8_t, 0x2C0>& audioRaw)
{
    bool frontendValid = false;
    bool hubFamily = false;

    constexpr std::int32_t minus80Bits = static_cast<std::int32_t>(0xC2A00000u);
    const bool frontendGuard =
        ReadI32(audioRaw.data(), 0x30) == 0 &&
        ReadI32(audioRaw.data(), 0x34) == 0 &&
        ReadI32(audioRaw.data(), 0x3C) == minus80Bits &&
        ReadI32(audioRaw.data(), 0x40) == minus80Bits &&
        ReadI32(audioRaw.data(), 0x44) == minus80Bits;

    if (frontendGuard)
    {
        const std::int32_t marker = ReadI32(audioRaw.data(), 0x38);
        if (marker == 0 || marker == minus80Bits)
        {
            frontendValid = true;
            hubFamily = marker == 0;
        }
    }

    std::uintptr_t cforza = 0;
    if (!ReadValue(gameBase + kCForzaRva, cforza) || !IsPointer(cforza))
        cforza = 0;

    bool flowValid = false;
    std::int32_t flowPrimary = 0;
    std::int32_t flowSecondary = 0;

    if (cforza != 0)
    {
        std::uintptr_t simulationManager = 0;
        if (ReadValue(cforza + kSimulationManagerPointerOffset, simulationManager) &&
            IsPointer(simulationManager))
        {
            std::array<std::uint8_t, 0x28> raw{};
            if (ReadBytes(simulationManager, raw.data(), raw.size()))
            {
                std::uintptr_t vtable = 0;
                std::memcpy(&vtable, raw.data(), sizeof(vtable));
                flowPrimary = ReadI32(raw.data(), 0x10);
                flowSecondary = ReadI32(raw.data(), 0x20);
                flowValid =
                    vtable == gameBase + kSimulationManagerVtableRva &&
                    ReadI32(raw.data(), 0x18) == 1 &&
                    ReadI32(raw.data(), 0x24) == 5;
            }
        }
    }

    if (frontendValid && hubFamily && flowValid)
    {
        if (flowPrimary == 3 && flowSecondary >= 0 && flowSecondary <= 0x40)
            return UiMode::Garage;
        if (flowPrimary == 2 && flowSecondary == 0)
            return UiMode::HorizonHub;
    }

    if (cforza != 0)
    {
        std::uintptr_t gameManager = 0;
        if (ReadValue(cforza + kGameManagerPointerOffset, gameManager) &&
            IsPointer(gameManager))
        {
            std::array<std::uint8_t, 0x40> raw{};
            if (ReadBytes(gameManager, raw.data(), raw.size()))
            {
                std::uintptr_t vtable = 0;
                std::memcpy(&vtable, raw.data(), sizeof(vtable));

                const bool guards =
                    vtable == gameBase + kGameManagerVtableRva &&
                    raw[0x28] == 0 &&
                    raw[0x29] == 1 &&
                    raw[0x2A] == 1 &&
                    raw[0x2B] == 1 &&
                    raw[0x2C] == 1 &&
                    raw[0x2F] == 0;

                if (guards && raw[0x2D] == 0 && raw[0x2E] == 0)
                    return UiMode::Loading;
            }
        }
    }

    std::uintptr_t registry = 0;
    if (ReadValue(audio + kRegistryPointerOffset, registry) && IsPointer(registry))
        return DecodeMixerModes(registry);

    return UiMode::Unknown;
}

const char* UiModeName(const UiMode mode)
{
    switch (mode)
    {
    case UiMode::PauseMenu:
        return "PauseMenu";
    case UiMode::PhotoMode:
        return "PhotoMode";
    case UiMode::WorldMap:
        return "WorldMap";
    case UiMode::HorizonHub:
        return "HorizonHub";
    case UiMode::Garage:
        return "Garage";
    case UiMode::Loading:
        return "Loading";
    case UiMode::Unknown:
    default:
        return "Unknown";
    }
}

Snapshot CaptureSnapshot(const std::uintptr_t gameBase)
{
    Snapshot snapshot{};
    snapshot.gameBase = gameBase;

    std::uintptr_t audio = 0;
    if (!ReadValue(gameBase + kAudioEngineRva, audio) || !IsPointer(audio))
        return snapshot;

    std::array<std::uint8_t, 0x2C0> raw{};
    if (!ReadBytes(audio, raw.data(), raw.size()))
        return snapshot;

    std::string car;
    std::string carReason;
    bool carParsed = ReadCarId(raw.data(), car, carReason);
    if (!carParsed)
    {
        std::size_t recoveredAt = 0;
        if (FindFallbackCar(audio, car, recoveredAt))
        {
            carParsed = true;
            char status[80]{};
            std::snprintf(status, sizeof(status), "fallback@0x%zX", recoveredAt);
            carReason = status;
        }
    }
    if (carParsed)
    {
        snapshot.carRaw = car;
        snapshot.carDisplay = FriendlyCarName(car);
    }

    const float mph = ReadF32(raw.data(), 0x90);
    if (std::isfinite(mph) && mph >= 0.0f && mph <= 400.0f)
    {
        snapshot.speedKmh = mph * 1.609344f;
        snapshot.hasSpeed = true;
    }

    const Vec3 position = ReadVec3(raw.data(), 0xA0);
    const Vec3 mirror = ReadVec3(raw.data(), 0x190);
    const Vec3 velocity = ReadVec3(raw.data(), 0x170);

    const bool positionValid = FiniteVec(position, 100000.0f);
    const bool mirrorValid = FiniteVec(mirror, 100000.0f);

    if (positionValid)
    {
        snapshot.position = position;
        snapshot.hasPosition = true;
    }

    const bool mirrorsMatch =
        positionValid &&
        mirrorValid &&
        std::fabs(position.x - mirror.x) <= 1.5f &&
        std::fabs(position.y - mirror.y) <= 1.5f &&
        std::fabs(position.z - mirror.z) <= 1.5f;

    snapshot.uiMode = ReadUiMode(gameBase, audio, raw);

    if (snapshot.uiMode == UiMode::Unknown &&
        !snapshot.carRaw.empty() &&
        mirrorsMatch &&
        snapshot.hasSpeed &&
        FiniteVec(velocity, 500.0f))
    {
        const float velocityKmh = Length(velocity) * 3.6f;
        if (std::isfinite(velocityKmh) && velocityKmh <= 1000.0f)
        {
            snapshot.inWorld =
                (snapshot.speedKmh <= 3.0f && velocityKmh <= 5.0f) ||
                std::fabs(snapshot.speedKmh - velocityKmh) <=
                    std::max(10.0f, snapshot.speedKmh * 0.15f);
        }
    }

    const std::uint8_t* carBytes = raw.data() + kCarIdOffset;
    const std::string carHex = HexBytes(carBytes, kCarIdBytes);
    const std::string diagnosticKey = carReason + "|" + carHex;
    static std::string lastCarDiagnostic;

    if (diagnosticKey != lastCarDiagnostic)
    {
        char header[320]{};
        std::snprintf(
            header,
            sizeof(header),
            "Car probe: parse=%s audio=0x%llX offset=0x%zX ui=%s speed=%s%.1f position=%s(%.2f,%.2f,%.2f)",
            carReason.c_str(),
            static_cast<unsigned long long>(audio),
            kCarIdOffset,
            UiModeName(snapshot.uiMode),
            snapshot.hasSpeed ? "" : "invalid/",
            snapshot.hasSpeed ? snapshot.speedKmh : 0.0f,
            snapshot.hasPosition ? "" : "invalid/",
            snapshot.hasPosition ? snapshot.position.x : 0.0f,
            snapshot.hasPosition ? snapshot.position.y : 0.0f,
            snapshot.hasPosition ? snapshot.position.z : 0.0f);

        std::string diagnostic = header;
        if (carParsed)
            diagnostic += " id=\"" + car + "\"";
        diagnostic += " ascii=\"" + AsciiBytes(carBytes, kCarIdBytes) + "\"";
        diagnostic += " hex=" + carHex;
        Trace(diagnostic);
        lastCarDiagnostic = diagnosticKey;
    }

    snapshot.valid = true;
    return snapshot;
}

std::string JsonEscape(const std::string& value)
{
    std::string result;
    result.reserve(value.size() + 16);

    for (const unsigned char c : value)
    {
        switch (c)
        {
        case '"':
            result += "\\\"";
            break;
        case '\\':
            result += "\\\\";
            break;
        case '\b':
            result += "\\b";
            break;
        case '\f':
            result += "\\f";
            break;
        case '\n':
            result += "\\n";
            break;
        case '\r':
            result += "\\r";
            break;
        case '\t':
            result += "\\t";
            break;
        default:
            if (c < 0x20)
            {
                char buffer[7]{};
                std::snprintf(buffer, sizeof(buffer), "\\u%04X", static_cast<unsigned int>(c));
                result += buffer;
            }
            else
            {
                result.push_back(static_cast<char>(c));
            }
            break;
        }
    }

    return result;
}

bool WriteAll(const HANDLE pipe, const void* data, const DWORD size)
{
    const auto* cursor = static_cast<const std::uint8_t*>(data);
    DWORD remaining = size;

    while (remaining > 0)
    {
        DWORD written = 0;
        if (WriteFile(pipe, cursor, remaining, &written, nullptr) == FALSE || written == 0)
            return false;

        cursor += written;
        remaining -= written;
    }

    return true;
}

bool ReadAll(const HANDLE pipe, void* data, const DWORD size)
{
    auto* cursor = static_cast<std::uint8_t*>(data);
    DWORD remaining = size;

    while (remaining > 0)
    {
        DWORD read = 0;
        if (ReadFile(pipe, cursor, remaining, &read, nullptr) == FALSE || read == 0)
            return false;

        cursor += read;
        remaining -= read;
    }

    return true;
}

bool SendFrame(const HANDLE pipe, const std::int32_t opcode, const std::string& payload)
{
    const std::int32_t length = static_cast<std::int32_t>(payload.size());
    std::array<std::int32_t, 2> header{opcode, length};

    return WriteAll(pipe, header.data(), static_cast<DWORD>(sizeof(header))) &&
           (payload.empty() ||
            WriteAll(pipe, payload.data(), static_cast<DWORD>(payload.size())));
}

bool WaitForPipeBytes(const HANDLE pipe, const DWORD timeoutMs)
{
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;

    while (!g_stop.load(std::memory_order_relaxed) && GetTickCount64() < deadline)
    {
        DWORD available = 0;
        if (PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr) == FALSE)
            return false;

        if (available >= 8)
            return true;

        Sleep(10);
    }

    return false;
}

bool ReadFrame(const HANDLE pipe, IpcFrame& frame)
{
    std::array<std::int32_t, 2> header{};
    if (!ReadAll(pipe, header.data(), static_cast<DWORD>(sizeof(header))))
        return false;

    if (header[1] < 0 || header[1] > 16 * 1024 * 1024)
        return false;

    frame.opcode = header[0];
    frame.payload.clear();

    if (header[1] == 0)
        return true;

    frame.payload.resize(static_cast<std::size_t>(header[1]));
    return ReadAll(
        pipe,
        frame.payload.data(),
        static_cast<DWORD>(frame.payload.size()));
}

HANDLE OpenDiscordPipe()
{
    for (int index = 0; index < 10; ++index)
    {
        wchar_t path[64]{};
        _snwprintf_s(path, _countof(path), _TRUNCATE, L"\\\\.\\pipe\\discord-ipc-%d", index);

        if (WaitNamedPipeW(path, 100) == FALSE)
            continue;

        HANDLE pipe = CreateFileW(
            path,
            GENERIC_READ | GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            0,
            nullptr);

        if (pipe != INVALID_HANDLE_VALUE)
            return pipe;
    }

    return INVALID_HANDLE_VALUE;
}

bool ConnectDiscord(HANDLE& pipe)
{
    pipe = OpenDiscordPipe();
    if (pipe == INVALID_HANDLE_VALUE)
        return false;

    const std::string handshake =
        std::string("{\"v\":1,\"client_id\":\"") +
        kDiscordApplicationId +
        "\"}";

    if (!SendFrame(pipe, 0, handshake))
    {
        CloseHandle(pipe);
        pipe = INVALID_HANDLE_VALUE;
        return false;
    }

    const ULONGLONG deadline = GetTickCount64() + 5000;

    while (!g_stop.load(std::memory_order_relaxed) && GetTickCount64() < deadline)
    {
        if (!WaitForPipeBytes(pipe, 250))
            continue;

        IpcFrame frame{};
        if (!ReadFrame(pipe, frame))
            break;

        if (frame.opcode == 3)
        {
            if (!SendFrame(pipe, 4, frame.payload))
                break;
            continue;
        }

        if (frame.opcode == 2)
            break;

        if (frame.opcode == 1 &&
            frame.payload.find("\"READY\"") != std::string::npos)
        {
            return true;
        }

        if (frame.opcode == 1 &&
            frame.payload.find("\"ERROR\"") != std::string::npos)
        {
            break;
        }
    }

    CloseHandle(pipe);
    pipe = INVALID_HANDLE_VALUE;
    return false;
}

bool PumpDiscord(const HANDLE pipe)
{
    for (;;)
    {
        DWORD available = 0;
        if (PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr) == FALSE)
            return false;

        if (available < 8)
            return true;

        IpcFrame frame{};
        if (!ReadFrame(pipe, frame))
            return false;

        if (frame.opcode == 3)
        {
            if (!SendFrame(pipe, 4, frame.payload))
                return false;
        }
        else if (frame.opcode == 2)
        {
            return false;
        }
    }
}

std::string BuildDetails(const Snapshot& snapshot)
{
    switch (snapshot.uiMode)
    {
    case UiMode::PauseMenu:
        return "Paused";
    case UiMode::PhotoMode:
        return "Photo Mode";
    case UiMode::WorldMap:
        return "Browsing World Map";
    case UiMode::Garage:
        return "Garage";
    case UiMode::Loading:
        return "Loading Race";
    case UiMode::HorizonHub:
        return "Horizon Hub";
    case UiMode::Unknown:
    default:
        if (!snapshot.inWorld)
            return "Forza Horizon 2";
        if (snapshot.hasPosition)
        {
            if (const char* location = ResolveFh2MainlandLocation(snapshot.position.x, snapshot.position.z))
                return location;
        }
        return "Exploring Southern Europe";
    }
}

std::string BuildState(const Snapshot& snapshot)
{
    // Keep the second Discord line empty during race loading, even when a
    // previously selected car is still present in the game memory snapshot.
    if (snapshot.uiMode == UiMode::Loading)
        return {};

    const bool carContext =
        snapshot.inWorld ||
        snapshot.uiMode == UiMode::PauseMenu ||
        snapshot.uiMode == UiMode::PhotoMode ||
        snapshot.uiMode == UiMode::WorldMap ||
        snapshot.uiMode == UiMode::HorizonHub ||
        snapshot.uiMode == UiMode::Garage ||
        snapshot.uiMode == UiMode::Loading;

    if (carContext && !snapshot.carDisplay.empty())
    {
        std::string state = snapshot.carDisplay;

        if (snapshot.inWorld && snapshot.hasSpeed)
        {
            const long rounded =
                std::lround(snapshot.speedKmh / 5.0f) * 5L;
            state += " | " + std::to_string(std::max(0L, rounded)) + " km/h";
        }

        return state;
    }

    return {};
}

bool SendActivity(
    const HANDLE pipe,
    const Snapshot& snapshot,
    const std::int64_t sessionStart,
    std::uint64_t& nonce)
{
    const std::string details = JsonEscape(BuildDetails(snapshot));
    const std::string rawState = BuildState(snapshot);
    const std::string state = JsonEscape(rawState);

    std::string json;
    json.reserve(512);
    json += "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":";
    json += std::to_string(GetCurrentProcessId());
    json += ",\"activity\":{\"type\":0,\"details\":\"";
    json += details;
    json += "\"";
    if (!rawState.empty())
    {
        json += ",\"state\":\"";
        json += state;
        json += "\"";
    }
    json += ",\"timestamps\":{\"start\":";
    json += std::to_string(sessionStart);
    json += "}}},\"nonce\":\"";
    json += std::to_string(++nonce);
    json += "\"}";

    return SendFrame(pipe, 1, json);
}

std::string Fingerprint(const Snapshot& snapshot)
{
    return BuildDetails(snapshot) + "\n" + BuildState(snapshot);
}

DWORD WINAPI RpcThread(LPVOID)
{
    Sleep(1000);

    std::uintptr_t gameBase = 0;
    HANDLE discordPipe = INVALID_HANDLE_VALUE;
    ULONGLONG lastDiscordAttempt = 0;
    ULONGLONG lastPresenceUpdate = 0;
    std::uint64_t nonce = 0;
    std::string lastFingerprint;
    const std::int64_t sessionStart =
        static_cast<std::int64_t>(std::time(nullptr));

    Trace(std::string("AzimuthRPC ") + kVersion + " FH2 Mod Platform native worker started");
    Trace("Log file: %LOCALAPPDATA%\\AzimuthRPC\\logs\\AzimuthRPC.log");
    LogLoadedModules();

    while (!g_stop.load(std::memory_order_relaxed))
    {
        if (gameBase == 0 || !IsSupportedGameImage(gameBase))
        {
            gameBase = GetGameBaseFromHost();
            if (gameBase == 0)
            {
                Sleep(1000);
                continue;
            }

            char message[128]{};
            std::snprintf(
                message,
                sizeof(message),
                "FH2 Mod Platform game module acquired at 0x%llX",
                static_cast<unsigned long long>(gameBase));
            Trace(message);
        }

        const ULONGLONG now = GetTickCount64();

        if (discordPipe == INVALID_HANDLE_VALUE)
        {
            if (now - lastDiscordAttempt >= kDiscordReconnectMs)
            {
                lastDiscordAttempt = now;
                if (ConnectDiscord(discordPipe))
                {
                    lastFingerprint.clear();
                    lastPresenceUpdate = 0;
                    Trace("Discord IPC connected");
                }
            }

            Sleep(100);
            continue;
        }

        if (!PumpDiscord(discordPipe))
        {
            CloseHandle(discordPipe);
            discordPipe = INVALID_HANDLE_VALUE;
            lastDiscordAttempt = now;
            lastFingerprint.clear();
            Trace("Discord IPC disconnected");
            continue;
        }

        if (now - lastPresenceUpdate >= kPresenceUpdateMs)
        {
            lastPresenceUpdate = now;
            const Snapshot snapshot = CaptureSnapshot(gameBase);

            if (snapshot.valid)
            {
                const std::string fingerprint = Fingerprint(snapshot);
                if (fingerprint != lastFingerprint)
                {
                    const std::string state = BuildState(snapshot);
                    Trace(
                        "Presence update: details=\"" + BuildDetails(snapshot) +
                        "\" state=\"" + (state.empty() ? std::string("<omitted>") : state) + "\"");

                    if (!SendActivity(discordPipe, snapshot, sessionStart, nonce))
                    {
                        CloseHandle(discordPipe);
                        discordPipe = INVALID_HANDLE_VALUE;
                        lastDiscordAttempt = now;
                        lastFingerprint.clear();
                        Trace("Discord activity send failed; reconnecting");
                        continue;
                    }

                    lastFingerprint = fingerprint;
                }
            }
        }

        Sleep(50);
    }

    if (discordPipe != INVALID_HANDLE_VALUE)
        CloseHandle(discordPipe);

    Trace("FH2 Mod Platform native worker stopped");
    return 0;
}
} // namespace

FH2MOD_EXPORT int fh2mod_init(const FH2ModApi* api)
{
    Trace(std::string("AzimuthRPC ") + kVersion + " fh2mod_init entered");
    if (api == nullptr)
    {
        Trace("FH2 Mod Platform rejected: null API");
        return 1;
    }
    constexpr std::size_t requiredSize = offsetof(FH2ModApi, wait_for_game) + sizeof(api->wait_for_game);
    if (api->size < requiredSize || api->api_version < 1 ||
        api->register_mod == nullptr || api->log == nullptr ||
        api->game_base == nullptr || api->wait_for_game == nullptr)
    {
        Trace("FH2 Mod Platform rejected: incomplete API table");
        return 1;
    }
    bool expected = false;
    if (!g_started.compare_exchange_strong(expected, true))
        return 0;
    g_stop.store(false, std::memory_order_relaxed);
    g_platformApi = api;
    FH2ModInfo info{};
    info.size = sizeof(info);
    info.name = "AzimuthRPC";
    info.version = kVersion;
    info.github_repo = "Refilewski/Azimuth-RPC";
    info.platform_version = kPlatformVersion;
    info.check_updates = 1;
    info.description = "Discord Rich Presence for Forza Horizon 2";
    g_registeredMod = api->register_mod(&info);
    if (!g_registeredMod)
    {
        Trace("FH2 Mod Platform rejected: register_mod failed");
        g_platformApi = nullptr;
        g_started.store(false, std::memory_order_relaxed);
        return 1;
    }
    // Azimuth maps FH2 after platform initialization: worker must wait.
    Trace("Registered with FH2 Mod Platform; waiting for supported game image");
    HANDLE thread = CreateThread(nullptr, 0, RpcThread, nullptr, 0, nullptr);
    if (!thread)
    {
        Trace("Failed to start native RPC worker");
        g_registeredMod = nullptr;
        g_platformApi = nullptr;
        g_started.store(false, std::memory_order_relaxed);
        return 1;
    }
    CloseHandle(thread);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        // Signal only. Waiting here would run under the Windows loader lock.
        g_stop.store(true, std::memory_order_relaxed);
    }

    return TRUE;
}
