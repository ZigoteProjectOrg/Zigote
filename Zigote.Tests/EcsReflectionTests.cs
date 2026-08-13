using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;
using Zigote.Ecs;
using Zigote.Ecs.Reflection;

namespace Zigote.Tests;

/// <summary>
///     Proves the "Option A" editor spine works WITHOUT flecs reflection/meta: a C# component registry
///     drives generic inspection, field editing back into flecs, and scene (de)serialization — plus
///     the
///     OnSet observer notification that a live inspector would use. Components are blittable POD.
/// </summary>
public sealed class EcsReflectionTests : IDisposable
{
    private readonly EcsComponentRegistry _reg = new();
    private readonly EcsWorld _w = new();

    public EcsReflectionTests()
    {
        _reg.Register<Position>();
        _reg.Register<Health>();
    }

    public void Dispose() => _w.Dispose();

    [Fact]
    public void Inspect_Discovers_Components_And_Field_Values_Generically()
    {
        var e = _w.CreateEntity();
        _w.Set(
            e: e,
            c: new Position {
                X = 1,
                Y = 2,
                Z = 3,
            }
        );
        _w.Set(
            e: e,
            c: new Health {
                Current = 70,
                Max = 100,
                Regen = 1.5f,
            }
        );

        var views = EcsEntitySerializer.Inspect(world: _w, registry: _reg, entity: e);
        Assert.Equal(expected: 2, actual: views.Count);

        var health = views.Single(v => v.Type.Name == "Health");
        Assert.Equal(
            expected: ["Current", "Max", "Regen"],
            actual: health.Type.Fields.Select(f => f.Name)
        );
        Assert.Equal(
            expected: 70,
            actual: health.Type.Fields.Single(f => f.Name == "Current").Get(health.Boxed)
        );
        Assert.Equal(
            expected: 1.5f,
            actual: health.Type.Fields.Single(f => f.Name == "Regen").Get(health.Boxed)
        );
    }

    [Fact]
    public void SetField_Writes_Back_Into_Flecs()
    {
        var e = _w.CreateEntity();
        _w.Set(
            e: e,
            c: new Health {
                Current = 50,
                Max = 100,
                Regen = 0f,
            }
        );

        var ct = _reg.ByType(typeof(Health))!;
        EcsEntitySerializer.SetField(
            world: _w,
            entity: e,
            type: ct,
            field: ct.Fields.Single(f => f.Name == "Current"),
            value: 88
        );

        Assert.Equal(
            expected: 88,
            actual: _w.Get<Health>(e).Current
        ); // typed read confirms the native blob changed
        Assert.Equal(expected: 100, actual: _w.Get<Health>(e).Max); // other fields untouched
    }

    [Fact]
    public void Serialize_Deserialize_RoundTrips_Onto_A_New_Entity()
    {
        var src = _w.CreateEntity();
        _w.Set(
            e: src,
            c: new Position {
                X = 4,
                Y = 5,
                Z = 6,
            }
        );
        _w.Set(
            e: src,
            c: new Health {
                Current = 30,
                Max = 120,
                Regen = 2.5f,
            }
        );

        var json = EcsEntitySerializer.Serialize(world: _w, registry: _reg, entity: src);
        // survives a string round-trip (the scene file path)
        var reparsed = (JsonObject)JsonNode.Parse(json.ToJsonString())!;

        var dst = EcsEntitySerializer.Deserialize(world: _w, registry: _reg, data: reparsed);

        Assert.True(_w.Has<Position>(dst));
        Assert.Equal(expected: 5f, actual: _w.Get<Position>(dst).Y);
        Assert.Equal(expected: 30, actual: _w.Get<Health>(dst).Current);
        Assert.Equal(expected: 120, actual: _w.Get<Health>(dst).Max);
        Assert.Equal(expected: 2.5f, actual: _w.Get<Health>(dst).Regen);
    }

    [Fact]
    public void Editing_A_Field_Fires_OnSet_Observer_For_Live_Inspector()
    {
        var captured = new List<(ulong ent, int current)>();
        _w.RegisterObserver<Health>(
            name: "watch",
            evt: EcsEvent.OnSet,
            body: (entities, data) =>
            {
                for (int i = 0; i < entities.Length; i++)
                    captured.Add((entities[i].Raw, data[i].Current));
            }
        );

        var e = _w.CreateEntity();
        _w.Set(
            e: e,
            c: new Health {
                Current = 10,
                Max = 10,
                Regen = 0f,
            }
        ); // fires once

        var ct = _reg.ByType(typeof(Health))!;
        EcsEntitySerializer.SetField(
            world: _w,
            entity: e,
            type: ct,
            field: ct.Fields.Single(f => f.Name == "Current"),
            value: 99
        ); // fires again

        Assert.Equal(expected: 2, actual: captured.Count);
        Assert.Equal(expected: e.Raw, actual: captured[^1].ent);
        Assert.Equal(expected: 99, actual: captured[^1].current); // observer saw the edited value
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Position
    {
        public float X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Health
    {
        public int Current;
        public int Max;
        public float Regen;
    }
}
