using System.Numerics;

namespace AzimuthRPC;

/// <summary>
/// Conservative mainland location resolver for FH2 Xbox One 1.0.0.64.
///
/// Stage 1 uses coarse map polygons derived from the full FH2 mainland map and
/// aligned to the live world-coordinate frame recovered from MapSetTransform.
/// These polygons are an approximation of the visible macro-regions, not native
/// game region volumes. Stage 2 falls back to the confirmed car-meet anchors.
///
/// Ambiguous positions can still return null so Discord falls back to
/// "Exploring Southern Europe" instead of publishing a confidently wrong city.
/// </summary>
public sealed class Fh2MainlandLocationResolver : ILocationResolver
{
    public static Fh2MainlandLocationResolver Instance { get; } = new();

    private const float FallbackMaxAnchorDistance = 3_250f;
    private const float FallbackMinimumLeadOverSecond = 500f;

    private static readonly Region[] Regions =
    [
        new(
            "Sisteron",
            new(-4147.82f, -2039.93f),
            [
                new(-5758f,  -154f),
                new( -415f,  -154f),
                new( -299f, -2317f),
                new( -996f, -3760f),
                new(-2738f, -4769f),
                new(-5409f, -4625f),
                new(-6106f, -2750f),
            ]),

        new(
            "Saint-Martin",
            new(1017.30f, -1799.88f),
            [
                new( -763f,   -10f),
                new( 4231f,   -10f),
                new( 4580f, -2606f),
                new( 3534f, -3904f),
                new( 1327f, -4192f),
                new( -647f, -3327f),
                new(-1112f, -1740f),
            ]),

        new(
            "Nice Massena",
            new(433.464f, -4682.50f),
            [
                new(-6106f, -3760f),
                new( -996f, -3327f),
                new( 1327f, -3904f),
                new( 3302f, -5779f),
                new( 3186f, -9385f),
                new(-5525f, -9385f),
                new(-6106f, -7077f),
            ]),

        new(
            "San Giovanni",
            new(5618.33f, -2379.07f),
            [
                new(2257f, -2606f),
                new(6554f, -2173f),
                new(7716f, -4625f),
                new(7135f, -6500f),
                new(5393f, -7510f),
                new(2954f, -6644f),
                new(1792f, -4625f),
            ]),

        new(
            "Montellino",
            new(10508.40f, -3260.87f),
            [
                new( 6322f,   423f),
                new(12710f,   423f),
                new(12943f, -4914f),
                new(10852f, -5202f),
                new( 8761f, -5563f),
                new( 7251f, -6212f),
                new( 7019f, -4192f),
                new( 6090f, -2173f),
            ]),

        new(
            "Castelletto",
            new(7840.33f, -6845.91f),
            [
                new( 5044f,  -6500f),
                new( 7251f,  -6212f),
                new( 8761f,  -5563f),
                new(10852f,  -5202f),
                new(12943f,  -4914f),
                new(13175f, -10394f),
                new( 3650f, -10394f),
                new( 3186f,  -8952f),
            ]),
    ];

    public string? Resolve(Vector3 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Z))
            return null;

        Region? polygonBest = null;
        float polygonBestDistance = float.PositiveInfinity;

        foreach (Region region in Regions)
        {
            if (!Contains(region.Polygon, position.X, position.Z))
                continue;

            float distance = Distance(position.X, position.Z, region.Anchor.X, region.Anchor.Z);
            if (distance < polygonBestDistance)
            {
                polygonBest = region;
                polygonBestDistance = distance;
            }
        }

        // Polygons intentionally overlap a little around transition corridors.
        // If more than one contains the point, the region whose confirmed live
        // anchor is closest wins.
        if (polygonBest is not null)
            return polygonBest.Name;

        // Last-resort fallback for small holes between coarse polygons.
        Region? best = null;
        float bestDistance = float.PositiveInfinity;
        float secondDistance = float.PositiveInfinity;

        foreach (Region region in Regions)
        {
            float distance = Distance(position.X, position.Z, region.Anchor.X, region.Anchor.Z);
            if (!float.IsFinite(distance))
                continue;

            if (distance < bestDistance)
            {
                secondDistance = bestDistance;
                bestDistance = distance;
                best = region;
            }
            else if (distance < secondDistance)
            {
                secondDistance = distance;
            }
        }

        if (best is null || bestDistance > FallbackMaxAnchorDistance)
            return null;

        if (float.IsFinite(secondDistance) && secondDistance - bestDistance < FallbackMinimumLeadOverSecond)
            return null;

        return best.Name;
    }

    private static bool Contains(MapPoint[] polygon, float x, float z)
    {
        bool inside = false;
        int j = polygon.Length - 1;

        for (int i = 0; i < polygon.Length; j = i++)
        {
            MapPoint a = polygon[i];
            MapPoint b = polygon[j];
            bool crosses = (a.Z > z) != (b.Z > z);
            if (!crosses)
                continue;

            float edgeX = (b.X - a.X) * (z - a.Z) / (b.Z - a.Z) + a.X;
            if (x < edgeX)
                inside = !inside;
        }

        return inside;
    }

    private static float Distance(float x1, float z1, float x2, float z2)
    {
        float dx = x1 - x2;
        float dz = z1 - z2;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private sealed record Region(string Name, MapPoint Anchor, MapPoint[] Polygon);
    private readonly record struct MapPoint(float X, float Z);
}
