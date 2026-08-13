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

        Assert.Equal(expected: "90", actual: defaults["Speed"]);
        Assert.Equal(expected: "true", actual: defaults["Clockwise"]);
        Assert.Equal(expected: "\"hi\"", actual: defaults["Label"]);
        Assert.Equal(expected: "7", actual: defaults["Count"]);
        Assert.Contains(expectedSubstring: "\"x\":1", actualString: defaults["Offset"]);
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

        ScriptSerializer.Deserialize(instance: inst, meta: meta, stored: meta.DefaultExports);

        Assert.Equal(expected: 90f, actual: inst.Speed);
        Assert.True(inst.Clockwise);
        Assert.Equal(expected: "hi", actual: inst.Label);
        Assert.Equal(expected: 7, actual: inst.Count);
        Assert.Equal(expected: 1f, actual: inst.Offset.X);
        Assert.Equal(expected: 2f, actual: inst.Offset.Y);
        Assert.Equal(expected: 3f, actual: inst.Offset.Z);
    }

    [Fact]
    public void DeserializeField_AppliesOneFieldAndLeavesOthersUntouched()
    {
        // The play-mode live-tune path: ScriptWorld pushes a single changed export to the running
        // instance. It must update just that field, not reset the others.
        var meta = ScriptMetadata.From(typeof(Tunable));
        var speed = Array.Find(array: meta.ExportedFields, match: f => f.Name == "Speed")!;
        var inst = new Tunable();

        ScriptSerializer.DeserializeField(instance: inst, field: speed, json: "150");

        Assert.Equal(expected: 150f, actual: inst.Speed);
        Assert.True(inst.Clockwise); // unchanged
        Assert.Equal(expected: "hi", actual: inst.Label); // unchanged
    }

    [Fact]
    public void DefaultExports_AreCached()
    {
        var meta = ScriptMetadata.From(typeof(Tunable));
        Assert.Same(expected: meta.DefaultExports, actual: meta.DefaultExports);
    }

    private sealed class Tunable : Component
    {
        [Export]
        [EditorRange(min: 0, max: 720)]
        public float Speed { get; set; } = 90f;

        [Export] public bool Clockwise { get; set; } = true;
        [Export] public string Label { get; set; } = "hi";
        [Export] public int Count { get; set; } = 7;
        [Export] public Vec3 Offset { get; set; } = new(x: 1f, y: 2f, z: 3f);
    }
}
