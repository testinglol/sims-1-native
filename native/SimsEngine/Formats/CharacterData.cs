using System.Numerics;

namespace SimsEngine.Formats;

/// <summary>A name and value pair on a bone, a motion or an event (text: two lines; binary: two strings).</summary>
public readonly record struct PropertyPair(string Name, string Value);

/// <summary>A bone: its name, its parent's name, its property pairs, its translation (x, y, z) and rotation quaternion
/// (x, y, z, w). Five more values follow the transform in Sims.exe (FUN_00595963, +0x6c to +0x7c): three integers and two
/// floats, kept as read. The quaternion order is Inferred from the CFP rotation channels (the fourth channel carries the
/// large component) and the repository's CFP authoring notes; FUN_00595963 stores the four values without naming them.</summary>
public sealed record Bone(string Name, string Parent, IReadOnlyList<PropertyPair> Properties, Vector3 Translation, Quaternion Rotation,
    int[] Integers, float[] Floats);

/// <summary>A skeleton: a name and its bones, in file order (FUN_00598aff).</summary>
public sealed record Skeleton(string Name, IReadOnlyList<Bone> Bones);

/// <summary>One binding of an appearance: the bone a mesh is attached to, the mesh's name and two integers.</summary>
public sealed record Binding(string Bone, string Mesh, int[] Integers);

/// <summary>An appearance (a suit or a body): its name, two integers and its bindings (FUN_0059510c).</summary>
public sealed record Appearance(string Name, int[] Integers, IReadOnlyList<Binding> Bindings);

/// <summary>An event of a motion: the integer Sims.exe reads with each event (FUN_00594536) and its property pairs.</summary>
public sealed record MotionEvent(int Time, IReadOnlyList<PropertyPair> Properties);

/// <summary>One motion of an animation, named after a bone. <see cref="FrameCount"/> is the u32 at +0x14 (FUN_00596834);
/// <see cref="Float"/> and <see cref="Integers"/> are the values after it at +0x18 to +0x28, kept as read.</summary>
public sealed record Motion(string Name, int FrameCount, float Float, int[] Integers, IReadOnlyList<PropertyPair> Properties,
    IReadOnlyList<MotionEvent> Events);

/// <summary>An animation (a "skill"). <see cref="CurveName"/> names its curve file, <c>&lt;CurveName&gt;.cfp</c>.
/// <see cref="TranslationCount"/> and <see cref="RotationCount"/> are the frame counts of its curve arrays: FUN_00596f69
/// stores them at +0x5c and +0x64, and FUN_005972be decodes that many translation and rotation frames.</summary>
public sealed record Animation(string Name, string CurveName, float[] Floats, int Integer, int TranslationCount, int RotationCount,
    IReadOnlyList<Motion> Motions)
{
    /// <summary>Practice cycle length from the first motion's frame count. Windows FUN_00597a90 stores the first
    /// motion's +0x14 value at Practice +0x38; later motions do not extend the cycle.</summary>
    public int PlaybackFrameCount => Motions.Count == 0 ? 0 : Motions[0].FrameCount;

    /// <summary>SAnimator::FindTagTime searches motions in file order, each motion's events in reverse order, and each
    /// event's properties in file order. It matches the requested property name and value, returning the event
    /// time and motion name (the skeleton bone name).</summary>
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

/// <summary>The content of one CMX or BCF file: its skeletons, appearances and animations, in file order.</summary>
public sealed record CharacterData(IReadOnlyList<Skeleton> Skeletons, IReadOnlyList<Appearance> Appearances, IReadOnlyList<Animation> Animations)
{
    /// <summary>A frame count above this is not a real animation; it is rejected before any array is allocated.</summary>
    private const int MaximumFrames = 10_000_000;

    /// <summary>The bytes of a text CMX file: comment lines, then <c>version 300</c>, then the sections.</summary>
    public static CharacterData ReadText(ReadOnlyMemory<byte> data, string source)
    {
        var start = data.Span.StartsWith("\xEF\xBB\xBF"u8) ? 3 : 0;
        var cursor = CharacterCursor.Text(data, source, start);
        if (cursor.ReadLineStartingWith("version ") != "version 300") throw cursor.Error("unsupported CMX version");
        return Read(cursor);
    }

    /// <summary>The bytes of a binary BCF file.</summary>
    public static CharacterData ReadBinary(ReadOnlyMemory<byte> data, string source) => Read(CharacterCursor.Binary(data, source));

    /// <summary>Reads the sections in the order of Sims.exe's loader (FUN_0059ef12 for BCF, FUN_0059e5d1 for CMX): the skeleton
    /// count and skeletons, the appearance count and appearances, then the animation count and animations.</summary>
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
        // FUN_00598aff reads the bone count as a u32 (FUN_005b9095). The installed skeletons parse exactly with that width.
        var bones = new Bone[cursor.Count(54)];
        for (var index = 0; index < bones.Length; index++) bones[index] = ReadBone(cursor);
        return new Skeleton(name, bones);
    }

    /// <summary>FUN_00595963: the bone's name and parent, a flag and then one property group when the flag is set, seven
    /// transform values (three translation, then four rotation values in X, Y, Z, W order), then three integers and two
    /// floats.</summary>
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

    /// <summary>FUN_0059510c: an appearance's name, two integers, then its bindings, each a bone name, a mesh name and two
    /// integers.</summary>
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

    /// <summary>FUN_005972be and FUN_00596f69: the animation's name, its curve name, two floats, an integer, the translation
    /// and rotation frame counts, then the motion count and the motions.</summary>
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

    /// <summary>FUN_00596834: the motion's name, its frame count, a float and four integers, a flag and one property group when
    /// the flag is set (FUN_0059411c), then a flag and the event list when that is set (FUN_00594536).</summary>
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
            // FUN_00594536: a count, then each event is a key (u32) and a property group.
            var count = cursor.Count(8);
            for (var index = 0; index < count; index++)
            {
                var time = cursor.Int32();
                events.Add(new MotionEvent(time, ReadProperties(cursor)));
            }
        }
        return new Motion(name, frameCount, value, integers, properties, events);
    }

    /// <summary>FUN_0059411c: a count of name and value pairs.</summary>
    private static IReadOnlyList<PropertyPair> ReadProperties(CharacterCursor cursor)
    {
        var pairs = new PropertyPair[cursor.Count(2)];
        for (var index = 0; index < pairs.Length; index++) pairs[index] = new PropertyPair(cursor.Text(), cursor.Text());
        return pairs;
    }

    /// <summary>A flag, then one property group when the flag is non-zero (the shape FUN_00595963 and FUN_00596834 read).</summary>
    private static IReadOnlyList<PropertyPair> ReadFlaggedProperties(CharacterCursor cursor) =>
        cursor.Int32() != 0 ? ReadProperties(cursor) : [];

    private static int ReadFrames(CharacterCursor cursor)
    {
        var frames = cursor.Int32();
        if (frames < 0 || frames > MaximumFrames) throw cursor.Error($"frame count {frames} is outside 0 to {MaximumFrames}");
        return frames;
    }
}
