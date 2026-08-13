using Zigote.Graphs.Core;

namespace Zigote.Graphs.Shading;

/// <summary>
///     Compiles a shader-material graph end-to-end: lower → generate WGSL → constant-fold → package.
///     The
///     single entry point used by both the editor domain and the tests.
/// </summary>
public static class ShaderGraphCompiler
{
    public static CompiledShaderGraph Compile(GraphDocument graph)
    {
        var program = ShaderGraphLowerer.Lower(graph: graph, diagnostics: out var diagnostics);
        bool hasError = diagnostics.Any(d => d.Severity == GraphDiagnosticSeverity.Error);
        var constants = new CpuShaderEvaluator(program).Constants();
        string wgsl = WgslShaderEmitter.Emit(program);

        return new CompiledShaderGraph {
            Success = !hasError,
            Program = program,
            Textures = program.Textures,
            Constants = constants,
            Wgsl = wgsl,
            Diagnostics = diagnostics,
        };
    }
}
