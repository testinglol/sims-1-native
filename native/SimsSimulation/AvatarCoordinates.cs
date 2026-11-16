using System.Numerics;

/// <summary>Converts between avatar coordinate space and lot scene geometry axes.</summary>
internal static class AvatarCoordinates
{
    /// <summary>Constructs a root translation vector by mapping lot Y to X, altitude to Y, and lot X to Z.</summary>
    internal static Vector3 RootTranslation(float lotX, float lotY, float altitude) =>
        new(lotY, altitude, lotX);

    /// <summary>Samples elevation by rounding fractional tile coordinates to the nearest sixteenth-tile
    /// grid point.</summary>
    internal static float ElevationSample(float rootFtile) => MathF.Truncate(rootFtile + 0.5f) / 16f;

    /// <summary>Maps avatar coordinate space back into lot coordinates by reading lot X from avatar Z and
    /// lot Y from avatar X.</summary>
    internal static Vector3 ToLot(Vector3 sourceWorld) =>
        new(sourceWorld.Z, sourceWorld.X, sourceWorld.Y);
}
