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
                From = new GraphPinEndpoint(node.Id, fromPin),
                To = new GraphPinEndpoint(target.Id, toPin),
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
            compiled.Diagnostics,
            d => d.Severity == GraphDiagnosticSeverity.Error
        );
        Assert.True(compiled.Asset.SpawnRate > 0f);
        Assert.NotEmpty(compiled.Asset.UpdateModules);

        var sim = new CpuParticleSimulator(compiled.Asset);
        for (var i = 0; i < 60; i++) sim.Tick(1f / 60f);
        Assert.True(sim.Pool.Count > 0, "default preset should spawn particles");
    }

    [Theory]
    [InlineData("Sparks", VfxBlendMode.Additive)]
    [InlineData("Fire", VfxBlendMode.Additive)]
    [InlineData("Smoke", VfxBlendMode.AlphaBlend)]
    [InlineData("Magic", VfxBlendMode.Additive)]
    [InlineData("Rain", VfxBlendMode.AlphaBlend)]
    public void NamedPresets_Compile(string preset, VfxBlendMode blend)
    {
        var compiled = VfxGraphCompiler.Compile(VfxPresets.Create(preset, preset));
        Assert.True(compiled.Success);
        Assert.Equal(blend, compiled.Asset.Blend);
        Assert.True(compiled.Asset.SpawnRate > 0f);
    }

    [Fact]
    public void MissingOutput_IsAnError()
    {
        var doc = new GraphDocument { DomainId = VfxNodeLibrary.DomainId };
        var compiled = VfxGraphCompiler.Compile(doc);
        Assert.False(compiled.Success);
        Assert.Contains(compiled.Diagnostics, d => d.Code == "VFX0001");
    }

    [Fact]
    public void NoSpawnModule_WarnsButCompiles()
    {
        var (doc, _) = NewGraph();
        var compiled = VfxGraphCompiler.Compile(doc);
        Assert.True(compiled.Success); // warning, not error
        Assert.Contains(compiled.Diagnostics, d => d.Code == "VFX0002");
        Assert.Equal(0f, compiled.Asset.SpawnRate);
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
            doc,
            output,
            VfxNodeLibrary.SpawnRate,
            "out.spawn",
            "in.spawn"
        );

        var asset = VfxGraphCompiler.Compile(doc).Asset;
        Assert.Equal(256, asset.Capacity);
        Assert.False(asset.Looping);
        Assert.Equal(1.5f, asset.Duration, 4);
        Assert.Equal(SimulationSpace.Local, asset.Space);
    }

    [Fact]
    public void UpdateModules_SortedByCanonicalPriority_RegardlessOfWiring()
    {
        var (doc, output) = NewGraph();
        AddInto(
            doc,
            output,
            VfxNodeLibrary.SpawnRate,
            "out.spawn",
            "in.spawn"
        );
        // Wire over-life BEFORE the force, opposite to the desired execution order.
        AddInto(
            doc,
            output,
            VfxNodeLibrary.ColorOverLife,
            "out.update",
            "in.update"
        );
        AddInto(
            doc,
            output,
            VfxNodeLibrary.Gravity,
            "out.update",
            "in.update"
        );

        var asset = VfxGraphCompiler.Compile(doc).Asset;
        Assert.Equal(2, asset.UpdateModules.Count);
        Assert.IsType<GravityModule>(asset.UpdateModules[0]);
        Assert.IsType<ColorOverLifeModule>(asset.UpdateModules[1]);
    }

    [Fact]
    public void ColorValueNode_DrivesInitialColor()
    {
        var (doc, output) = NewGraph();
        AddInto(
            doc,
            output,
            VfxNodeLibrary.SpawnRate,
            "out.spawn",
            "in.spawn"
        );
        var initColor = AddInto(
            doc,
            output,
            VfxNodeLibrary.InitColor,
            "out.init",
            "in.init"
        );

        var color = new GraphNode { DefinitionId = VfxNodeLibrary.ColorValue };
        color.Properties["color"] = GraphValue.FromFloat4(
            0.1f,
            0.2f,
            0.3f,
            1f
        );
        doc.Nodes.Add(color);
        doc.Edges.Add(
            new GraphEdge {
                From = new GraphPinEndpoint(color.Id, "out.color"),
                To = new GraphPinEndpoint(initColor.Id, "in.color"),
            }
        );

        var asset = VfxGraphCompiler.Compile(doc).Asset;
        Assert.Equal(0.1f, asset.StartColor.R, 4);
        Assert.Equal(0.2f, asset.StartColor.G, 4);
        Assert.Equal(0.3f, asset.StartColor.B, 4);
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
            doc,
            output,
            VfxNodeLibrary.SpawnRate,
            "out.spawn",
            "in.spawn"
        );
        var domain = new VfxDomain();

        Assert.True(
            domain.CanCreateEdge(
                doc,
                new GraphPinEndpoint(rate.Id, "out.spawn"),
                new GraphPinEndpoint(output.Id, "in.spawn"),
                out _
            )
        );

        // spawn output into the update input is a type mismatch.
        Assert.False(
            domain.CanCreateEdge(
                doc,
                new GraphPinEndpoint(rate.Id, "out.spawn"),
                new GraphPinEndpoint(output.Id, "in.update"),
                out var reason
            )
        );
        Assert.NotNull(reason);

        // self-loop.
        Assert.False(
            domain.CanCreateEdge(
                doc,
                new GraphPinEndpoint(output.Id, "in.spawn"),
                new GraphPinEndpoint(output.Id, "in.update"),
                out _
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
        Assert.Contains(result.Diagnostics, d => d.Code == "VFX0003");
    }

    [Fact]
    public void RampJson_RoundTrips()
    {
        var ramp = new ColorRamp(
            [
                new ColorStop(0f, new Color(1f, 0f, 0f)),
                new ColorStop(
                    0.5f,
                    new Color(
                        0f,
                        1f,
                        0f,
                        0.5f
                    )
                ),
                new ColorStop(
                    1f,
                    new Color(
                        0f,
                        0f,
                        1f,
                        0f
                    )
                ),
            ]
        );
        var parsed = VfxRampJson.Parse(VfxRampJson.Serialize(ramp));

        Assert.Equal(3, parsed.Stops.Count);
        Assert.Equal(1f, parsed.Evaluate(0.5f).G, 3); // middle stop is fully green
        Assert.Equal(0.5f, parsed.Evaluate(0.5f).A, 3); // …at half alpha
        Assert.Equal(0f, parsed.Evaluate(1f).A, 3);
    }
}