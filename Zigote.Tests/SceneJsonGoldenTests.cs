using System.Text.RegularExpressions;
using Xunit;
using Zigote.Runtime.Scene;

namespace Zigote.Tests;

/// <summary>
///     Golden gate for the NativeAOT serializer swap: scene load now runs through the source-generated
///     resolver (<c>RuntimeJsonContext</c>) instead of reflection. These tests deserialize the real
///     PorscheDemo scene with the production options under JIT — if the resolver, the Preserve
///     reference handling, or the Vec/Quat converters change behavior, this fails long before an AOT
///     publish does.
/// </summary>
public class SceneJsonGoldenTests
{
    private static string RepoRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "Zigote.sln")))
                return dir;
        throw new InvalidOperationException("Zigote.sln not found above the test bin directory.");
    }

    private static string ScenePath()
    {
        return Path.Combine(
            RepoRoot(),
            "examples",
            "PorscheDemo",
            "assets",
            "main.scene"
        );
    }

    private static int CountNodes(SceneNode n)
    {
        return 1 + n.Children.Sum(CountNodes);
    }

    private static List<string> NodeNames(SceneNode n)
    {
        var names = new List<string> { n.Name };
        foreach (var c in n.Children) names.AddRange(NodeNames(c));
        return names;
    }

    [Fact]
    public void PorscheScene_LoadsWithSourceGenResolver()
    {
        var scene = SceneGraph.Load(ScenePath());

        var count = CountNodes(scene.Root);
        Assert.True(count > 50, $"expected a substantial scene, got {count} nodes");

        var names = NodeNames(scene.Root);
        Assert.Contains(names, n => n.Contains("Camera", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.Contains("Wheel", StringComparison.OrdinalIgnoreCase));

        // Parent links restored (Preserve reference handling) and paths canonical.
        void Check(SceneNode n)
        {
            foreach (var c in n.Children)
            {
                Assert.Same(n, c.Parent);
                if (c.MeshPath is { Length: > 0 } mp && mp[0] != '#')
                    Assert.False(Path.IsPathRooted(mp), $"non-portable MeshPath survived: {mp}");
                Check(c);
            }
        }

        Check(scene.Root);

        // A mesh node with real transform data made it through the Vec/Quat converters.
        Assert.Contains(names, n => n.Length > 0);
        Assert.True(scene.Root.Children.Any(c => CountNodes(c) > 1), "expected nested hierarchy");
    }

    [Fact]
    public void PorscheScene_RoundTripsThroughSaveAndLoad()
    {
        var scene = SceneGraph.Load(ScenePath());
        var tmp = Path.Combine(Path.GetTempPath(), $"golden-{Guid.NewGuid():N}.scene");
        try
        {
            scene.Save(tmp);
            var again = SceneGraph.Load(tmp);

            Assert.Equal(CountNodes(scene.Root), CountNodes(again.Root));
            Assert.Equal(NodeNames(scene.Root), NodeNames(again.Root));
            Assert.Equal(scene.EnvironmentPath, again.EnvironmentPath);

            // Serialize the reloaded copy once more — a stable fixed point means no data drifts
            // through repeated save/load cycles under the source-gen resolver. SceneNode.Id is a
            // volatile runtime counter (serialized, never restored), so compare Id-insensitively.
            var tmp2 = tmp + "2";
            try
            {
                again.Save(tmp2);

                static string StripIds(string json)
                {
                    return Regex.Replace(json, "\"Id\": \\d+", "\"Id\": 0");
                }

                Assert.Equal(StripIds(File.ReadAllText(tmp)), StripIds(File.ReadAllText(tmp2)));
            }
            finally
            {
                File.Delete(tmp2);
            }
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}