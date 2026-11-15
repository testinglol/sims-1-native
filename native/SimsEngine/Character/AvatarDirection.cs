namespace SimsEngine.Character;

using System.Numerics;

/// <summary>Maps the saved person direction index to the angle used by the avatar renderer. Sims.exe's route direction
/// lookup (FUN_00452129) stores one of eight values, 0 through 7; they are indices, not direction bit flags.</summary>
public static class AvatarDirection
{
    public static float Angle(short direction) => Angle((float)direction);

    /// <summary>Resolves the root heading used by SAnimator::UpdateRootPos. A child animator attached through
    /// the parent's HEAD slot receives the parent's root transform; otherwise +0x88 plus SetAnimDisplacements' +0x94
    /// is used, falling back to the person's discrete direction before a root heading has been seeded.</summary>
    public static float RootDirection(float? animatorDirection, short objectDirection, float displacementDirection,
        float? inheritedRootDirection = null)
    {
        if (inheritedRootDirection is { } inherited && float.IsFinite(inherited)) return inherited;
        var direction = animatorDirection is { } animator && float.IsFinite(animator) ? animator : objectDirection;
        return direction + displacementDirection;
    }

    /// <summary>Converts the animator's direction value to the renderer angle. Person directions are eight turns around
    /// the lot; SAnimator stores the current value as a float at +0x88 and exposes it from GetDirection.</summary>
    public static float Angle(float direction)
    {
        var wrapped = Wrap(direction);
        return wrapped * (MathF.PI / 4);
    }

    /// <summary>Converts the animator root direction to the lot-space rotation around Y for the avatar mesh.
    /// Matches SAnimator::UpdateRootPos (005920ea.c:106): (direction + 6) * pi/4. In avatar world coordinates,
    /// matrix X maps to LotY and matrix Z maps to LotX via AvatarCoordinates.ToLot; rotating local +Z by
    /// (direction + 6) * pi/4 yields (cos facing, sin facing) in (LotX, LotY), mapping 0->(0,-1), 2->(+1,0),
    /// 4->(0,+1), and 6->(-1,0) (FUN_00452129, FUN_004222ef).</summary>
    public static float LotFacingAngle(float direction) => Angle(direction + 6f);

    /// <summary>Advances a direction in the animator's eight-units-per-turn space. SAnimator::Tick wraps its
    /// direction delta to [-4,4) before clamping it (SAnimator.s 0x1034F550–0x1034F638).</summary>
    public static float Approach(float current, float target, float maximumStep)
        => Approach(current, target, maximumStep, out _);

    /// <summary>Applies SAnimator::Tick's wrapped, bounded turn and reports whether its per-tick limit clamped
    /// the requested heading (the local r21 gate at SAnimator.s 0x10350D60–0x10350DAC).</summary>
    public static float Approach(float current, float target, float maximumStep, out bool clamped)
    {
        var delta = target - current;
        while (delta >= 4f) delta -= 8f;
        while (delta < -4f) delta += 8f;
        clamped = MathF.Abs(delta) > maximumStep;
        return Wrap(current + Math.Clamp(delta, -maximumStep, maximumStep));
    }

    /// <summary>Computes the animator direction from a route vector in (LotX, LotY), matching SAnimator::Tick
    /// (0058dc76.c:872): atan2(deltaY, deltaX) * (4 / pi) + 2, wrapped to [0, 8). Route direction 0 is -Y,
    /// 2 is +X, 4 is +Y, and 6 is -X (FUN_00452129, FUN_004222ef).</summary>
    public static float Toward(float deltaX, float deltaY)
    {
        var direction = MathF.Atan2(deltaY, deltaX) * (4f / MathF.PI) + 2f;
        return Wrap(direction);
    }

    /// <summary>Matches SAnimator state 7's floor(direction + 0.5) followed by wrapping the short direction
    /// into [0, 7] before storing cXObject +0x4c (005920ac.c:7-14; SAnimator.s 0x103501E4–0x10350238).</summary>
    public static short ToObjectDirection(float direction)
    {
        if (!float.IsFinite(direction)) return 0;
        var value = (int)MathF.Floor(direction + 0.5f);
        return (short)((value % 8 + 8) % 8);
    }

    private static float Wrap(float direction) => direction - MathF.Floor(direction / 8f) * 8f;
}
