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
        Path.Combine(
            path1: Path.GetTempPath(),
            path2: "zigote-scripthash-" + Guid.NewGuid().ToString("N")
        );

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(path: _root, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private string MakeProject(string name = "Scripts")
    {
        string dir = Path.Combine(path1: _root, path2: name);
        Directory.CreateDirectory(dir);
        string csproj = Path.Combine(path1: dir, path2: name + ".csproj");
        File.WriteAllText(
            path: csproj,
            contents: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
                      "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
        );
        File.WriteAllText(
            path: Path.Combine(path1: dir, path2: "A.cs"),
            contents: "public class A { }"
        );
        return csproj;
    }

    [Fact]
    public void Fingerprint_is_stable_when_nothing_changes()
    {
        string proj = MakeProject();
        Assert.Equal(
            expected: ScriptCompiler.ComputeBuildFingerprint(proj),
            actual: ScriptCompiler.ComputeBuildFingerprint(proj)
        );
    }

    [Fact]
    public void Fingerprint_changes_when_a_source_file_content_changes()
    {
        string proj = MakeProject();
        string before = ScriptCompiler.ComputeBuildFingerprint(proj);
        File.WriteAllText(
            path: Path.Combine(path1: Path.GetDirectoryName(proj)!, path2: "A.cs"),
            contents: "public class A { int x; }"
        );
        Assert.NotEqual(expected: before, actual: ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_changes_when_a_source_file_is_added()
    {
        string proj = MakeProject();
        string before = ScriptCompiler.ComputeBuildFingerprint(proj);
        File.WriteAllText(
            path: Path.Combine(path1: Path.GetDirectoryName(proj)!, path2: "B.cs"),
            contents: "public class B { }"
        );
        Assert.NotEqual(expected: before, actual: ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_changes_when_the_csproj_changes()
    {
        string proj = MakeProject();
        string before = ScriptCompiler.ComputeBuildFingerprint(proj);
        File.AppendAllText(path: proj, contents: "<!-- a comment -->");
        Assert.NotEqual(expected: before, actual: ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_ignores_obj_and_bin_artefacts()
    {
        string proj = MakeProject();
        string dir = Path.GetDirectoryName(proj)!;
        string before = ScriptCompiler.ComputeBuildFingerprint(proj);

        Directory.CreateDirectory(Path.Combine(path1: dir, path2: "obj"));
        File.WriteAllText(
            path: Path.Combine(path1: dir, path2: "obj", path3: "Scripts.AssemblyInfo.cs"),
            contents: "// generated"
        );
        Directory.CreateDirectory(Path.Combine(path1: dir, path2: "bin", path3: "Debug"));
        File.WriteAllText(
            path: Path.Combine(
                path1: dir,
                path2: "bin",
                path3: "Debug",
                path4: "leftover.cs"
            ),
            contents: "// stale output"
        );

        Assert.Equal(expected: before, actual: ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_changes_when_nuget_restore_state_changes()
    {
        string proj = MakeProject();
        string assets = Path.Combine(
            path1: Path.GetDirectoryName(proj)!,
            path2: "obj",
            path3: "project.assets.json"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        File.WriteAllText(path: assets, contents: "{\"version\":3}");
        string before = ScriptCompiler.ComputeBuildFingerprint(proj);

        File.WriteAllText(
            path: assets,
            contents: "{\"version\":3,\"libraries\":{\"Newtonsoft.Json/13.0.0\":{}}}"
        );
        Assert.NotEqual(expected: before, actual: ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_changes_when_a_referenced_project_assembly_changes()
    {
        // A referenced project with a built assembly under bin/Debug.
        string refDir = Path.Combine(path1: _root, path2: "RefLib");
        Directory.CreateDirectory(refDir);
        File.WriteAllText(
            path: Path.Combine(path1: refDir, path2: "RefLib.csproj"),
            contents: "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"
        );
        string refBin = Path.Combine(
            path1: refDir,
            path2: "bin",
            path3: "Debug",
            path4: "net10.0"
        );
        Directory.CreateDirectory(refBin);
        string refDll = Path.Combine(path1: refBin, path2: "RefLib.dll");
        File.WriteAllText(path: refDll, contents: "v1");

        // A project that references it.
        string dir = Path.Combine(path1: _root, path2: "Scripts");
        Directory.CreateDirectory(dir);
        string proj = Path.Combine(path1: dir, path2: "Scripts.csproj");
        File.WriteAllText(
            path: proj,
            contents: "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                      "<ProjectReference Include=\"../RefLib/RefLib.csproj\" />" +
                      "</ItemGroup></Project>"
        );
        File.WriteAllText(
            path: Path.Combine(path1: dir, path2: "A.cs"),
            contents: "public class A { }"
        );

        string before = ScriptCompiler.ComputeBuildFingerprint(proj);

        // Simulate an engine/dependency rebuild: the referenced assembly's size changes.
        File.WriteAllText(path: refDll, contents: "v2-rebuilt-bigger");
        Assert.NotEqual(expected: before, actual: ScriptCompiler.ComputeBuildFingerprint(proj));
    }

    [Fact]
    public void Fingerprint_differs_by_configuration()
    {
        string proj = MakeProject();
        Assert.NotEqual(
            expected: ScriptCompiler.ComputeBuildFingerprint(proj),
            actual: ScriptCompiler.ComputeBuildFingerprint(projectPath: proj, config: "Release")
        );
    }
}
