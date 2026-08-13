using Xunit;
using Zigote.Core;
using Zigote.Graphs.Core;
using Zigote.Graphs.Vfx;
using Zigote.Vfx;

namespace Zigote.Tests;

/// <summary>
///     Tests for the VFX node domain + compiler: graphs lower to the expected
///     <see cref="VfxEmitterAsset" />
///     module stack, presets compile + run, validation/edge rules hold. Headless (no editor/native).
/// </summary>
public class VfxGraphTests
{
    private static (GraphDocument doc, GraphNode output) NewGraph()
    {
        var doc = new GraphDocument {
            DomainId = VfxNodeLibrary.DomainId,
            SchemaId = VfxNodeLibrary.EmitterSchema,
        };
        var output = new GraphNode { DefinitionId = VfxNodeLibrary.Output };
        doc.Nodes.Add(output);
        return (doc, output);
    }

    private static GraphNode AddInto(GraphDocument doc, GraphNode target, string defId,
        string fromPin, string toPin)
    {
        var node = new GraphNode { DefinitionId = defId };
        doc.Nodes.Add(node);
        doc.Edges.Add(
            new GraphEdge {
                From = new GraphPinEndpoint(NodeId: node.Id, PinId: fromPin),
                To = new GraphPinEndpoint(NodeId: target.Id, PinId: toPin),
            }
        );
        return node;
    }

    [Fact]
    public void DefaultPreset_CompilesAndSimulates()
    {
        var compiled = VfxGraphCompiler.Compile(VfxPresets.CreateDefault());

        Assert.True(compiled.Success);
        Assert.DoesNotContain(
            collection: compiled.Diagnostics,
            filter: d => d.Severity == GraphDiagnosticSeverity.Error
        );
        Assert.True(compiled.Asset.SpawnRate > 0f);
        Assert.NotEmpty(compiled.Asset.UpdateModules);

        var sim = new CpuParticleSimulator(compiled.Asset);
        for (int i = 0; i < 60; i++) sim.Tick(1f / 60f);
        Assert.True(
            condition: sim.Pool.Count > 0,
            userMessage: "default preset should spawn particles"
        );
    }

    [Theory]
    [InlineData("Sparks", VfxBlendMode.Additive)]
    [InlineData("Fire", VfxBlendMode.Additive)]
    [InlineData("Smoke", VfxBlendMode.AlphaBlend)]
    [InlineData("Magic", VfxBlendMode.Additive)]
    [InlineData("Rain", VfxBlendMode.AlphaBlend)]
    public void NamedPresets_Compile(string preset, VfxBlendMode blend)
    {
        var compiled = VfxGraphCompiler.Compile(VfxPresets.Create(preset: preset, name: preset));
        Assert.True(compiled.Success);
        Assert.Equal(expected: blend, actual: compiled.Asset.Blend);
        Assert.True(compiled.Asset.SpawnRate > 0f);
    }

    [Fact]
    public void MissingOutput_IsAnError()
    {
        var doc = new GraphDocument { DomainId = VfxNodeLibrary.DomainId };
        var compiled = VfxGraphCompiler.Compile(doc);
        Assert.False(compiled.Success);
        Assert.Contains(collection: compiled.Diagnostics, filter: d => d.Code == "VFX0001");
    }

    [Fact]
    public void NoSpawnModule_WarnsButCompiles()
    {
        var (doc, _) = NewGraph();
        var compiled = VfxGraphCompiler.Compile(doc);
        Assert.True(compiled.Success); // warning, not error
        Assert.Contains(collection: compiled.Diagnostics, filter: d => d.Code == "VFX0002");
        Assert.Equal(expected: 0f, actual: compiled.Asset.SpawnRate);
    }

    [Fact]
    public void EmitterProperties_LowerOntoAsset()
    {
        var (doc, output) = NewGraph();
        output.Properties["capacity"] = GraphValue.FromInt(256);
        output.Properties["looping"] = GraphValue.FromBool(false);
        output.Properties["duration"] = GraphValue.FromFloat(1.5f);
        output.Properties["space"] = GraphValue.FromInt((int)SimulationSpace.Local);
        AddInto(
            doc: doc,
            target: output,
            defId: VfxNodeLibrary.SpawnRate,
            fromPin: "out.spawn",
            toPin: "in.spawn"
        );

        var asset = VfxGraphCompiler.Compile(doc).Asset;
        Assert.Equal(expected: 256, actual: asset.Capacity);
        Assert.False(asset.Looping);
        Assert.Equal(expected: 1.5f, actual: asset.Duration, precision: 4);
        Assert.Equal(expected: SimulationSpace.Local, actual: asset.Space);
    }

    [Fact]
    public void UpdateModules_SortedByCanonicalPriority_RegardlessOfWiring()
    {
        var (doc, output) = NewGraph();
        AddInto(
            doc: doc,
            target: output,
            defId: VfxNodeLibrary.SpawnRate,
            fromPin: "out.spawn",
            toPin: "in.spawn"
        );
        // Wire over-life BEFORE the force, opposite to the desired execution order.
        AddInto(
            doc: doc,
            target: output,
            defId: VfxNodeLibrary.ColorOverLife,
            fromPin: "out.update",
            toPin: "in.update"
        );
        AddInto(
            doc: doc,
            target: output,
            defId: VfxNodeLibrary.Gravity,
            fromPin: "out.update",
            toPin: "in.update"
        );

        var asset = VfxGraphCompiler.Compile(doc).Asset;
        Assert.Equal(expected: 2, actual: asset.UpdateModules.Count);
        Assert.IsType<GravityModule>(asset.UpdateModules[0]);
        Assert.IsType<ColorOverLifeModule>(asset.UpdateModules[1]);
    }

    [Fact]
    public void ColorValueNode_DrivesInitialColor()
    {
        var (doc, output) = NewGraph();
        AddInto(
            doc: doc,
            target: output,
            defId: VfxNodeLibrary.SpawnRate,
            fromPin: "out.spawn",
            toPin: "in.spawn"
        );
        var initColor = AddInto(
            doc: doc,
            target: output,
            defId: VfxNodeLibrary.InitColor,
            fromPin: "out.init",
            toPin: "in.init"
        );

        var color = new GraphNode { DefinitionId = VfxNodeLibrary.ColorValue };
        color.Properties["color"] = GraphValue.FromFloat4(
            x: 0.1f,
            y: 0.2f,
            z: 0.3f,
            w: 1f
        );
        doc.Nodes.Add(color);
        doc.Edges.Add(
            new GraphEdge {
                From = new GraphPinEndpoint(NodeId: color.Id, PinId: "out.color"),
                To = new GraphPinEndpoint(NodeId: initColor.Id, PinId: "in.color"),
            }
        );

        var asset = VfxGraphCompiler.Compile(doc).Asset;
        Assert.Equal(expected: 0.1f, actual: asset.StartColor.R, precision: 4);
        Assert.Equal(expected: 0.2f, actual: asset.StartColor.G, precision: 4);
        Assert.Equal(expected: 0.3f, actual: asset.StartColor.B, precision: 4);
    }

    [Fact]
    public void Domain_RegistersNodesAndCoreTypes()
    {
        var registry = VfxDomain.CreateRegistry();
        Assert.NotNull(registry.GetNodeDefinition(VfxNodeLibrary.Output));
        Assert.NotNull(registry.GetNodeDefinition(VfxNodeLibrary.Gravity));
        Assert.NotNull(registry.GetTypeDefinition(VfxNodeLibrary.SpawnType)); // domain type
        Assert.NotNull(registry.GetTypeDefinition("core.float")); // core type still present
    }

    [Fact]
    public void CanCreateEdge_EnforcesPinTypes()
    {
        var (doc, output) = NewGraph();
        var rate = AddInto(
            doc: doc,
            target: output,
            defId: VfxNodeLibrary.SpawnRate,
            fromPin: "out.spawn",
            toPin: "in.spawn"
        );
        var domain = new VfxDomain();

        Assert.True(
            domain.CanCreateEdge(
                graph: doc,
                from: new GraphPinEndpoint(NodeId: rate.Id, PinId: "out.spawn"),
                to: new GraphPinEndpoint(NodeId: output.Id, PinId: "in.spawn"),
                reason: out _
            )
        );

        // spawn output into the update input is a type mismatch.
        Assert.False(
            domain.CanCreateEdge(
                graph: doc,
                from: new GraphPinEndpoint(NodeId: rate.Id, PinId: "out.spawn"),
                to: new GraphPinEndpoint(NodeId: output.Id, PinId: "in.update"),
                reason: out string? reason
            )
        );
        Assert.NotNull(reason);

        // self-loop.
        Assert.False(
            domain.CanCreateEdge(
                graph: doc,
                from: new GraphPinEndpoint(NodeId: output.Id, PinId: "in.spawn"),
                to: new GraphPinEndpoint(NodeId: output.Id, PinId: "in.update"),
                reason: out _
            )
        );
    }

    [Fact]
    public void Validate_RejectsMultipleOutputs()
    {
        var (doc, _) = NewGraph();
        doc.Nodes.Add(new GraphNode { DefinitionId = VfxNodeLibrary.Output });
        var result = new VfxDomain().Validate(doc);
        Assert.False(result.IsValid);
        Assert.Contains(collection: result.Diagnostics, filter: d => d.Code == "VFX0003");
    }

    [Fact]
    public void RampJson_RoundTrips()
    {
        var ramp = new ColorRamp(
            [
                new ColorStop(position: 0f, color: new Color(r: 1f, g: 0f, b: 0f)),
                new ColorStop(
                    position: 0.5f,
                    color: new Color(
                        r: 0f,
                        g: 1f,
                        b: 0f,
                        a: 0.5f
                    )
                ),
                new ColorStop(
                    position: 1f,
                    color: new Color(
                        r: 0f,
                        g: 0f,
                        b: 1f,
                        a: 0f
                    )
                ),
            ]
        );
        var parsed = VfxRampJson.Parse(VfxRampJson.Serialize(ramp));

        Assert.Equal(expected: 3, actual: parsed.Stops.Count);
        Assert.Equal(
            expected: 1f,
            actual: parsed.Evaluate(0.5f).G,
            precision: 3
        ); // middle stop is fully green
        Assert.Equal(
            expected: 0.5f,
            actual: parsed.Evaluate(0.5f).A,
            precision: 3
        ); // …at half alpha
        Assert.Equal(expected: 0f, actual: parsed.Evaluate(1f).A, precision: 3);
    }
}
