using System.Text.Json.Nodes;
using Zigote.Core.Math3D;
using Zigote.Ecs;
using Zigote.Ecs.Prefab;
using Zigote.Ecs.Reflection;
using Zigote.Ecs.Scene;

namespace Zigote.EcsBench;

/// <summary>
///     Standalone correctness checks for the SceneNode↔entity bridge + first-class prefabs. Lives in the
///     benchmark (references only Zigote.ECS) so it runs independent of the editor build. Mirrors the
///     xUnit tests in Zigote.Tests for when that assembly's editor dependency compiles again.
///     Run: dotnet run -c Release --project Zigote.Ecs.Benchmark -- verify
/// </summary>
internal static class Verify
{
    public static int Run()
    {
        var failures = 0;
        Console.WriteLine("=== Bridge + Prefab verification ===");

        Section("EcsSceneBridge", ref failures, BridgeChecks);
        Section("EcsPrefab", ref failures, PrefabChecks);

        Console.WriteLine(failures == 0 ? "\nALL PASS" : $"\n{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    private static void BridgeChecks(Asserter a)
    {
        using var bridge = new EcsSceneBridge();
        var root = new FakeNode {
            Id = 1,
            Name = "root",
            Position = new Vec3(1, 0, 0),
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

        a.True(bridge.NodeEntities.Count == 3, "entity per node");
        a.True(bridge.TryNodeId(bridge.EntityOf(1), out var nid) && nid == 1, "reverse lookup");
        a.True(
            bridge.TryGetTransform(1, out var t0) && t0.Position.Equals(new Vec3(1, 0, 0)),
            "transform seeded"
        );
        a.True(
            bridge.World.GetParent(bridge.EntityOf(2)) == bridge.EntityOf(1),
            "ChildOf mirrored"
        );
        a.True(
            bridge.World.GetParent(bridge.EntityOf(3)) == bridge.EntityOf(2),
            "grandchild ChildOf"
        );

        // canonical entity transform → node mirror (the play hand-off)
        bridge.SetTransform(
            1,
            new Transform {
                Position = new Vec3(9, 8, 7),
                Rotation = Quat.Identity,
                Scale = Vec3.One,
            }
        );
        a.True(root.Position.Equals(new Vec3(1, 0, 0)), "node not yet mirrored");
        bridge.PullTransforms(root);
        a.True(root.Position.Equals(new Vec3(9, 8, 7)), "PullTransforms mirrors entity→node");

        // author edit → entity bake
        root.Position = new Vec3(5, 5, 5);
        bridge.PushTransforms(root);
        a.True(
            bridge.TryGetTransform(1, out var t1) && t1.Position.Equals(new Vec3(5, 5, 5)),
            "PushTransforms node→entity"
        );

        // remove subtree
        var childE = bridge.EntityOf(2);
        bridge.RemoveNode(root.Kids[0]);
        a.True(bridge.EntityOf(2).IsNull && bridge.EntityOf(3).IsNull, "subtree entities removed");
        a.True(!bridge.World.IsAlive(childE), "removed entity destroyed");
    }

    private static void PrefabChecks(Asserter a)
    {
        using var w = new EcsWorld();
        var reg = new EcsComponentRegistry();
        reg.Register<Health>();
        reg.Register<Speed>();
        var lib = new EcsPrefabLibrary(w, reg);

        int HealthOf(Entity e)
        {
            return w.TryGet<Health>(e, out var h) ? h.Current : -1;
        } // read-only: never auto-overrides

        float SpeedOf(Entity e)
        {
            return w.TryGet<Speed>(e, out var s) ? s.Value : -1f;
        }

        var enemy = lib.Define("Enemy").With(
            new Health {
                Current = 100,
                Max = 100,
            }
        ).With(new Speed { Value = 3f });
        var inst = lib.Instantiate("Enemy");
        a.True(
            w.Has<Health>(inst) && !lib.IsOverridden(inst, typeof(Health)),
            "instance inherits (not owned)"
        );
        a.True(HealthOf(inst) == 100 && SpeedOf(inst) == 3f, "inherited values");

        w.Set(
            inst,
            new Health {
                Current = 25,
                Max = 100,
            }
        );
        a.True(
            lib.IsOverridden(inst, typeof(Health)) && HealthOf(inst) == 25,
            "override owns value"
        );
        enemy.With(
            new Health {
                Current = 999,
                Max = 999,
            }
        );
        a.True(HealthOf(inst) == 25, "override isolated from prefab edit");

        var a2 = lib.Instantiate("Enemy");
        a.True(HealthOf(a2) == 999, "new instance inherits latest prefab value");

        a.True(
            lib.Revert(inst, typeof(Health)) && !lib.IsOverridden(inst, typeof(Health)),
            "revert drops override"
        );
        a.True(HealthOf(inst) == 999, "reverted instance inherits again");

        // serialize overrides-only round-trip
        var iso = lib.Instantiate("Enemy");
        w.Set(iso, new Speed { Value = 9f });
        var json = (JsonObject)JsonNode.Parse(lib.SerializeInstance(iso, "Enemy").ToJsonString())!;
        a.True(((JsonArray)json["overrides"]!).Count == 1, "only overrides serialized");
        var restored = lib.DeserializeInstance(json);
        a.True(
            SpeedOf(restored) == 9f && lib.IsOverridden(restored, typeof(Speed)),
            "override restored"
        );
        a.True(
            HealthOf(restored) == 999 && !lib.IsOverridden(restored, typeof(Health)),
            "inheritance kept"
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
