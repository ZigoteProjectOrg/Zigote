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
        string dir = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "zig-prefab-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        try
        {
            var reg = new AssetRegistry();
            var svc = new PrefabService(assets: () => reg, projectDir: () => dir);

            var source = new SceneNode(name: "Turret", kind: NodeKind.Mesh) {
                MeshPath = "#cube",
                MeshMetallic = 0.7f,
            };
            source.AddChild(
                new SceneNode(name: "Barrel", kind: NodeKind.Mesh) { MeshPath = "#cylinder" }
            );

            var id = svc.CreatePrefab(source);
            Assert.False(id.IsEmpty);
            Assert.True(
                File.Exists(
                    Path.Combine(
                        path1: dir,
                        path2: "assets",
                        path3: "prefabs",
                        path4: "Turret.prefab"
                    )
                )
            );

            var inst = svc.InstantiateNode(id);
            Assert.NotNull(inst);
            Assert.Equal(expected: id, actual: inst!.PrefabSource);
            Assert.True(inst.IsPrefabInstance);
            Assert.Equal(expected: "#cube", actual: inst.MeshPath);
            Assert.Equal(expected: 0.7f, actual: inst.MeshMetallic);
            Assert.Single(inst.Children);
            Assert.Equal(expected: "#cylinder", actual: inst.Children[0].MeshPath);
            Assert.Same(
                expected: inst,
                actual: inst.Children[0].Parent
            ); // parent linkage restored on load
        }
        finally
        {
            Directory.Delete(path: dir, recursive: true);
        }
    }

    [Fact]
    public void PrefabPreview_ReportsNameAndNodeCount()
    {
        string dir = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "zig-prefab-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        try
        {
            var reg = new AssetRegistry();
            var svc = new PrefabService(assets: () => reg, projectDir: () => dir);
            var source = new SceneNode(name: "Turret", kind: NodeKind.Mesh) { MeshPath = "#cube" };
            source.AddChild(new SceneNode(name: "Barrel", kind: NodeKind.Mesh));
            svc.CreatePrefab(source);

            string file = Path.Combine(
                path1: dir,
                path2: "assets",
                path3: "prefabs",
                path4: "Turret.prefab"
            );
            var meta = new PrefabPreviewProvider().ExtraMetadata(file).ToList();
            Assert.Contains(expected: ("Prefab", "Turret"), collection: meta);
            Assert.Contains(
                collection: meta,
                filter: kv => kv.Key == "Nodes" && kv.Value == "2"
            ); // handles Preserve $values
        }
        finally
        {
            Directory.Delete(path: dir, recursive: true);
        }
    }

    [Fact]
    public void PrefabSourceId_RoundTripsOnSceneNode()
    {
        var id = AssetId.New();
        var n = new SceneNode(name: "x", kind: NodeKind.Mesh) { PrefabSource = id };
        Assert.Equal(expected: id.ToString(), actual: n.PrefabSourceId);

        var n2 = new SceneNode(name: "y", kind: NodeKind.Mesh) {
            PrefabSourceId = n.PrefabSourceId,
        };
        Assert.Equal(expected: id, actual: n2.PrefabSource);
        Assert.True(n2.IsPrefabInstance);

        var plain = new SceneNode(name: "z", kind: NodeKind.Mesh);
        Assert.Null(plain.PrefabSourceId);
        Assert.False(plain.IsPrefabInstance);
    }

    [Fact]
    public void DeepClone_Preserves_PrefabSource()
    {
        var id = AssetId.New();
        var n = new SceneNode(name: "inst", kind: NodeKind.Mesh) { PrefabSource = id };
        Assert.Equal(
            expected: id,
            actual: n.DeepClone().PrefabSource
        ); // the verified DeepClone landmine
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
        return new SceneNode(name: "Crate", kind: NodeKind.Mesh) {
            MeshPath = "#cube",
            MeshMetallic = 0.5f,
            MeshColor = new Vec3(x: 1f, y: 0f, z: 0f),
            Position = new Vec3(x: 1f, y: 0f, z: 0f),
        };
    }

    [Fact]
    public void NoOverride_WhenInstanceMatchesTemplate()
    {
        var tmpl = Template();
        var inst = tmpl.DeepClone();
        Assert.False(PrefabOverrides.AnyOverridden(instance: inst, template: tmpl));
        Assert.False(
            PrefabOverrides.IsOverridden(
                component: PrefabComponent.Material,
                instance: inst,
                template: tmpl
            )
        );
        Assert.False(
            PrefabOverrides.IsOverridden(
                component: PrefabComponent.Transform,
                instance: inst,
                template: tmpl
            )
        );
    }

    [Fact]
    public void MaterialAndTransform_Overrides_DetectedIndependently()
    {
        var tmpl = Template();
        var inst = tmpl.DeepClone();
        inst.MeshMetallic = 0.9f;

        Assert.True(
            PrefabOverrides.IsOverridden(
                component: PrefabComponent.Material,
                instance: inst,
                template: tmpl
            )
        );
        Assert.False(
            PrefabOverrides.IsOverridden(
                component: PrefabComponent.Transform,
                instance: inst,
                template: tmpl
            )
        );

        inst.Position = new Vec3(x: 5f, y: 0f, z: 0f);
        Assert.True(
            PrefabOverrides.IsOverridden(
                component: PrefabComponent.Transform,
                instance: inst,
                template: tmpl
            )
        );
        Assert.True(PrefabOverrides.AnyOverridden(instance: inst, template: tmpl));
    }

    [Fact]
    public void Revert_CopiesTemplateValues_And_Capture_RoundTrips()
    {
        var tmpl = Template();
        var inst = tmpl.DeepClone();
        inst.MeshMetallic = 0.9f;

        object before = PrefabOverrides.Capture(component: PrefabComponent.Material, node: inst);
        PrefabOverrides.Revert(component: PrefabComponent.Material, instance: inst, template: tmpl);
        Assert.Equal(expected: 0.5f, actual: inst.MeshMetallic); // reverted to template
        Assert.False(
            PrefabOverrides.IsOverridden(
                component: PrefabComponent.Material,
                instance: inst,
                template: tmpl
            )
        );

        PrefabOverrides.Restore(component: PrefabComponent.Material, node: inst, snapshot: before);
        Assert.Equal(expected: 0.9f, actual: inst.MeshMetallic); // undo restores the override
    }

    [Fact]
    public void ApplicableTo_MatchesNodeKind()
    {
        Assert.Equal(
            expected: [PrefabComponent.Transform, PrefabComponent.Material],
            actual: PrefabOverrides.ApplicableTo(new SceneNode(name: "m", kind: NodeKind.Mesh))
                .ToArray()
        );
        Assert.Equal(
            expected: [PrefabComponent.Transform, PrefabComponent.Light],
            actual: PrefabOverrides.ApplicableTo(new SceneNode(name: "l", kind: NodeKind.Light))
                .ToArray()
        );
        Assert.Equal(
            expected: [PrefabComponent.Transform],
            actual: PrefabOverrides.ApplicableTo(new SceneNode("e")).ToArray()
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

    public ScenePrefabTests() => _lib = new ScenePrefabLibrary(_w);

    public void Dispose() => _w.Dispose();

    private static SceneNode MeshTemplate()
    {
        return new SceneNode(name: "Crate", kind: NodeKind.Mesh) {
            MeshPath = "#cube",
            Position = new Vec3(x: 1f, y: 2f, z: 3f),
            MeshColor = new Vec3(x: 0.8f, y: 0.4f, z: 0.2f),
            MeshMetallic = 0.5f,
            MeshRoughness = 0.3f,
        };
    }

    // ── Pure mapper (no ECS) ────────────────────────────────────────────────────

    [Fact]
    public void Mapper_RoundTrips_Material_And_Transform()
    {
        var src = MeshTemplate();
        var dst = new SceneNode(name: "Copy", kind: NodeKind.Mesh);

        SceneNodeComponents.WriteTransform(n: dst, t: SceneNodeComponents.ReadTransform(src));
        SceneNodeComponents.WriteMaterial(n: dst, m: SceneNodeComponents.ReadMaterial(src));

        Assert.Equal(expected: src.Position, actual: dst.Position);
        Assert.Equal(expected: src.MeshColor, actual: dst.MeshColor);
        Assert.Equal(expected: src.MeshMetallic, actual: dst.MeshMetallic);
        Assert.Equal(expected: src.MeshRoughness, actual: dst.MeshRoughness);
    }

    // ── EcsPrefab-backed behaviour ──────────────────────────────────────────────

    [Fact]
    public void Instance_Inherits_Template_Values()
    {
        _lib.DefinePrefab(name: "Crate", template: MeshTemplate());
        var inst = _lib.Instantiate("Crate");

        var node = new SceneNode(name: "Instance", kind: NodeKind.Mesh);
        _lib.ApplyToNode(instance: inst, node: node);

        Assert.Equal(expected: new Vec3(x: 1f, y: 2f, z: 3f), actual: node.Position);
        Assert.Equal(expected: 0.5f, actual: node.MeshMetallic);
        Assert.False(
            _lib.IsOverridden(instance: inst, type: typeof(NodeMaterial))
        ); // inherited, not owned
    }

    [Fact]
    public void Override_Owns_And_Is_Isolated_From_Prefab_Edits()
    {
        var template = MeshTemplate();
        _lib.DefinePrefab(name: "Crate", template: template);
        var inst = _lib.Instantiate("Crate");

        var node = new SceneNode(name: "Instance", kind: NodeKind.Mesh);
        _lib.ApplyToNode(instance: inst, node: node);
        node.MeshMetallic = 0.9f; // user edits the instance
        _lib.OverrideMaterial(instance: inst, node: node);
        Assert.True(_lib.IsOverridden(instance: inst, type: typeof(NodeMaterial)));

        template.MeshMetallic = 0.1f; // edit the prefab template
        _lib.DefinePrefab(name: "Crate", template: template);

        var refreshed = new SceneNode(name: "R", kind: NodeKind.Mesh);
        _lib.ApplyToNode(instance: inst, node: refreshed);
        Assert.Equal(
            expected: 0.9f,
            actual: refreshed.MeshMetallic
        ); // override shields the instance
    }

    [Fact]
    public void Revert_ReInherits_From_Prefab()
    {
        _lib.DefinePrefab(name: "Crate", template: MeshTemplate());
        var inst = _lib.Instantiate("Crate");

        var node = new SceneNode(name: "Instance", kind: NodeKind.Mesh);
        _lib.ApplyToNode(instance: inst, node: node);
        node.MeshMetallic = 0.9f;
        _lib.OverrideMaterial(instance: inst, node: node);
        Assert.True(_lib.IsOverridden(instance: inst, type: typeof(NodeMaterial)));

        Assert.True(_lib.Revert(instance: inst, node: node, type: typeof(NodeMaterial)));
        Assert.False(_lib.IsOverridden(instance: inst, type: typeof(NodeMaterial)));
        Assert.Equal(
            expected: 0.5f,
            actual: node.MeshMetallic
        ); // node refreshed to inherited value
    }

    [Fact]
    public void Editing_Prefab_Propagates_To_NonOverriding_Instance()
    {
        var template = MeshTemplate();
        _lib.DefinePrefab(name: "Crate", template: template);
        var inst = _lib.Instantiate("Crate");

        template.MeshMetallic = 0.15f;
        _lib.DefinePrefab(name: "Crate", template: template); // re-define == edit prefab

        var node = new SceneNode(name: "Instance", kind: NodeKind.Mesh);
        _lib.ApplyToNode(instance: inst, node: node);
        Assert.Equal(
            expected: 0.15f,
            actual: node.MeshMetallic
        ); // non-overriding instance sees the new value
    }

    [Fact]
    public void SerializeInstance_StoresOverridesOnly_AndRoundTrips()
    {
        _lib.DefinePrefab(name: "Crate", template: MeshTemplate());
        var inst = _lib.Instantiate("Crate");

        var node = new SceneNode(name: "Instance", kind: NodeKind.Mesh);
        _lib.ApplyToNode(instance: inst, node: node);
        node.MeshMetallic = 0.77f;
        _lib.OverrideMaterial(instance: inst, node: node);

        var json = _lib.SerializeInstance(instance: inst, prefabName: "Crate");
        Assert.Equal(expected: "Crate", actual: (string?)json["prefab"]);

        var restored = _lib.DeserializeInstance(json);
        Assert.NotEqual(expected: Entity.Null, actual: restored);
        Assert.True(_lib.IsOverridden(instance: restored, type: typeof(NodeMaterial)));

        var restoredNode = new SceneNode(name: "Restored", kind: NodeKind.Mesh);
        _lib.ApplyToNode(instance: restored, node: restoredNode);
        Assert.Equal(expected: 0.77f, actual: restoredNode.MeshMetallic);
    }

    [Fact]
    public void Light_Prefab_Inherits_And_Overrides()
    {
        var template = new SceneNode(name: "Lamp", kind: NodeKind.Light) {
            LightKind = LightType.Point,
            LightIntensity = 2f,
            LightRange = 10f,
        };
        _lib.DefinePrefab(name: "Lamp", template: template);
        var inst = _lib.Instantiate("Lamp");

        var node = new SceneNode(name: "Inst", kind: NodeKind.Light);
        _lib.ApplyToNode(instance: inst, node: node);
        Assert.Equal(expected: 2f, actual: node.LightIntensity);
        Assert.Equal(expected: LightType.Point, actual: node.LightKind);

        node.LightIntensity = 5f;
        _lib.OverrideLight(instance: inst, node: node);
        Assert.True(_lib.IsOverridden(instance: inst, type: typeof(NodeLight)));
    }
}
