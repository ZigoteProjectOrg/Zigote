using Xunit;
using Zigote.Core.Assets;
using Zigote.Core.Math3D;
using Zigote.Ecs;
using Zigote.Editor.Panels.AssetPreview.Providers;
using Zigote.Editor.Prefab;
using Zigote.Runtime.Prefab;
using Zigote.Runtime.Scene;

namespace Zigote.Tests;

/// <summary>
///     The editor-facing prefab asset flow (<see cref="PrefabService" /> +
///     <see cref="PrefabDocument" />):
///     create a <c>.prefab</c> from a subtree, register it, instantiate it back, and round-trip the
///     structure + the <see cref="SceneNode.PrefabSourceId" /> serialization. Pure file/clone/registry
///     —
///     no native, no ECS.
/// </summary>
public sealed class PrefabServiceTests
{
    [Fact]
    public void CreateThenInstantiate_RoundTrips_Structure()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zig-prefab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var reg = new AssetRegistry();
            var svc = new PrefabService(() => reg, () => dir);

            var source = new SceneNode("Turret", NodeKind.Mesh) {
                MeshPath = "#cube",
                MeshMetallic = 0.7f,
            };
            source.AddChild(new SceneNode("Barrel", NodeKind.Mesh) { MeshPath = "#cylinder" });

            var id = svc.CreatePrefab(source);
            Assert.False(id.IsEmpty);
            Assert.True(
                File.Exists(
                    Path.Combine(
                        dir,
                        "assets",
                        "prefabs",
                        "Turret.prefab"
                    )
                )
            );

            var inst = svc.InstantiateNode(id);
            Assert.NotNull(inst);
            Assert.Equal(id, inst!.PrefabSource);
            Assert.True(inst.IsPrefabInstance);
            Assert.Equal("#cube", inst.MeshPath);
            Assert.Equal(0.7f, inst.MeshMetallic);
            Assert.Single(inst.Children);
            Assert.Equal("#cylinder", inst.Children[0].MeshPath);
            Assert.Same(inst, inst.Children[0].Parent); // parent linkage restored on load
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PrefabPreview_ReportsNameAndNodeCount()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zig-prefab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var reg = new AssetRegistry();
            var svc = new PrefabService(() => reg, () => dir);
            var source = new SceneNode("Turret", NodeKind.Mesh) { MeshPath = "#cube" };
            source.AddChild(new SceneNode("Barrel", NodeKind.Mesh));
            svc.CreatePrefab(source);

            var file = Path.Combine(
                dir,
                "assets",
                "prefabs",
                "Turret.prefab"
            );
            var meta = new PrefabPreviewProvider().ExtraMetadata(file).ToList();
            Assert.Contains(("Prefab", "Turret"), meta);
            Assert.Contains(
                meta,
                kv => kv.Key == "Nodes" && kv.Value == "2"
            ); // handles Preserve $values
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PrefabSourceId_RoundTripsOnSceneNode()
    {
        var id = AssetId.New();
        var n = new SceneNode("x", NodeKind.Mesh) { PrefabSource = id };
        Assert.Equal(id.ToString(), n.PrefabSourceId);

        var n2 = new SceneNode("y", NodeKind.Mesh) { PrefabSourceId = n.PrefabSourceId };
        Assert.Equal(id, n2.PrefabSource);
        Assert.True(n2.IsPrefabInstance);

        var plain = new SceneNode("z", NodeKind.Mesh);
        Assert.Null(plain.PrefabSourceId);
        Assert.False(plain.IsPrefabInstance);
    }

    [Fact]
    public void DeepClone_Preserves_PrefabSource()
    {
        var id = AssetId.New();
        var n = new SceneNode("inst", NodeKind.Mesh) { PrefabSource = id };
        Assert.Equal(id, n.DeepClone().PrefabSource); // the verified DeepClone landmine
    }
}

/// <summary>
///     Edit-mode override detection + revert (<see cref="PrefabOverrides" />): a prefab instance's
///     authorable components diffed against its template, at the per-component granularity flecs
///     tracks.
///     Pure — no ECS, no native.
/// </summary>
public sealed class PrefabOverridesTests
{
    private static SceneNode Template()
    {
        return new SceneNode("Crate", NodeKind.Mesh) {
            MeshPath = "#cube",
            MeshMetallic = 0.5f,
            MeshColor = new Vec3(1f, 0f, 0f),
            Position = new Vec3(1f, 0f, 0f),
        };
    }

    [Fact]
    public void NoOverride_WhenInstanceMatchesTemplate()
    {
        var tmpl = Template();
        var inst = tmpl.DeepClone();
        Assert.False(PrefabOverrides.AnyOverridden(inst, tmpl));
        Assert.False(PrefabOverrides.IsOverridden(PrefabComponent.Material, inst, tmpl));
        Assert.False(PrefabOverrides.IsOverridden(PrefabComponent.Transform, inst, tmpl));
    }

    [Fact]
    public void MaterialAndTransform_Overrides_DetectedIndependently()
    {
        var tmpl = Template();
        var inst = tmpl.DeepClone();
        inst.MeshMetallic = 0.9f;

        Assert.True(PrefabOverrides.IsOverridden(PrefabComponent.Material, inst, tmpl));
        Assert.False(PrefabOverrides.IsOverridden(PrefabComponent.Transform, inst, tmpl));

        inst.Position = new Vec3(5f, 0f, 0f);
        Assert.True(PrefabOverrides.IsOverridden(PrefabComponent.Transform, inst, tmpl));
        Assert.True(PrefabOverrides.AnyOverridden(inst, tmpl));
    }

    [Fact]
    public void Revert_CopiesTemplateValues_And_Capture_RoundTrips()
    {
        var tmpl = Template();
        var inst = tmpl.DeepClone();
        inst.MeshMetallic = 0.9f;

        var before = PrefabOverrides.Capture(PrefabComponent.Material, inst);
        PrefabOverrides.Revert(PrefabComponent.Material, inst, tmpl);
        Assert.Equal(0.5f, inst.MeshMetallic); // reverted to template
        Assert.False(PrefabOverrides.IsOverridden(PrefabComponent.Material, inst, tmpl));

        PrefabOverrides.Restore(PrefabComponent.Material, inst, before);
        Assert.Equal(0.9f, inst.MeshMetallic); // undo restores the override
    }

    [Fact]
    public void ApplicableTo_MatchesNodeKind()
    {
        Assert.Equal(
            [PrefabComponent.Transform, PrefabComponent.Material],
            PrefabOverrides.ApplicableTo(new SceneNode("m", NodeKind.Mesh)).ToArray()
        );
        Assert.Equal(
            [PrefabComponent.Transform, PrefabComponent.Light],
            PrefabOverrides.ApplicableTo(new SceneNode("l", NodeKind.Light)).ToArray()
        );
        Assert.Equal(
            [PrefabComponent.Transform],
            PrefabOverrides.ApplicableTo(new SceneNode("e")).ToArray()
        );
    }
}

/// <summary>
///     The editor prefab engine built on flecs <c>EcsPrefab</c> (<see cref="ScenePrefabLibrary" />):
///     a SceneNode template becomes a prefab whose numeric state instances inherit, override (own),
///     revert (remove), propagate (edit prefab), and serialize (overrides-only). Uses a real flecs
///     world, like <c>EcsPrefabTests</c>. Plus the pure, ECS-free <see cref="SceneNodeComponents" />
///     mapper.
/// </summary>
public sealed class ScenePrefabTests : IDisposable
{
    private readonly ScenePrefabLibrary _lib;
    private readonly EcsWorld _w = new();

    public ScenePrefabTests()
    {
        _lib = new ScenePrefabLibrary(_w);
    }

    public void Dispose()
    {
        _w.Dispose();
    }

    private static SceneNode MeshTemplate()
    {
        return new SceneNode("Crate", NodeKind.Mesh) {
            MeshPath = "#cube",
            Position = new Vec3(1f, 2f, 3f),
            MeshColor = new Vec3(0.8f, 0.4f, 0.2f),
            MeshMetallic = 0.5f,
            MeshRoughness = 0.3f,
        };
    }

    // ── Pure mapper (no ECS) ────────────────────────────────────────────────────

    [Fact]
    public void Mapper_RoundTrips_Material_And_Transform()
    {
        var src = MeshTemplate();
        var dst = new SceneNode("Copy", NodeKind.Mesh);

        SceneNodeComponents.WriteTransform(dst, SceneNodeComponents.ReadTransform(src));
        SceneNodeComponents.WriteMaterial(dst, SceneNodeComponents.ReadMaterial(src));

        Assert.Equal(src.Position, dst.Position);
        Assert.Equal(src.MeshColor, dst.MeshColor);
        Assert.Equal(src.MeshMetallic, dst.MeshMetallic);
        Assert.Equal(src.MeshRoughness, dst.MeshRoughness);
    }

    // ── EcsPrefab-backed behaviour ──────────────────────────────────────────────

    [Fact]
    public void Instance_Inherits_Template_Values()
    {
        _lib.DefinePrefab("Crate", MeshTemplate());
        var inst = _lib.Instantiate("Crate");

        var node = new SceneNode("Instance", NodeKind.Mesh);
        _lib.ApplyToNode(inst, node);

        Assert.Equal(new Vec3(1f, 2f, 3f), node.Position);
        Assert.Equal(0.5f, node.MeshMetallic);
        Assert.False(_lib.IsOverridden(inst, typeof(NodeMaterial))); // inherited, not owned
    }

    [Fact]
    public void Override_Owns_And_Is_Isolated_From_Prefab_Edits()
    {
        var template = MeshTemplate();
        _lib.DefinePrefab("Crate", template);
        var inst = _lib.Instantiate("Crate");

        var node = new SceneNode("Instance", NodeKind.Mesh);
        _lib.ApplyToNode(inst, node);
        node.MeshMetallic = 0.9f; // user edits the instance
        _lib.OverrideMaterial(inst, node);
        Assert.True(_lib.IsOverridden(inst, typeof(NodeMaterial)));

        template.MeshMetallic = 0.1f; // edit the prefab template
        _lib.DefinePrefab("Crate", template);

        var refreshed = new SceneNode("R", NodeKind.Mesh);
        _lib.ApplyToNode(inst, refreshed);
        Assert.Equal(0.9f, refreshed.MeshMetallic); // override shields the instance
    }

    [Fact]
    public void Revert_ReInherits_From_Prefab()
    {
        _lib.DefinePrefab("Crate", MeshTemplate());
        var inst = _lib.Instantiate("Crate");

        var node = new SceneNode("Instance", NodeKind.Mesh);
        _lib.ApplyToNode(inst, node);
        node.MeshMetallic = 0.9f;
        _lib.OverrideMaterial(inst, node);
        Assert.True(_lib.IsOverridden(inst, typeof(NodeMaterial)));

        Assert.True(_lib.Revert(inst, node, typeof(NodeMaterial)));
        Assert.False(_lib.IsOverridden(inst, typeof(NodeMaterial)));
        Assert.Equal(0.5f, node.MeshMetallic); // node refreshed to inherited value
    }

    [Fact]
    public void Editing_Prefab_Propagates_To_NonOverriding_Instance()
    {
        var template = MeshTemplate();
        _lib.DefinePrefab("Crate", template);
        var inst = _lib.Instantiate("Crate");

        template.MeshMetallic = 0.15f;
        _lib.DefinePrefab("Crate", template); // re-define == edit prefab

        var node = new SceneNode("Instance", NodeKind.Mesh);
        _lib.ApplyToNode(inst, node);
        Assert.Equal(0.15f, node.MeshMetallic); // non-overriding instance sees the new value
    }

    [Fact]
    public void SerializeInstance_StoresOverridesOnly_AndRoundTrips()
    {
        _lib.DefinePrefab("Crate", MeshTemplate());
        var inst = _lib.Instantiate("Crate");

        var node = new SceneNode("Instance", NodeKind.Mesh);
        _lib.ApplyToNode(inst, node);
        node.MeshMetallic = 0.77f;
        _lib.OverrideMaterial(inst, node);

        var json = _lib.SerializeInstance(inst, "Crate");
        Assert.Equal("Crate", (string?)json["prefab"]);

        var restored = _lib.DeserializeInstance(json);
        Assert.NotEqual(Entity.Null, restored);
        Assert.True(_lib.IsOverridden(restored, typeof(NodeMaterial)));

        var restoredNode = new SceneNode("Restored", NodeKind.Mesh);
        _lib.ApplyToNode(restored, restoredNode);
        Assert.Equal(0.77f, restoredNode.MeshMetallic);
    }

    [Fact]
    public void Light_Prefab_Inherits_And_Overrides()
    {
        var template = new SceneNode("Lamp", NodeKind.Light) {
            LightKind = LightType.Point,
            LightIntensity = 2f,
            LightRange = 10f,
        };
        _lib.DefinePrefab("Lamp", template);
        var inst = _lib.Instantiate("Lamp");

        var node = new SceneNode("Inst", NodeKind.Light);
        _lib.ApplyToNode(inst, node);
        Assert.Equal(2f, node.LightIntensity);
        Assert.Equal(LightType.Point, node.LightKind);

        node.LightIntensity = 5f;
        _lib.OverrideLight(inst, node);
        Assert.True(_lib.IsOverridden(inst, typeof(NodeLight)));
    }
}