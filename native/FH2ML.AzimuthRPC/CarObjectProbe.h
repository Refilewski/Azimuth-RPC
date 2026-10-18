#pragma once

#include <cstdint>

// Experimental FH2 1.0.0.64 in-process reader, based on the local-car path
// used by Extra Options. Never call from DllMain. Disabled by default.
enum class CarObjectProbeStatus : std::uint32_t
{
    Ok,
    NoGameManager,
    NoPlayer,
    NoCar,
    InvalidCarVtable,
    InvalidModelId,
    UnsafeRelease,
    Fault,
};

struct CarObjectProbeResult
{
    CarObjectProbeStatus status{CarObjectProbeStatus::NoGameManager};
    std::uint32_t modelId{};
};

// These native game calls are NOT established as thread-safe.
// Caller MUST require explicit developer opt-in and only a validated game PE.
// The probe is non-patching, but it still invokes game methods.
CarObjectProbeResult ProbeCurrentCarObject(std::uintptr_t gameBase) noexcept;

const char* CarObjectProbeStatusName(CarObjectProbeStatus status) noexcept;
