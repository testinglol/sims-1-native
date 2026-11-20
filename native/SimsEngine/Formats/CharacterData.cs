using System.Numerics;

namespace SimsEngine.Formats;

public readonly record struct PropertyPair(string Name, string Value);

/// <summary>Represents a skeleton bone with its hierarchy, translation, rotation quaternion stored in (x,
/// y, z, w) order, and trailing auxiliary numeric attributes.</summary>
public sealed record Bone(string Name, string Parent, IReadOnlyList<PropertyPair> Properties, Vector3 Translation, Quaternion Rotation,
    int[] Integers, float[] Floats)
{
    /// <summary>Raw wiggle length metadata stored in <see cref="Floats"/>[0] (reinterpreted as int32 in binary BCF).
    /// Sims.exe computes unused scratch vectors from this field at load time and never applies it in pose sampling or skinning.</summary>
    public int WiggleLengthBits => Floats.Length > 0 ? BitConverter.SingleToInt32Bits(Floats[0]) : 0;

    /// <summary>Wiggle power metadata stored in <see cref="Floats"/>[1], never applied by Sims.exe during pose sampling or skinning.</summary>
    public float WigglePower => Floats.Length > 1 ? Floats[1] : 0f;
}

public sealed record Skeleton(string Name, IReadOnlyList<Bone> Bones);

public sealed record Binding(string Bone, string Mesh, int[] Integers);

public sealed record Appearance(string Name, int[] Integers, IReadOnlyList<Binding> Bindings);

public sealed record MotionEvent(int Time, IReadOnlyList<PropertyPair> Properties);

public sealed record Motion(string Name, int FrameCount, float Float, int[] Integers, IReadOnlyList<PropertyPair> Properties,
    IReadOnlyList<MotionEvent> Events);

/// <summary>Defines an animation skill referencing an external CFP curve file, specifying its translation
/// and rotation curve frame counts along with per-bone motions.</summary>
public sealed record Animation(string Name, string CurveName, float[] Floats, int Integer, int TranslationCount, int RotationCount,
    IReadOnlyList<Motion> Motions)
{
    /// <summary>Gets the total playback cycle length, which is determined strictly by the first motion's
    /// frame count.</summary>
    public int PlaybackFrameCount => Motions.Count == 0 ? 0 : Motions[0].FrameCount;

    /// <summary>Searches for a matching property tag across motions in file order, scanning events in
    /// reverse order and properties in forward order to return the event time and bone name.</summary>
    public (int Time, string Bone)? FindTagTime(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        for (var motionIndex = 0; motionIndex < Motions.Count; motionIndex++)
        {
            var motion = Motions[motionIndex];
            for (var eventIndex = motion.Events.Count - 1; eventIndex >= 0; eventIndex--)
            {
                var animationEvent = motion.Events[eventIndex];
                for (var propertyIndex = 0; propertyIndex < animationEvent.Properties.Count; propertyIndex++)
                {
                    var property = animationEvent.Properties[propertyIndex];
                    if (property.Name == name && property.Value == value)
                        return (animationEvent.Time, motion.Name);
                }
            }
        }
        return null;
    }
}

public sealed record CharacterData(IReadOnlyList<Skeleton> Skeletons, IReadOnlyList<Appearance> Appearances, IReadOnlyList<Animation> Animations)
{
    private const int MaximumFrames = 10_000_000;

    public static CharacterData ReadText(ReadOnlyMemory<byte> data, string source)
    {
        var start = data.Span.StartsWith("\xEF\xBB\xBF"u8) ? 3 : 0;
        var cursor = CharacterCursor.Text(data, source, start);
        if (cursor.ReadLineStartingWith("version ") != "version 300") throw cursor.Error("unsupported CMX version");
        return Read(cursor);
    }

    public static CharacterData ReadBinary(ReadOnlyMemory<byte> data, string source) => Read(CharacterCursor.Binary(data, source));

    private static CharacterData Read(CharacterCursor cursor)
    {
        var skeletons = new Skeleton[cursor.Count(5)];
        for (var index = 0; index < skeletons.Length; index++) skeletons[index] = ReadSkeleton(cursor);
        var appearances = new Appearance[cursor.Count(5)];
        for (var index = 0; index < appearances.Length; index++) appearances[index] = ReadAppearance(cursor);
        var animations = new Animation[cursor.Count(5)];
        for (var index = 0; index < animations.Length; index++) animations[index] = ReadAnimation(cursor);
        cursor.ExpectEnd();
        return new CharacterData(skeletons, appearances, animations);
    }

    private static Skeleton ReadSkeleton(CharacterCursor cursor)
    {
        var name = cursor.Text();
        var bones = new Bone[cursor.Count(54)];
        for (var index = 0; index < bones.Length; index++) bones[index] = ReadBone(cursor);
        return new Skeleton(name, bones);
    }

    private static Bone ReadBone(CharacterCursor cursor)
    {
        var name = cursor.Text();
        var parent = cursor.Text();
        var properties = ReadFlaggedProperties(cursor);
        var translation = new Vector3(cursor.Float32(), cursor.Float32(), cursor.Float32());
        var rotation = new Quaternion(cursor.Float32(), cursor.Float32(), cursor.Float32(), cursor.Float32());
        var integers = new[] { cursor.Int32(), cursor.Int32(), cursor.Int32() };
        var floats = new[] { cursor.Float32(), cursor.Float32() };
        return new Bone(name, parent, properties, translation, rotation, integers, floats);
    }

    private static Appearance ReadAppearance(CharacterCursor cursor)
    {
        var name = cursor.Text();
        var integers = new[] { cursor.Int32(), cursor.Int32() };
        var bindings = new Binding[cursor.Count(10)];
        for (var index = 0; index < bindings.Length; index++)
        {
            var bone = cursor.Text();
            var mesh = cursor.Text();
            bindings[index] = new Binding(bone, mesh, [cursor.Int32(), cursor.Int32()]);
        }
        return new Appearance(name, integers, bindings);
    }

    private static Animation ReadAnimation(CharacterCursor cursor)
    {
        var name = cursor.Text();
        var curveName = cursor.Text();
        var floats = new[] { cursor.Float32(), cursor.Float32() };
        var integer = cursor.Int32();
        var translationCount = ReadFrames(cursor);
        var rotationCount = ReadFrames(cursor);
        var motions = new Motion[cursor.Count(30)];
        for (var index = 0; index < motions.Length; index++) motions[index] = ReadMotion(cursor);
        return new Animation(name, curveName, floats, integer, translationCount, rotationCount, motions);
    }

    private static Motion ReadMotion(CharacterCursor cursor)
    {
        var name = cursor.Text();
        var frameCount = cursor.Int32();
        var value = cursor.Float32();
        var integers = new[] { cursor.Int32(), cursor.Int32(), cursor.Int32(), cursor.Int32() };
        var properties = ReadFlaggedProperties(cursor);
        var events = new List<MotionEvent>();
        if (cursor.Int32() != 0)
        {
            var count = cursor.Count(8);
            for (var index = 0; index < count; index++)
            {
                var time = cursor.Int32();
                events.Add(new MotionEvent(time, ReadProperties(cursor)));
            }
        }
        return new Motion(name, frameCount, value, integers, properties, events);
    }

    private static IReadOnlyList<PropertyPair> ReadProperties(CharacterCursor cursor)
    {
        var pairs = new PropertyPair[cursor.Count(2)];
        for (var index = 0; index < pairs.Length; index++) pairs[index] = new PropertyPair(cursor.Text(), cursor.Text());
        return pairs;
    }

    private static IReadOnlyList<PropertyPair> ReadFlaggedProperties(CharacterCursor cursor) =>
        cursor.Int32() != 0 ? ReadProperties(cursor) : [];

    private static int ReadFrames(CharacterCursor cursor)
    {
        var frames = cursor.Int32();
        if (frames < 0 || frames > MaximumFrames) throw cursor.Error($"frame count {frames} is outside 0 to {MaximumFrames}");
        return frames;
    }
}
