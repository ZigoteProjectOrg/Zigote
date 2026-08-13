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

    public void Dispose()
    {
        _w.Dispose();
    }

    [Fact]
    public void Inspect_Discovers_Components_And_Field_Values_Generically()
    {
        var e = _w.CreateEntity();
        _w.Set(
            e,
            new Position {
                X = 1,
                Y = 2,
                Z = 3,
            }
        );
        _w.Set(
            e,
            new Health {
                Current = 70,
                Max = 100,
                Regen = 1.5f,
            }
        );

        var views = EcsEntitySerializer.Inspect(_w, _reg, e);
        Assert.Equal(2, views.Count);

        var health = views.Single(v => v.Type.Name == "Health");
        Assert.Equal(["Current", "Max", "Regen"], health.Type.Fields.Select(f => f.Name));
        Assert.Equal(70, health.Type.Fields.Single(f => f.Name == "Current").Get(health.Boxed));
        Assert.Equal(1.5f, health.Type.Fields.Single(f => f.Name == "Regen").Get(health.Boxed));
    }

    [Fact]
    public void SetField_Writes_Back_Into_Flecs()
    {
        var e = _w.CreateEntity();
        _w.Set(
            e,
            new Health {
                Current = 50,
                Max = 100,
                Regen = 0f,
            }
        );

        var ct = _reg.ByType(typeof(Health))!;
        EcsEntitySerializer.SetField(
            _w,
            e,
            ct,
            ct.Fields.Single(f => f.Name == "Current"),
            88
        );

        Assert.Equal(88, _w.Get<Health>(e).Current); // typed read confirms the native blob changed
        Assert.Equal(100, _w.Get<Health>(e).Max); // other fields untouched
    }

    [Fact]
    public void Serialize_Deserialize_RoundTrips_Onto_A_New_Entity()
    {
        var src = _w.CreateEntity();
        _w.Set(
            src,
            new Position {
                X = 4,
                Y = 5,
                Z = 6,
            }
        );
        _w.Set(
            src,
            new Health {
                Current = 30,
                Max = 120,
                Regen = 2.5f,
            }
        );

        var json = EcsEntitySerializer.Serialize(_w, _reg, src);
        // survives a string round-trip (the scene file path)
        var reparsed = (JsonObject)JsonNode.Parse(json.ToJsonString())!;

        var dst = EcsEntitySerializer.Deserialize(_w, _reg, reparsed);

        Assert.True(_w.Has<Position>(dst));
        Assert.Equal(5f, _w.Get<Position>(dst).Y);
        Assert.Equal(30, _w.Get<Health>(dst).Current);
        Assert.Equal(120, _w.Get<Health>(dst).Max);
        Assert.Equal(2.5f, _w.Get<Health>(dst).Regen);
    }

    [Fact]
    public void Editing_A_Field_Fires_OnSet_Observer_For_Live_Inspector()
    {
        var captured = new List<(ulong ent, int current)>();
        _w.RegisterObserver<Health>(
            "watch",
            EcsEvent.OnSet,
            (entities, data) =>
            {
                for (var i = 0; i < entities.Length; i++)
                    captured.Add((entities[i].Raw, data[i].Current));
            }
        );

        var e = _w.CreateEntity();
        _w.Set(
            e,
            new Health {
                Current = 10,
                Max = 10,
                Regen = 0f,
            }
        ); // fires once

        var ct = _reg.ByType(typeof(Health))!;
        EcsEntitySerializer.SetField(
            _w,
            e,
            ct,
            ct.Fields.Single(f => f.Name == "Current"),
            99
        ); // fires again

        Assert.Equal(2, captured.Count);
        Assert.Equal(e.Raw, captured[^1].ent);
        Assert.Equal(99, captured[^1].current); // observer saw the edited value
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
