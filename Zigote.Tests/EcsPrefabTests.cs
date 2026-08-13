using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;
using Zigote.Ecs;
using Zigote.Ecs.Prefab;
using Zigote.Ecs.Reflection;

namespace Zigote.Tests;

/// <summary>
///     First-class flecs prefabs: shared components via (IsA, prefab), per-instance override + revert,
///     edit-prefab-propagates, and compact instance serialization (overrides only).
///     Reads use TryGet (read-only ecs_get) — Get (ecs_get_mut) would auto-override an inherited
///     value.
/// </summary>
public sealed class EcsPrefabTests : IDisposable
{
    private readonly EcsPrefabLibrary _lib;
    private readonly EcsComponentRegistry _reg = new();
    private readonly EcsWorld _w = new();

    public EcsPrefabTests()
    {
        _reg.Register<Health>();
        _reg.Register<Speed>();
        _lib = new EcsPrefabLibrary(world: _w, registry: _reg);
    }

    public void Dispose() => _w.Dispose();

    [Fact]
    public void Instance_Inherits_Prefab_Components()
    {
        _lib.Define("Enemy").With(
            new Health {
                Current = 100,
                Max = 100,
            }
        ).With(new Speed { Value = 3f });
        var inst = _lib.Instantiate("Enemy");

        Assert.True(_w.Has<Health>(inst));
        Assert.False(
            _lib.IsOverridden(instance: inst, type: typeof(Health))
        ); // inherited, not owned
        Assert.Equal(expected: 100, actual: Hp(inst));
        Assert.Equal(expected: 3f, actual: Spd(inst));
    }

    [Fact]
    public void Override_Owns_Value_And_Is_Isolated_From_Prefab_Edits()
    {
        var enemy = _lib.Define("Enemy").With(
            new Health {
                Current = 100,
                Max = 100,
            }
        );
        var inst = _lib.Instantiate("Enemy");

        _w.Set(
            e: inst,
            c: new Health {
                Current = 25,
                Max = 100,
            }
        ); // override
        Assert.True(_lib.IsOverridden(instance: inst, type: typeof(Health)));
        Assert.Equal(expected: 25, actual: Hp(inst));

        enemy.With(
            new Health {
                Current = 999,
                Max = 999,
            }
        ); // edit prefab
        Assert.Equal(expected: 25, actual: Hp(inst)); // override shields the instance
    }

    [Fact]
    public void Editing_Prefab_Propagates_To_NonOverriding_Instances()
    {
        var enemy = _lib.Define("Enemy").With(
            new Health {
                Current = 100,
                Max = 100,
            }
        );
        var a = _lib.Instantiate("Enemy");
        var b = _lib.Instantiate("Enemy");
        _w.Set(
            e: b,
            c: new Health {
                Current = 10,
                Max = 100,
            }
        ); // b overrides

        enemy.With(
            new Health {
                Current = 50,
                Max = 200,
            }
        ); // edit prefab

        Assert.Equal(expected: 50, actual: Hp(a)); // a inherits the new prefab value
        Assert.Equal(expected: 200, actual: HpMax(a));
        Assert.Equal(expected: 10, actual: Hp(b)); // b keeps its override
    }

    [Fact]
    public void Revert_Drops_Override_Back_To_Inherited()
    {
        _lib.Define("Enemy").With(
            new Health {
                Current = 100,
                Max = 100,
            }
        );
        var inst = _lib.Instantiate("Enemy");
        _w.Set(
            e: inst,
            c: new Health {
                Current = 1,
                Max = 1,
            }
        );
        Assert.True(_lib.IsOverridden(instance: inst, type: typeof(Health)));

        Assert.True(_lib.Revert(instance: inst, type: typeof(Health)));
        Assert.False(_lib.IsOverridden(instance: inst, type: typeof(Health)));
        Assert.Equal(expected: 100, actual: Hp(inst)); // inherits the prefab again
    }

    [Fact]
    public void SerializeInstance_Stores_Only_Overrides()
    {
        _lib.Define("Enemy").With(
            new Health {
                Current = 100,
                Max = 100,
            }
        ).With(new Speed { Value = 3f });
        var inst = _lib.Instantiate("Enemy");
        _w.Set(e: inst, c: new Speed { Value = 9f }); // override Speed only

        var json = _lib.SerializeInstance(instance: inst, prefabName: "Enemy");

        Assert.Equal(expected: "Enemy", actual: (string?)json["prefab"]);
        var overrides = (JsonArray)json["overrides"]!;
        Assert.Single(overrides); // Health inherited → not stored; only Speed
        Assert.Equal(expected: "Speed", actual: (string?)overrides[0]!["type"]);
    }

    [Fact]
    public void DeserializeInstance_Reapplies_Overrides_And_Keeps_Inheritance()
    {
        var enemy = _lib.Define("Enemy").With(
            new Health {
                Current = 100,
                Max = 100,
            }
        ).With(new Speed { Value = 3f });
        var original = _lib.Instantiate("Enemy");
        _w.Set(e: original, c: new Speed { Value = 9f });

        var json =
            (JsonObject)JsonNode.Parse(
                _lib.SerializeInstance(instance: original, prefabName: "Enemy").ToJsonString()
            )!;
        var restored = _lib.DeserializeInstance(json);

        Assert.Equal(expected: 9f, actual: Spd(restored)); // override restored + owned
        Assert.True(_lib.IsOverridden(instance: restored, type: typeof(Speed)));
        Assert.Equal(expected: 100, actual: Hp(restored)); // Health still inherited
        Assert.False(_lib.IsOverridden(instance: restored, type: typeof(Health)));

        enemy.With(
            new Health {
                Current = 7,
                Max = 7,
            }
        ); // prefab edit still reaches the restored instance
        Assert.Equal(expected: 7, actual: Hp(restored));
    }

    // Read-only accessors (never trigger copy-on-write override).
    private int Hp(Entity e) => _w.TryGet<Health>(e: e, value: out var h) ? h.Current : -1;

    private int HpMax(Entity e) => _w.TryGet<Health>(e: e, value: out var h) ? h.Max : -1;

    private float Spd(Entity e) => _w.TryGet<Speed>(e: e, value: out var s) ? s.Value : -1f;

    [StructLayout(LayoutKind.Sequential)]
    private struct Health
    {
        public int Current;
        public int Max;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Speed
    {
        public float Value;
    }
}
