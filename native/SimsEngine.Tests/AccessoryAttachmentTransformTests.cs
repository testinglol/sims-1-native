using System.Numerics;
using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using Xunit;

namespace SimsEngine.Tests;

public class AccessoryAttachmentTransformTests
{
    private static byte[] Operand(byte index, byte mode, byte flags) => [index, mode, flags, 0, 0, 0, 0, 0];

    private static PersonCharacter Character(params (string, string)[] extraParts) =>
        new(0, 0, Enumerable.Repeat(("", ""), 5).ToArray(), extraParts, [], null, []);

    private static BehaviorScope ScopeWithStr304(string[] privateEntries, string[] globalEntries)
    {
        static byte[] Table(string[] entries)
        {
            var builder = new ChunkBuilder().S16(-1).S16((short)entries.Length);
            foreach (var entry in entries) builder.CString(entry);
            return builder.ToArray();
        }
        var privateFile = new IffFile(ChunkBuilder.File(("STR#", (ushort)304, Table(privateEntries))), "object.iff");
        var globalFile = new IffFile(ChunkBuilder.File(("STR#", (ushort)304, Table(globalEntries))), "global.iff");
        return new BehaviorScope(globalFile, privateFile, null);
    }

    private static TreeContext CreateContextWithScope(XPerson person, BehaviorScope scope)
    {
        var world = new SimulationWorld();
        person.TypeRecord = new TypeRecord(1) { BehaviorScope = scope };
        return new TreeContext(world, person, new StackFrame(0, [], [0, 0]) { Table = scope });
    }

    [Theory]
    [InlineData("Palm", 0.5f, 0.0f, 0.0f)]
    [InlineData("Top", 0.8f, 0.0f, 0.0f)]
    [InlineData("HeadFake", 0.8f, 0.0f, 100.0f)]
    [InlineData("Shoulder", 0.0f, 0.0f, 1.0f)]
    [InlineData("Unrecognized", 0.0f, 0.0f, 0.0f)]
    public void RegistrationSuffixBindsToAuthoredBoneOffsetsViaPose(string suffix, float expectedX, float expectedY, float expectedZ)
    {
        var bonePose = new BonePose("TEST_BONE", "PELVIS", true,
            Vector3.Zero, Quaternion.Identity,
            new Vector3(10.0f, 20.0f, 30.0f), Quaternion.Identity);

        var result = Pose.RegistrationTranslation(bonePose, suffix);
        var expected = new Vector3(10.0f + expectedX, 20.0f + expectedY, 30.0f + expectedZ);

        Assert.Equal(expected.X, result.X, 0.001f);
        Assert.Equal(expected.Y, result.Y, 0.001f);
        Assert.Equal(expected.Z, result.Z, 0.001f);
    }

    [Fact]
    public void RegistrationTranslation_AppliesBoneWorldRotationToAuthoredOffset()
    {
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2.0f);
        var bonePose = new BonePose("R_HAND", "PELVIS", true,
            Vector3.Zero, Quaternion.Identity,
            new Vector3(5.0f, 0.0f, 5.0f), rotation);

        var result = Pose.RegistrationTranslation(bonePose, "Palm");
        var expectedOffset = Vector3.Transform(new Vector3(0.5f, 0.0f, 0.0f), rotation);

        Assert.Equal(5.0f + expectedOffset.X, result.X, 0.001f);
        Assert.Equal(0.0f + expectedOffset.Y, result.Y, 0.001f);
        Assert.Equal(5.0f + expectedOffset.Z, result.Z, 0.001f);
    }

    [Fact]
    public void ComposedAttachmentTransformTracksBoneAndRootMotion()
    {
        var rootPos = new Vector3(12.0f, 0.5f, 15.0f);
        var headingAngle = MathF.PI / 4.0f;
        var rootRotation = Matrix4x4.CreateRotationY(headingAngle);
        var rootTranslation = Matrix4x4.CreateTranslation(rootPos);
        var rootMatrix = rootRotation * rootTranslation;

        var boneLocalPos = new Vector3(0.3f, 1.2f, 0.4f);
        var bonePose = new BonePose("R_HAND", "PELVIS", true,
            boneLocalPos, Quaternion.Identity,
            boneLocalPos, Quaternion.Identity);

        var palmLocal = Pose.RegistrationTranslation(bonePose, "Palm");
        var propWorldPos = Vector3.Transform(palmLocal, rootMatrix);

        Assert.True(propWorldPos.Length() > 0);
        Assert.NotEqual(rootPos, propWorldPos);

        var advancedRootPos = rootPos + new Vector3(0, 0, 1.0f);
        var advancedRootMatrix = rootRotation * Matrix4x4.CreateTranslation(advancedRootPos);
        var advancedPropWorldPos = Vector3.Transform(palmLocal, advancedRootMatrix);

        Assert.Equal(1.0f, advancedPropWorldPos.Z - propWorldPos.Z, 0.001f);
    }

    [Fact]
    public void SimToSimAttachment_RootDirectionAndTranslationInheritance()
    {
        // When attached via the head bone slot, a child character inherits the parent's orientation rather
        // than using its own discrete facing direction.
        var parentDirection = 6.0f;
        var childDiscreteDirection = (short)2;

        var attachedDirection = AvatarDirection.RootDirection(animatorDirection: null,
            objectDirection: childDiscreteDirection, displacementDirection: 0f, inheritedRootDirection: parentDirection);
        var unattachedDirection = AvatarDirection.RootDirection(animatorDirection: null,
            objectDirection: childDiscreteDirection, displacementDirection: 0f, inheritedRootDirection: null);

        Assert.Equal(6.0f, attachedDirection);
        Assert.Equal(2.0f, unattachedDirection);

        var parentRoot = AvatarCoordinates.RootTranslation(10.0f, 8.0f, 1.2f);
        var lot = AvatarCoordinates.ToLot(parentRoot);
        Assert.Equal(10.0f, lot.X);
        Assert.Equal(8.0f, lot.Y);
        Assert.Equal(1.2f, lot.Z);
    }

    [Fact]
    public void ChangeSuitAccessory_Opcode6_BindsAppearanceFromStr304()
    {
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        person.SavedCharacter = Character(("existing-part.apr", ""));

        var scope = ScopeWithStr304(
            privateEntries: ["acc-umbrella", "acc-briefcase.apr"],
            globalEntries: ["acc-global-hat"]);
        var context = CreateContextWithScope(person, scope);

        var result = ChangeSuitAccessoryPrimitive.Execute(context, Operand(index: 0, mode: 2, flags: 0));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.NotNull(person.SavedCharacter);
        // Accessory asset filenames automatically normalize to the .apr extension and append to character
        // appearance parts.
        Assert.Contains(("existing-part.apr", ""), person.SavedCharacter.ExtraParts);
        Assert.Contains(("acc-umbrella.apr", ""), person.SavedCharacter.ExtraParts);

        ChangeSuitAccessoryPrimitive.Execute(context, Operand(index: 1, mode: 2, flags: 0));
        Assert.Equal(3, person.SavedCharacter.ExtraParts.Count);
        Assert.Contains(("acc-briefcase.apr", ""), person.SavedCharacter.ExtraParts);
    }

    [Fact]
    public void ChangeSuitAccessory_RemoveFlag_ClearsTargetAppearanceFromExtraParts()
    {
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        person.SavedCharacter = Character(
            ("acc-umbrella.apr", ""),
            ("acc-briefcase.apr", ""),
            ("acc-sunglasses.apr", ""));

        var scope = ScopeWithStr304(
            privateEntries: ["acc-umbrella", "acc-briefcase"],
            globalEntries: []);
        var context = CreateContextWithScope(person, scope);

        var result = ChangeSuitAccessoryPrimitive.Execute(context, Operand(index: 0, mode: 2, flags: 1));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.NotNull(person.SavedCharacter);
        Assert.DoesNotContain(person.SavedCharacter.ExtraParts, p => p.Item1.Equals("acc-umbrella.apr", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(("acc-briefcase.apr", ""), person.SavedCharacter.ExtraParts);
        Assert.Contains(("acc-sunglasses.apr", ""), person.SavedCharacter.ExtraParts);
    }

    [Fact]
    public void ChangeSuitAccessory_GlobalMode_ReadsFromGlobalBank()
    {
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        person.SavedCharacter = Character();

        var scope = ScopeWithStr304(
            privateEntries: ["private-part"],
            globalEntries: ["global-cape"]);
        var context = CreateContextWithScope(person, scope);

        var result = ChangeSuitAccessoryPrimitive.Execute(context, Operand(index: 0, mode: 0, flags: 0));

        Assert.Equal(PrimitiveResult.True, result);
        Assert.Contains(("global-cape.apr", ""), person.SavedCharacter!.ExtraParts);
    }

    [Fact]
    public void ChangeSuitAccessory_Mode1_ManagesOutfitAndCostumeTrunkVariant()
    {
        var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType));
        person[PersonData.CurrentOutfit] = 9;
        person[PersonData.CostumeTrunkOffsetHouseParty] = 4;

        var world = new SimulationWorld();
        var context = new TreeContext(world, person, new StackFrame(0, [], []));

        var result = ChangeSuitAccessoryPrimitive.Execute(context, Operand(index: 5, mode: 1, flags: 1));
        Assert.Equal(PrimitiveResult.True, result);
        Assert.Equal(9, person[PersonData.CurrentOutfit]);
        Assert.Equal(4, person[PersonData.CostumeTrunkOffsetHouseParty]);

        // Suit changes with an index below 8 update the current outfit and reset the outfit variant to
        // zero.
        ChangeSuitAccessoryPrimitive.Execute(context, Operand(index: 3, mode: 1, flags: 0));
        Assert.Equal(3, person[PersonData.CurrentOutfit]);
        Assert.Equal(0, person[PersonData.CostumeTrunkOffsetHouseParty]);
    }
}
