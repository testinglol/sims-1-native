namespace SimsEngine.Character;

using System.Numerics;

/// <summary>Maps saved character direction indices to angles used by the avatar renderer. Route directions
/// store discrete indices from 0 through 7 rather than direction bit flags.</summary>
public static class AvatarDirection
{
    public static float Angle(short direction) => Angle((float)direction);

    /// <summary>Resolves the root heading for an avatar animator. A child animator attached through a
    /// parent's head slot receives the parent's root transform; otherwise, the animator direction plus
    /// displacement direction is used, falling back to the person's discrete direction before a root
    /// heading has been seeded.</summary>
    public static float RootDirection(float? animatorDirection, short objectDirection, float displacementDirection,
        float? inheritedRootDirection = null)
    {
        if (inheritedRootDirection is { } inherited && float.IsFinite(inherited)) return inherited;
        var direction = animatorDirection is { } animator && float.IsFinite(animator) ? animator : objectDirection;
        return direction + displacementDirection;
    }

    public static float Angle(float direction)
    {
        var wrapped = Wrap(direction);
        return wrapped * (MathF.PI / 4);
    }

    /// <summary>Converts animator root direction into a lot-space Y-axis rotation angle for avatar meshes.
    /// Rotating local +Z by (direction + 6) * π / 4 maps direction 0 to (0, -1), 2 to (+1, 0), 4 to (0,
    /// +1), and 6 to (-1, 0).</summary>
    public static float LotFacingAngle(float direction) => Angle(direction + 6f);

    /// <summary>Advances a heading toward a target in an eight-unit circle, wrapping the angular delta to
    /// [-4, 4) before clamping.</summary>
    public static float Approach(float current, float target, float maximumStep)
        => Approach(current, target, maximumStep, out _);

    public static float Approach(float current, float target, float maximumStep, out bool clamped)
    {
        var delta = target - current;
        while (delta >= 4f) delta -= 8f;
        while (delta < -4f) delta += 8f;
        clamped = MathF.Abs(delta) > maximumStep;
        return Wrap(current + Math.Clamp(delta, -maximumStep, maximumStep));
    }

    /// <summary>Computes the animator direction from a lot displacement vector as atan2(deltaY, deltaX) *
    /// (4 / π) + 2 wrapped to [0, 8), where direction 0 points along -Y and 2 points along +X.</summary>
    public static float Toward(float deltaX, float deltaY)
    {
        var direction = MathF.Atan2(deltaY, deltaX) * (4f / MathF.PI) + 2f;
        return Wrap(direction);
    }

    /// <summary>Rounds a continuous heading to the nearest discrete object direction index wrapped to [0,
    /// 7].</summary>
    public static short ToObjectDirection(float direction)
    {
        if (!float.IsFinite(direction)) return 0;
        var value = (int)MathF.Floor(direction + 0.5f);
        return (short)((value % 8 + 8) % 8);
    }

    public static float Wrap(float direction) => direction - MathF.Floor(direction / 8f) * 8f;

    public static float ShortestDelta(float from, float to)
    {
        var delta = to - from;
        while (delta >= 4f) delta -= 8f;
        while (delta < -4f) delta += 8f;
        return delta;
    }
}
