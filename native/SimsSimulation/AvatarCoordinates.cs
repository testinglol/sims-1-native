using System.Numerics;

/// <summary>Converts between Sims.exe's avatar world axes and the lot axes used by scene geometry.</summary>
internal static class AvatarCoordinates
{
    /// <summary>Mac SAnimator::UpdateRootPos stores lot FTile Y in matrix X and FTile X in matrix Z.</summary>
    internal static Vector3 RootTranslation(float lotX, float lotY, float altitude) =>
        new(lotY, altitude, lotX);

    /// <summary>UpdateRootPos adds 0.5, converts with fctiwz, then passes the FTile coordinate to GetFAlt.</summary>
    internal static float ElevationSample(float rootFtile) => MathF.Truncate(rootFtile + 0.5f) / 16f;

    /// <summary>The native lot projection reads lot X/Y from avatar world Z/X respectively.</summary>
    internal static Vector3 ToLot(Vector3 sourceWorld) =>
        new(sourceWorld.Z, sourceWorld.X, sourceWorld.Y);
}
