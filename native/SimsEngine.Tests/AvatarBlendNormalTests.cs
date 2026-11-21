using System.Numerics;
using SimsEngine.Character;
using SimsEngine.Content;
using SimsEngine.Formats;
using SimsEngine.Simulation;
using SimsSimulation;

namespace SimsEngine.Tests;

public sealed class AvatarBlendNormalTests
{
    [Fact]
    public void BlendedVertex_BlendsPositionButKeepsPrimaryBoneNormal()
    {
        var tempGameDir = Path.Combine(Path.GetTempPath(), $"sims-blend-normal-{Guid.NewGuid():N}");
        var skinsDir = Path.Combine(tempGameDir, "GameData", "Skins");
        Directory.CreateDirectory(skinsDir);
        try
        {
            var skeleton = new Skeleton("adult",
            [
                new Bone("ROOT", "NULL", [], new Vector3(0f, 3f, 0f), Quaternion.Identity, [0, 0, 0], [0f, 0f]),
                new Bone("PELVIS", "ROOT", [], new Vector3(0f, 0f, 0f), Quaternion.Identity, [0, 0, 0], [0f, 0f]),
                new Bone("R_LEG", "PELVIS", [], new Vector3(0f, -0.5f, 0f), Quaternion.Identity, [0, 0, 0], [0f, 0f]),
            ]);
            var appearance = new Appearance("b001mafit_blend", [0, 0],
            [
                new Binding("PELVIS", "xskin-b001mafit_blend", [0, 0]),
            ]);
            using (var cmxStream = File.Create(Path.Combine(skinsDir, "test-blend.cmx")))
                AnimationAuthor.WriteCharacterContainer(cmxStream, [skeleton], [appearance], [], cmx: true);

            // Vertex 0 is bound to R_LEG with an outward +Y normal, blended 80% with PELVIS whose
            // blend vertex has an inverted -Y normal (reproducing the Maxis SKN/BMF exporter bug).
            // Vertex 1 and 2 are unblended R_LEG vertices with the same +Y normal for comparison.
            var mesh = new Mesh(
                "xskin-b001mafit_blend",
                "b001mafit_blend",
                ["PELVIS", "R_LEG"],
                [new MeshFace(0, 1, 2)],
                [
                    new MeshBinding(0, -1, 0, 0, 1),
                    new MeshBinding(1, 0, 3, -1, 0),
                ],
                [new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f)],
                [new MeshBlend(0, 26214)],
                [
                    new MeshVertex(new Vector3(0f, 0f, 0f), Vector3.UnitY),
                    new MeshVertex(new Vector3(0.2f, 0f, 0f), Vector3.UnitY),
                    new MeshVertex(new Vector3(0f, 0.2f, 0f), Vector3.UnitY),
                    new MeshVertex(new Vector3(0f, 0.5f, 0f), -Vector3.UnitY),
                ]);
            File.WriteAllBytes(Path.Combine(skinsDir, "xskin-b001mafit_blend.skn"), OutfitMesh.Write(mesh));
            File.WriteAllBytes(Path.Combine(skinsDir, "b001mafit_blend.bmp"), OutfitAuthor.WriteBitmap(4, 4, new byte[4 * 4 * 4]));

            var geometry = AvatarSceneGeometry.For(tempGameDir, new ObjectRegistry());
            var person = new XPerson(1, PrimitiveTestWorld.Definition(XPerson.PersonType))
            {
                SavedCharacter = new PersonCharacter(0, 0,
                    [("b001mafit_blend", "b001mafit_blend"), ("NONE", ""), ("NONE", ""), ("NONE", ""), ("NONE", "")],
                    [], [], null, []),
                Position = new ObjectPosition(0x20, 0x20, 1),
            };
            var camera = new LotCamera(0, 64, 64);

            var parts = geometry.Build(person, 1, 0, (_, _) => 0f, camera);
            Assert.Single(parts);
            var verts = parts[0].Vertices;
            Assert.Equal(3, verts.Length);

            var blendedNormal = new Vector3(verts[0].Nx, verts[0].Ny, verts[0].Nz);
            var unblendedNormal = new Vector3(verts[1].Nx, verts[1].Ny, verts[1].Nz);
            Assert.True(Vector3.Dot(blendedNormal, unblendedNormal) > 0.999f,
                $"Expected blended vertex normal {blendedNormal} to match primary bone normal {unblendedNormal}.");
            // Position should still be blended toward PELVIS (higher Y on screen => smaller projected Y).
            Assert.True(verts[0].Y < verts[1].Y - 1f,
                $"Expected blended vertex Y ({verts[0].Y}) to reflect PELVIS position blend relative to unblended vertex ({verts[1].Y}).");
        }
        finally
        {
            if (Directory.Exists(tempGameDir))
                Directory.Delete(tempGameDir, recursive: true);
        }
    }
}
