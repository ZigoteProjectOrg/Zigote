using Xunit;
using Zigote.Core.Math3D;
using Zigote.Scripting;
using Zigote.Scripting.Metadata;
using Zigote.Scripting.Serialization;

namespace Zigote.Tests;

/// <summary>
///     Covers the scripting-layer logic behind the inspector's exported-field defaults (so a freshly
///     attached script shows its real values, not zeros) and play-mode live tuning (push one inspector
///     edit to a running component). Pure — no editor/native engine.
/// </summary>
public class ScriptExportsTests
{
    [Fact]
    public void DefaultExports_ExposeCompiledInDefaults()
    {
        var defaults = ScriptMetadata.From(typeof(Tunable)).DefaultExports;

        Assert.Equal("90", defaults["Speed"]);
        Assert.Equal("true", defaults["Clockwise"]);
        Assert.Equal("\"hi\"", defaults["Label"]);
        Assert.Equal("7", defaults["Count"]);
        Assert.Contains("\"x\":1", defaults["Offset"]);
    }

    [Fact]
    public void DefaultExports_RoundTripBackToTheOriginalValues()
    {
        // The inspector displays these defaults for an untouched field; deserializing them onto a fresh
        // instance must reproduce exactly what the script runs with — i.e. what's shown == what runs.
        var meta = ScriptMetadata.From(typeof(Tunable));
        var inst = new Tunable {
            Speed = 0f,
            Clockwise = false,
            Label = "",
            Count = 0,
            Offset = Vec3.Zero,
        };

        ScriptSerializer.Deserialize(inst, meta, meta.DefaultExports);

        Assert.Equal(90f, inst.Speed);
        Assert.True(inst.Clockwise);
        Assert.Equal("hi", inst.Label);
        Assert.Equal(7, inst.Count);
        Assert.Equal(1f, inst.Offset.X);
        Assert.Equal(2f, inst.Offset.Y);
        Assert.Equal(3f, inst.Offset.Z);
    }

    [Fact]
    public void DeserializeField_AppliesOneFieldAndLeavesOthersUntouched()
    {
        // The play-mode live-tune path: ScriptWorld pushes a single changed export to the running
        // instance. It must update just that field, not reset the others.
        var meta = ScriptMetadata.From(typeof(Tunable));
        var speed = Array.Find(meta.ExportedFields, f => f.Name == "Speed")!;
        var inst = new Tunable();

        ScriptSerializer.DeserializeField(inst, speed, "150");

        Assert.Equal(150f, inst.Speed);
        Assert.True(inst.Clockwise); // unchanged
        Assert.Equal("hi", inst.Label); // unchanged
    }

    [Fact]
    public void DefaultExports_AreCached()
    {
        var meta = ScriptMetadata.From(typeof(Tunable));
        Assert.Same(meta.DefaultExports, meta.DefaultExports);
    }

    private sealed class Tunable : Component
    {
        [Export] [EditorRange(0, 720)] public float Speed { get; set; } = 90f;
        [Export] public bool Clockwise { get; set; } = true;
        [Export] public string Label { get; set; } = "hi";
        [Export] public int Count { get; set; } = 7;
        [Export] public Vec3 Offset { get; set; } = new(1f, 2f, 3f);
    }
}