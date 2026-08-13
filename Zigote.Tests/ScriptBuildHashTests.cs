using Xunit;
using Zigote.Scripting.Compilation;

namespace Zigote.Tests;

/// <summary>
///     Verifies the incremental script-build fingerprint (
///     <see cref="ScriptCompiler.ComputeBuildFingerprint" />):
///     it is stable when nothing changes, changes when a source/project file or a referenced project's
///     built assembly changes, and ignores MSBuild's generated <c>obj/</c>/<c>bin/</c> trees. Pure
///     file
///     I/O in a temp directory — no <c>dotnet build</c> and no native engine.
/// </summary>
public class ScriptBuildHashTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "zigote-scripthash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private string MakeProject(string name = "Scripts")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, name + ".csproj");
        File.WriteAllText(
            csproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        File.WriteAllText(Path.Combine(dir, "A.cs"), "public class A { }");
        return csproj;
    }

    [Fact]
    public void Fingerprint_is_stable_when_nothing_changes()
    {
        var proj = MakeProject();
        Assert.Equal(
            ScriptCompiler.ComputeBuildFingerprint(proj),
            ScriptCompiler.ComputeBuildFingerprint(proj)
        );
    }

    [Fact]
    public void Fingerprint_changes_when_a_source_file_content_changes()
    {
        var proj = MakeProject();
        var before = ScriptCompiler.ComputeBuildFingerprint(proj);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(proj)!, "A.cs"),
            "public class A { int x; }"
        );
        Assert.NotEqual(before, ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_changes_when_a_source_file_is_added()
    {
        var proj = MakeProject();
        var before = ScriptCompiler.ComputeBuildFingerprint(proj);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(proj)!, "B.cs"), "public class B { }");
        Assert.NotEqual(before, ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_changes_when_the_csproj_changes()
    {
        var proj = MakeProject();
        var before = ScriptCompiler.ComputeBuildFingerprint(proj);
        File.AppendAllText(proj, "<!-- a comment -->");
        Assert.NotEqual(before, ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_ignores_obj_and_bin_artefacts()
    {
        var proj = MakeProject();
        var dir = Path.GetDirectoryName(proj)!;
        var before = ScriptCompiler.ComputeBuildFingerprint(proj);

        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        File.WriteAllText(Path.Combine(dir, "obj", "Scripts.AssemblyInfo.cs"), "// generated");
        Directory.CreateDirectory(Path.Combine(dir, "bin", "Debug"));
        File.WriteAllText(
            Path.Combine(
                dir,
                "bin",
                "Debug",
                "leftover.cs"
            ),
            "// stale output"
        );

        Assert.Equal(before, ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_changes_when_nuget_restore_state_changes()
    {
        var proj = MakeProject();
        var assets = Path.Combine(Path.GetDirectoryName(proj)!, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        File.WriteAllText(assets, "{\"version\":3}");
        var before = ScriptCompiler.ComputeBuildFingerprint(proj);

        File.WriteAllText(assets, "{\"version\":3,\"libraries\":{\"Newtonsoft.Json/13.0.0\":{}}}");
        Assert.NotEqual(before, ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_changes_when_a_referenced_project_assembly_changes()
    {
        // A referenced project with a built assembly under bin/Debug.
        var refDir = Path.Combine(_root, "RefLib");
        Directory.CreateDirectory(refDir);
        File.WriteAllText(
            Path.Combine(refDir, "RefLib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
        );
        var refBin = Path.Combine(
            refDir,
            "bin",
            "Debug",
            "net10.0"
        );
        Directory.CreateDirectory(refBin);
        var refDll = Path.Combine(refBin, "RefLib.dll");
        File.WriteAllText(refDll, "v1");

        // A project that references it.
        var dir = Path.Combine(_root, "Scripts");
        Directory.CreateDirectory(dir);
        var proj = Path.Combine(dir, "Scripts.csproj");
        File.WriteAllText(
            proj,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<ProjectReference Include=\"../RefLib/RefLib.csproj\" />" +
            "</ItemGroup></Project>"
        );
        File.WriteAllText(Path.Combine(dir, "A.cs"), "public class A { }");

        var before = ScriptCompiler.ComputeBuildFingerprint(proj);

        // Simulate an engine/dependency rebuild: the referenced assembly's size changes.
        File.WriteAllText(refDll, "v2-rebuilt-bigger");
        Assert.NotEqual(before, ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_differs_by_configuration()
    {
        var proj = MakeProject();
        Assert.NotEqual(
            ScriptCompiler.ComputeBuildFingerprint(proj),
            ScriptCompiler.ComputeBuildFingerprint(proj, "Release")
        );
    }
}
