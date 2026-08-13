using System.Text.Json.Nodes;
using Zigote.Core.Math3D;
using Zigote.Ecs;
using Zigote.Ecs.Prefab;
using Zigote.Ecs.Reflection;
using Zigote.Ecs.Scene;

namespace Zigote.EcsBench;

/// <summary>
///     Standalone correctness checks for the SceneNode↔entity bridge + first-class prefabs. Lives in
///     the
///     benchmark (references only Zigote.ECS) so it runs independent of the editor build. Mirrors the
///     xUnit tests in Zigote.Tests for when that assembly's editor dependency compiles again.
///     Run: dotnet run -c Release --project Zigote.Ecs.Benchmark -- verify
/// </summary>
internal static class Verify
{
    public static int Run()
    {
        int failures = 0;
        Console.WriteLine("=== Bridge + Prefab verification ===");

        Section(name: "EcsSceneBridge", failures: ref failures, body: BridgeChecks);
        Section(name: "EcsPrefab", failures: ref failures, body: PrefabChecks);

        Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static void BridgeChecks(Asserter a)
    {
        using var bridge = new EcsSceneBridge();
        var root = new FakeNode {
            Id = 1,
            Name = "root",
            Position = new Vec3(x: 1, y: 0, z: 0),
            Kids = {
                new FakeNode {
                    Id = 2,
                    Name = "a",
                    Kids = {
                        new FakeNode {
                            Id = 3,
                            Name = "a.1",
                        },
                    },
                },
            },
        };
        bridge.BuildFrom(root);

        a.True(cond: bridge.NodeEntities.Count == 3, label: "entity per node");
        a.True(
            cond: bridge.TryNodeId(e: bridge.EntityOf(1), nodeId: out int nid) && nid == 1,
            label: "reverse lookup"
        );
        a.True(
            cond: bridge.TryGetTransform(nodeId: 1, transform: out var t0) &&
                  t0.Position.Equals(new Vec3(x: 1, y: 0, z: 0)),
            label: "transform seeded"
        );
        a.True(
            cond: bridge.World.GetParent(bridge.EntityOf(2)) == bridge.EntityOf(1),
            label: "ChildOf mirrored"
        );
        a.True(
            cond: bridge.World.GetParent(bridge.EntityOf(3)) == bridge.EntityOf(2),
            label: "grandchild ChildOf"
        );

        // canonical entity transform → node mirror (the play hand-off)
        bridge.SetTransform(
            nodeId: 1,
            transform: new Transform {
                Position = new Vec3(x: 9, y: 8, z: 7),
                Rotation = Quat.Identity,
                Scale = Vec3.One,
            }
        );
        a.True(
            cond: root.Position.Equals(new Vec3(x: 1, y: 0, z: 0)),
            label: "node not yet mirrored"
        );
        bridge.PullTransforms(root);
        a.True(
            cond: root.Position.Equals(new Vec3(x: 9, y: 8, z: 7)),
            label: "PullTransforms mirrors entity→node"
        );

        // author edit → entity bake
        root.Position = new Vec3(x: 5, y: 5, z: 5);
        bridge.PushTransforms(root);
        a.True(
            cond: bridge.TryGetTransform(nodeId: 1, transform: out var t1) &&
                  t1.Position.Equals(new Vec3(x: 5, y: 5, z: 5)),
            label: "PushTransforms node→entity"
        );

        // remove subtree
        var childE = bridge.EntityOf(2);
        bridge.RemoveNode(root.Kids[0]);
        a.True(
            cond: bridge.EntityOf(2).IsNull && bridge.EntityOf(3).IsNull,
            label: "subtree entities removed"
        );
        a.True(cond: !bridge.World.IsAlive(childE), label: "removed entity destroyed");
    }

    private static void PrefabChecks(Asserter a)
    {
        using var w = new EcsWorld();
        var reg = new EcsComponentRegistry();
        reg.Register<Health>();
        reg.Register<Speed>();
        var lib = new EcsPrefabLibrary(world: w, registry: reg);

        int HealthOf(Entity e) =>
            w.TryGet<Health>(e: e, value: out var h)
                ? h.Current
                : -1; // read-only: never auto-overrides

        float SpeedOf(Entity e) => w.TryGet<Speed>(e: e, value: out var s) ? s.Value : -1f;

        var enemy = lib.Define("Enemy").With(
            new Health {
                Current = 100,
                Max = 100,
            }
        ).With(new Speed { Value = 3f });
        var inst = lib.Instantiate("Enemy");
        a.True(
            cond: w.Has<Health>(inst) && !lib.IsOverridden(instance: inst, type: typeof(Health)),
            label: "instance inherits (not owned)"
        );
        a.True(cond: HealthOf(inst) == 100 && SpeedOf(inst) == 3f, label: "inherited values");

        w.Set(
            e: inst,
            c: new Health {
                Current = 25,
                Max = 100,
            }
        );
        a.True(
            cond: lib.IsOverridden(instance: inst, type: typeof(Health)) && HealthOf(inst) == 25,
            label: "override owns value"
        );
        enemy.With(
            new Health {
                Current = 999,
                Max = 999,
            }
        );
        a.True(cond: HealthOf(inst) == 25, label: "override isolated from prefab edit");

        var a2 = lib.Instantiate("Enemy");
        a.True(cond: HealthOf(a2) == 999, label: "new instance inherits latest prefab value");

        a.True(
            cond: lib.Revert(instance: inst, type: typeof(Health)) &&
                  !lib.IsOverridden(instance: inst, type: typeof(Health)),
            label: "revert drops override"
        );
        a.True(cond: HealthOf(inst) == 999, label: "reverted instance inherits again");

        // serialize overrides-only round-trip
        var iso = lib.Instantiate("Enemy");
        w.Set(e: iso, c: new Speed { Value = 9f });
        var json = (JsonObject)JsonNode.Parse(
            lib.SerializeInstance(instance: iso, prefabName: "Enemy").ToJsonString()
        )!;
        a.True(
            cond: ((JsonArray)json["overrides"]!).Count == 1,
            label: "only overrides serialized"
        );
        var restored = lib.DeserializeInstance(json);
        a.True(
            cond: SpeedOf(restored) == 9f &&
                  lib.IsOverridden(instance: restored, type: typeof(Speed)),
            label: "override restored"
        );
        a.True(
            cond: HealthOf(restored) == 999 && !lib.IsOverridden(
                instance: restored,
                type: typeof(Health)
            ),
            label: "inheritance kept"
        );
    }

    private static void Section(string name, ref int failures, Action<Asserter> body)
    {
        var a = new Asserter(name);
        try
        {
            body(a);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{name}] threw: {ex.Message}");
            a.Failures++;
        }

        failures += a.Failures;
    }

    private sealed class Asserter(string section)
    {
        public int Failures;

        public void True(bool cond, string label)
        {
            Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {section}: {label}");
            if (!cond) Failures++;
        }
    }

    private struct Health
    {
        public int Current;
        public int Max;
    }

    private struct Speed
    {
        public float Value;
    }

    private sealed class FakeNode : IEcsSceneNode
    {
        public List<FakeNode> Kids { get; } = [];
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public Vec3 Position { get; set; } = Vec3.Zero;
        public Quat Rotation { get; set; } = Quat.Identity;
        public Vec3 Scale { get; set; } = Vec3.One;
        IReadOnlyList<IEcsSceneNode> IEcsSceneNode.Children => Kids;
    }
}
