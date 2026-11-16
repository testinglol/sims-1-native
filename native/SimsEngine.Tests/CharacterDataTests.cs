using System.Numerics;
using SimsEngine.Formats;

namespace SimsEngine.Tests;

public class CharacterDataTests
{
    private static byte[] Sample(bool text)
    {
        var writer = new CharacterWriter(text).CmxHeader();
        writer.Int(1).Str("dog").Int(2);
        writer.Str("ROOT").Str("NULL").Int(0).Floats(0, 0, 0, 0, 0, 0, 1).Ints(1, 2, 3).Floats(0.5f, -1f);
        writer.Str("PELVIS").Str("ROOT").Int(1).Int(1).Str("Weight").Str("2")
            .Floats(1, 2, 3, 0.1f, 0.2f, 0.3f, 0.4f).Ints(0, 0, 0).Floats(0, 0);
        writer.Int(1).Str("suit").Ints(7, 8).Int(1).Str("PELVIS").Str("xskin-suit-PELVIS-BODY").Ints(0, 0);
        writer.Int(1).Str("a2a-wave").Str("xskill-a2a-wave").Floats(3817f, 0f).Int(232).Int(2).Int(3);
        writer.Int(2);
        writer.Str("ROOT").Int(116).Floats(3817f).Ints(1, 1, 0, 0).Int(1).Int(1).Str("Name").Str("v")
            .Int(1).Int(1).Int(5).Int(0);
        writer.Str("PELVIS").Int(4).Floats(0.5f).Ints(0, 0, 0, 0).Int(0).Int(0);
        return writer.ToArray();
    }

    private static CharacterData ReadSample(bool text) =>
        text ? CharacterData.ReadText(Sample(true), "sample.cmx") : CharacterData.ReadBinary(Sample(false), "sample.bcf");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsSkeletonsAppearancesAndAnimationsInBothForms(bool text)
    {
        var file = ReadSample(text);

        var skeleton = Assert.Single(file.Skeletons);
        Assert.Equal("dog", skeleton.Name);
        Assert.Equal(["ROOT", "PELVIS"], skeleton.Bones.Select(bone => bone.Name));
        var root = skeleton.Bones[0];
        Assert.Equal("NULL", root.Parent);
        Assert.Empty(root.Properties);
        Assert.Equal(Vector3.Zero, root.Translation);
        Assert.Equal(new Quaternion(0, 0, 0, 1), root.Rotation);
        Assert.Equal([1, 2, 3], root.Integers);
        Assert.Equal([0.5f, -1f], root.Floats);

        var pelvis = skeleton.Bones[1];
        Assert.Equal("ROOT", pelvis.Parent);
        Assert.Equal([new PropertyPair("Weight", "2")], pelvis.Properties);
        Assert.Equal(new Vector3(1, 2, 3), pelvis.Translation);
        // Bone rotations in skeleton chunks are parsed in X, Y, Z, W component order.
        Assert.Equal(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f), pelvis.Rotation);

        var appearance = Assert.Single(file.Appearances);
        Assert.Equal("suit", appearance.Name);
        Assert.Equal([7, 8], appearance.Integers);
        var binding = Assert.Single(appearance.Bindings);
        Assert.Equal(("PELVIS", "xskin-suit-PELVIS-BODY"), (binding.Bone, binding.Mesh));

        var animation = Assert.Single(file.Animations);
        Assert.Equal("a2a-wave", animation.Name);
        Assert.Equal("xskill-a2a-wave", animation.CurveName);
        Assert.Equal([3817f, 0f], animation.Floats);
        Assert.Equal(232, animation.Integer);
        Assert.Equal((2, 3), (animation.TranslationCount, animation.RotationCount));
        Assert.Equal(["ROOT", "PELVIS"], animation.Motions.Select(motion => motion.Name));
        var first = animation.Motions[0];
        Assert.Equal(116, first.FrameCount);
        Assert.Equal(3817f, first.Float);
        Assert.Equal([1, 1, 0, 0], first.Integers);
        Assert.Equal([new PropertyPair("Name", "v")], first.Properties);
        var evt = Assert.Single(first.Events);
        Assert.Equal(5, evt.Time);
        Assert.Empty(evt.Properties);
        Assert.Empty(animation.Motions[1].Properties);
        Assert.Empty(animation.Motions[1].Events);
    }

    [Fact]
    public void TextAndBinaryFormsReadTheSameContent()
    {
        var text = ReadSample(true);
        var binary = ReadSample(false);
        Assert.Equal(text.Skeletons.Single().Bones.Select(bone => (bone.Name, bone.Parent, bone.Translation, bone.Rotation)),
            binary.Skeletons.Single().Bones.Select(bone => (bone.Name, bone.Parent, bone.Translation, bone.Rotation)));
        Assert.Equal(text.Animations.Single().Motions.Select(motion => (motion.Name, motion.FrameCount)),
            binary.Animations.Single().Motions.Select(motion => (motion.Name, motion.FrameCount)));
    }

    [Fact]
    public void FindTagTimeUsesSourceMotionAndEventScanOrder()
    {
        var tag = new PropertyPair("anchor", "1");
        var other = new PropertyPair("", "1");
        var firstMotion = new Motion("L_FOOT", 40, 0, [0, 0, 0, 0], [],
        [
            new MotionEvent(10, [tag]),
            new MotionEvent(20, [other, tag]),
        ]);
        var secondMotion = new Motion("R_FOOT", 40, 0, [0, 0, 0, 0], [],
            [new MotionEvent(30, [tag])]);
        var animation = new Animation("walk", "walk", [], 0, 0, 0, [firstMotion, secondMotion]);

        Assert.Equal((20, "L_FOOT"), animation.FindTagTime("anchor", "1"));
        Assert.Null(animation.FindTagTime("anchor", "2"));
        Assert.Equal((20, "L_FOOT"), animation.FindTagTime("", "1"));
        Assert.Null(animation.FindTagTime("Anchor", "1"));
    }

    [Fact]
    public void PracticeCycleLengthComesFromFirstMotionRatherThanLongestTrack()
    {
        var animation = new Animation("walk", "walk", [], 0, 0, 0,
        [
            new Motion("ROOT", 40, 0, [0, 0, 0, 0], [], []),
            new Motion("HEAD", 96, 0, [0, 0, 0, 0], [], []),
        ]);
        var empty = new Animation("empty", "empty", [], 0, 0, 0, []);

        Assert.Equal(40, animation.PlaybackFrameCount);
        Assert.Equal(0, empty.PlaybackFrameCount);
    }

    [Fact]
    public void LongBinaryStringsUseTheExtendedLength()
    {
        var name = new string('n', 300);
        var data = new CharacterWriter(false).Int(0).Int(0).Int(1).Str(name).Str("curve").Floats(0, 0).Int(0).Int(0).Int(0).Int(0).ToArray();
        var file = CharacterData.ReadBinary(data, "long.bcf");
        Assert.Equal(name, file.Animations.Single().Name);
    }

    [Fact]
    public void TruncatedBinaryReportsTheFileAndOffset()
    {
        var data = Sample(false)[..40];
        var error = Assert.Throws<ResourceFormatException>(() => CharacterData.ReadBinary(data, "damaged.bcf"));
        Assert.Equal("damaged.bcf", error.File);
        Assert.InRange(error.Offset, 0, data.Length);
        Assert.Contains("damaged.bcf", error.Message);
    }

    [Fact]
    public void CountThatCannotFitInTheFileIsRejected()
    {
        var data = new CharacterWriter(false).Int(0).Int(0).Int(1_000_000).ToArray();
        var error = Assert.Throws<ResourceFormatException>(() => CharacterData.ReadBinary(data, "count.bcf"));
        Assert.Contains("cannot fit", error.Message);
    }

    [Fact]
    public void NegativeCountIsRejected()
    {
        var data = new CharacterWriter(false).Int(-1).ToArray();
        Assert.Throws<ResourceFormatException>(() => CharacterData.ReadBinary(data, "negative.bcf"));
    }

    [Fact]
    public void BytesAfterTheLastSectionAreRejected()
    {
        var data = new CharacterWriter(false).Int(0).Int(0).Int(0).Raw(1).ToArray();
        var error = Assert.Throws<ResourceFormatException>(() => CharacterData.ReadBinary(data, "tail.bcf"));
        Assert.Equal(12, error.Offset);
    }

    [Fact]
    public void ZeroBytesAfterTheLastSectionAreAllowed()
    {
        var data = new CharacterWriter(false).Int(0).Int(0).Int(0).Raw(0, 0, 0).ToArray();
        Assert.Empty(CharacterData.ReadBinary(data, "padded.bcf").Animations);
    }

    [Fact]
    public void OtherCmxVersionsAreRejected()
    {
        var data = System.Text.Encoding.Latin1.GetBytes("// Character File.\r\nversion 301\r\n0\r\n0\r\n0\r\n");
        var error = Assert.Throws<ResourceFormatException>(() => CharacterData.ReadText(data, "version.cmx"));
        Assert.Contains("unsupported CMX version", error.Message);
    }

    [Fact]
    public void TextBlankLineWhereANumberIsExpectedIsRejected()
    {
        var data = System.Text.Encoding.Latin1.GetBytes("version 300\r\n\r\n");
        var error = Assert.Throws<ResourceFormatException>(() => CharacterData.ReadText(data, "blank.cmx"));
        Assert.Contains("blank line", error.Message);
    }

    [Fact]
    public void ByteOrderMarkBeforeTheCommentIsSkipped()
    {
        var body = Sample(true);
        var data = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();
        Assert.Single(CharacterData.ReadText(data, "bom.cmx").Skeletons);
    }
}
