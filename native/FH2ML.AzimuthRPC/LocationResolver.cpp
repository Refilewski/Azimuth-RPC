#include "LocationResolver.h"

#include <array>
#include <cmath>
#include <cstddef>
#include <limits>

namespace
{
constexpr float kFallbackMaxAnchorDistance = 3250.0f;
constexpr float kFallbackMinimumLeadOverSecond = 500.0f;

struct Point
{
    float x;
    float z;
};

struct Region
{
    const char* name;
    Point anchor;
    const Point* polygon;
    std::size_t count;
};

constexpr Point kSisteron[] = {
    {-5758.0f, -154.0f}, {-415.0f, -154.0f}, {-299.0f, -2317.0f},
    {-996.0f, -3760.0f}, {-2738.0f, -4769.0f}, {-5409.0f, -4625.0f},
    {-6106.0f, -2750.0f}};

constexpr Point kSaintMartin[] = {
    {-763.0f, -10.0f}, {4231.0f, -10.0f}, {4580.0f, -2606.0f},
    {3534.0f, -3904.0f}, {1327.0f, -4192.0f}, {-647.0f, -3327.0f},
    {-1112.0f, -1740.0f}};

constexpr Point kNiceMassena[] = {
    {-6106.0f, -3760.0f}, {-996.0f, -3327.0f}, {1327.0f, -3904.0f},
    {3302.0f, -5779.0f}, {3186.0f, -9385.0f}, {-5525.0f, -9385.0f},
    {-6106.0f, -7077.0f}};

constexpr Point kSanGiovanni[] = {
    {2257.0f, -2606.0f}, {6554.0f, -2173.0f}, {7716.0f, -4625.0f},
    {7135.0f, -6500.0f}, {5393.0f, -7510.0f}, {2954.0f, -6644.0f},
    {1792.0f, -4625.0f}};

constexpr Point kMontellino[] = {
    {6322.0f, 423.0f}, {12710.0f, 423.0f}, {12943.0f, -4914.0f},
    {10852.0f, -5202.0f}, {8761.0f, -5563.0f}, {7251.0f, -6212.0f},
    {7019.0f, -4192.0f}, {6090.0f, -2173.0f}};

constexpr Point kCastelletto[] = {
    {5044.0f, -6500.0f}, {7251.0f, -6212.0f}, {8761.0f, -5563.0f},
    {10852.0f, -5202.0f}, {12943.0f, -4914.0f}, {13175.0f, -10394.0f},
    {3650.0f, -10394.0f}, {3186.0f, -8952.0f}};

template <std::size_t N>
constexpr Region MakeRegion(const char* name, Point anchor, const Point (&polygon)[N])
{
    return {name, anchor, polygon, N};
}

constexpr std::array<Region, 6> kRegions = {
    MakeRegion("Sisteron", {-4147.82f, -2039.93f}, kSisteron),
    MakeRegion("Saint-Martin", {1017.30f, -1799.88f}, kSaintMartin),
    MakeRegion("Nice Massena", {433.464f, -4682.50f}, kNiceMassena),
    MakeRegion("San Giovanni", {5618.33f, -2379.07f}, kSanGiovanni),
    MakeRegion("Montellino", {10508.40f, -3260.87f}, kMontellino),
    MakeRegion("Castelletto", {7840.33f, -6845.91f}, kCastelletto),
};

bool Contains(const Region& region, float x, float z) noexcept
{
    bool inside = false;
    std::size_t j = region.count - 1;

    for (std::size_t i = 0; i < region.count; j = i++)
    {
        const Point& a = region.polygon[i];
        const Point& b = region.polygon[j];
        const bool crosses = (a.z > z) != (b.z > z);
        if (!crosses)
            continue;

        const float edgeX = (b.x - a.x) * (z - a.z) / (b.z - a.z) + a.x;
        if (x < edgeX)
            inside = !inside;
    }

    return inside;
}

float Distance(float x1, float z1, float x2, float z2) noexcept
{
    const float dx = x1 - x2;
    const float dz = z1 - z2;
    return std::sqrt(dx * dx + dz * dz);
}
} // namespace

const char* ResolveFh2MainlandLocation(const float x, const float z) noexcept
{
    if (!std::isfinite(x) || !std::isfinite(z))
        return nullptr;

    const Region* polygonBest = nullptr;
    float polygonBestDistance = std::numeric_limits<float>::infinity();

    for (const Region& region : kRegions)
    {
        if (!Contains(region, x, z))
            continue;

        const float distance = Distance(x, z, region.anchor.x, region.anchor.z);
        if (distance < polygonBestDistance)
        {
            polygonBest = &region;
            polygonBestDistance = distance;
        }
    }

    if (polygonBest != nullptr)
        return polygonBest->name;

    const Region* best = nullptr;
    float bestDistance = std::numeric_limits<float>::infinity();
    float secondDistance = std::numeric_limits<float>::infinity();

    for (const Region& region : kRegions)
    {
        const float distance = Distance(x, z, region.anchor.x, region.anchor.z);
        if (!std::isfinite(distance))
            continue;

        if (distance < bestDistance)
        {
            secondDistance = bestDistance;
            bestDistance = distance;
            best = &region;
        }
        else if (distance < secondDistance)
        {
            secondDistance = distance;
        }
    }

    if (best == nullptr || bestDistance > kFallbackMaxAnchorDistance)
        return nullptr;

    if (std::isfinite(secondDistance) &&
        secondDistance - bestDistance < kFallbackMinimumLeadOverSecond)
    {
        return nullptr;
    }

    return best->name;
}
