#include "CarObjectProbe.h"

#include <windows.h>
#include <cstdint>
#include <cstring>

namespace
{
constexpr std::uintptr_t kPlayerManagerRva = 0x034ABA60;
constexpr std::uintptr_t kGetLocalPlayerRva = 0x0021E0C0;
constexpr std::uintptr_t kGetPlayerCarRva = 0x002DA060;
constexpr std::uintptr_t kPlayerCarVtableRva = 0x02C5CB80;
constexpr std::uintptr_t kModelIdOffset = 0x118;
constexpr std::uintptr_t kReleaseSlot = 0x18;
constexpr std::uintptr_t kGameCodeLimitRva = 0x01DCC000;
constexpr std::uint32_t kMaximumPlausibleModelId = 10000;

using GetLocalPlayerFn = void* (*)(void*, int);
using GetPlayerCarFn = void* (*)(void*, void**);
using ReleaseFn = void (*)(void*);

bool ReadMemory(const std::uintptr_t address, void* destination, const SIZE_T bytes) noexcept
{
    if (address < 0x10000 || destination == nullptr || bytes == 0)
        return false;
    SIZE_T read = 0;
    return ReadProcessMemory(GetCurrentProcess(),
                             reinterpret_cast<LPCVOID>(address),
                             destination, bytes, &read) != FALSE &&
           read == bytes;
}

bool ValidPointer(const std::uintptr_t pointer) noexcept
{
    return pointer >= 0x10000 &&
           pointer <= 0x00007FFFFFFFE000ull &&
           (pointer & 7ull) == 0;
}

bool ReadPointer(const std::uintptr_t address, std::uintptr_t& value) noexcept
{
    value = 0;
    return ReadMemory(address, &value, sizeof(value)) && ValidPointer(value);
}

bool SafeGameCode(const std::uintptr_t base, const std::uintptr_t address) noexcept
{
    return address >= base + 0x1000 && address < base + kGameCodeLimitRva;
}

// There are no objects needing C++ unwinding in this SEH boundary.
// Game methods might still be thread-dependent. On any fault, the caller
// permanently disables further probes for this session.
CarObjectProbeResult ProbeInsideSeh(const std::uintptr_t base) noexcept
{
    CarObjectProbeResult result{};
    __try
    {
        std::uintptr_t manager = 0;
        if (!ReadPointer(base + kPlayerManagerRva, manager))
        {
            result.status = CarObjectProbeStatus::NoGameManager;
            return result;
        }

        auto getPlayer = reinterpret_cast<GetLocalPlayerFn>(base + kGetLocalPlayerRva);
        void* player = getPlayer(reinterpret_cast<void*>(manager), 0);
        if (!ValidPointer(reinterpret_cast<std::uintptr_t>(player)))
        {
            result.status = CarObjectProbeStatus::NoPlayer;
            return result;
        }

        void* car = nullptr;
        auto getCar = reinterpret_cast<GetPlayerCarFn>(base + kGetPlayerCarRva);
        getCar(player, &car);
        const auto address = reinterpret_cast<std::uintptr_t>(car);
        if (!ValidPointer(address))
        {
            result.status = CarObjectProbeStatus::NoCar;
            return result;
        }

        std::uintptr_t vtable = 0;
        const bool validType = ReadPointer(address, vtable) &&
                               vtable == base + kPlayerCarVtableRva;
        std::uintptr_t releaseMethod = 0;
        const bool canRelease = validType &&
                                ReadMemory(vtable + kReleaseSlot, &releaseMethod,
                                           sizeof(releaseMethod)) &&
                                SafeGameCode(base, releaseMethod);

        std::uint32_t modelId = 0;
        const bool modelRead = validType &&
                               ReadMemory(address + kModelIdOffset,
                                          &modelId, sizeof(modelId));

        // GetPlayerCar returns an owned reference. Never return without
        // releasing a reference when its verified virtual release method exists.
        if (canRelease)
        {
            auto release = reinterpret_cast<ReleaseFn>(releaseMethod);
            release(car);
        }
        else
        {
            result.status = validType ? CarObjectProbeStatus::UnsafeRelease
                                      : CarObjectProbeStatus::InvalidCarVtable;
            return result;
        }

        if (!modelRead || modelId == 0 || modelId > kMaximumPlausibleModelId)
        {
            result.status = CarObjectProbeStatus::InvalidModelId;
            return result;
        }
        result.status = CarObjectProbeStatus::Ok;
        result.modelId = modelId;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        result.status = CarObjectProbeStatus::Fault;
        result.modelId = 0;
    }
    return result;
}
} // namespace

CarObjectProbeResult ProbeCurrentCarObject(const std::uintptr_t gameBase) noexcept
{
    if (!ValidPointer(gameBase) || gameBase > UINTPTR_MAX - kPlayerManagerRva)
        return {CarObjectProbeStatus::NoGameManager, 0};
    return ProbeInsideSeh(gameBase);
}

const char* CarObjectProbeStatusName(const CarObjectProbeStatus status) noexcept
{
    switch (status)
    {
    case CarObjectProbeStatus::Ok: return "ok";
    case CarObjectProbeStatus::NoGameManager: return "no-manager";
    case CarObjectProbeStatus::NoPlayer: return "no-player";
    case CarObjectProbeStatus::NoCar: return "no-car";
    case CarObjectProbeStatus::InvalidCarVtable: return "invalid-vtable";
    case CarObjectProbeStatus::InvalidModelId: return "invalid-model-id";
    case CarObjectProbeStatus::UnsafeRelease: return "unsafe-release";
    case CarObjectProbeStatus::Fault: return "fault";
    }
    return "unknown";
}
