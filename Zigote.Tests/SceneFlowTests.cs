using Xunit;
using Zigote.Core.Math3D;
using Zigote.Ecs.Scene;
using Zigote.Runtime.Scene;
using Zigote.Scripting;
using Zigote.Scripting.Metadata;

namespace Zigote.Tests;

/// <summary>
///     Headless tests for the Scenes backend: full swap + additive load ride the World spawn
///     machinery, so the authored scene is restored on play stop even after a mid-play scene switch.
/// </summary>
public sealed class SceneFlowTests : IDisposable
{
    private readonly string _dir;
    private readonly EcsSceneBridge _ecs;
    private readonly string _levelPath;
    private readonly SceneNode _root;
    private readonly RuntimeScenesBackend _scenes;
    private readonly RuntimeWorldBackend _world;

    public SceneFlowTests()
    {
        _dir = Directory.CreateTempSubdirectory("zigote-sceneflow-tests").FullName;

        _root = new SceneNode("Scene");
        _root.AddChild(new SceneNode("Player") { Tag = "Player" });
        _root.AddChild(new SceneNode(name: "Camera", kind: NodeKind.Camera));

        var scripts = new ScriptWorld(new ScriptRegistry());
        _ecs = new EcsSceneBridge();
        _ecs.BuildFrom(_root);
        _world = new RuntimeWorldBackend(
            root: _root,
            scripts: scripts,
            ecs: _ecs,
            hooks: null
        );
        _scenes = new RuntimeScenesBackend(world: _world, initialScenePath: "assets/start.scene");

        // A second level on disk: two nodes, one tagged.
        var level = new SceneGraph();
        level.Root.AddChild(
            new SceneNode("Boss") {
                Tag = "Enemy",
                Position = new Vec3(x: 3f, y: 0f, z: 0f),
            }
        );
        level.Root.AddChild(new SceneNode("Exit"));
        _levelPath = Path.Combine(path1: _dir, path2: "level2.scene");
        level.Save(_levelPath);
    }

    public void Dispose()
    {
        _ecs.Dispose();
        try
        {
            Directory.Delete(path: _dir, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void LoadAdditive_GraftsContent_AndDestroyUnloads()
    {
        var container = _scenes.LoadAdditive(_levelPath);

        Assert.True(container.IsValid);
        Assert.Equal(expected: "level2", actual: _world.GetName(container));
        Assert.True(_world.Find("Boss").IsValid);
        Assert.Equal(expected: 1, actual: _world.CountByTag("Enemy"));
        Assert.Equal(
            expected: "assets/start.scene",
            actual: _scenes.Current
        ); // additive does not change Current

        _world.DestroyNow(container);
        Assert.False(_world.Find("Boss").IsValid);
        Assert.Equal(expected: 0, actual: _world.CountByTag("Enemy"));
    }

    [Fact]
    public void Load_IsDeferred_ThenSwapsEverything()
    {
        var player = _world.Find("Player");
        _scenes.Load(scenePath: _levelPath, fadeSeconds: 0f);

        Assert.True(_world.IsAlive(player)); // not yet — applies at the tick's safe point

        _scenes.ApplyPending();

        Assert.False(_world.IsAlive(player));
        Assert.True(_world.Find("Boss").IsValid);
        Assert.Equal(expected: _levelPath, actual: _scenes.Current);
        Assert.Equal(expected: 1, actual: _world.CountByTag("Enemy"));
        Assert.Equal(expected: 0, actual: _world.CountByTag("Player"));
    }

    [Fact]
    public void Load_ThenRestoreSceneEdits_BringsTheAuthoredSceneBack()
    {
        _scenes.Load(scenePath: _levelPath, fadeSeconds: 0f);
        _scenes.ApplyPending();
        Assert.DoesNotContain(collection: _root.Children, filter: c => c.Name == "Player");

        _world.RestoreSceneEdits();

        Assert.Equal(expected: "Player", actual: _root.Children[0].Name); // authored order restored
        Assert.Equal(expected: "Camera", actual: _root.Children[1].Name);
        Assert.DoesNotContain(collection: _root.Children, filter: c => c.Name == "level2");
    }

    [Fact]
    public void Load_WithFade_SwapsOnlyAtFullBlack()
    {
        _scenes.Load(scenePath: _levelPath, fadeSeconds: 0.2f);

        _scenes.ApplyPending();
        Assert.True(_world.Find("Player").IsValid); // still fading out

        _scenes.TickFade(0.1f);
        Assert.InRange(actual: _scenes.FadeAlpha, low: 0.4f, high: 0.6f);
        _scenes.ApplyPending();
        Assert.True(_world.Find("Player").IsValid);

        _scenes.TickFade(0.15f); // past full black
        Assert.Equal(expected: 1f, actual: _scenes.FadeAlpha);
        _scenes.ApplyPending();
        Assert.False(_world.Find("Player").IsValid);
        Assert.True(_world.Find("Boss").IsValid);

        // Fade back in after the swap
        _scenes.TickFade(0.1f);
        Assert.InRange(actual: _scenes.FadeAlpha, low: 0.4f, high: 0.6f);
        _scenes.TickFade(0.2f);
        Assert.Equal(expected: 0f, actual: _scenes.FadeAlpha);
    }

    [Fact]
    public void Load_MissingScene_KeepsPlayingTheCurrentOne()
    {
        _scenes.Load(scenePath: Path.Combine(path1: _dir, path2: "nope.scene"), fadeSeconds: 0f);
        _scenes.ApplyPending();

        Assert.True(_world.Find("Player").IsValid);
        Assert.Equal(expected: "assets/start.scene", actual: _scenes.Current);
    }

    [Fact]
    public void Provider_IsSafeWithoutBackend_AndForwards()
    {
        Scenes.Backend = null;
        Assert.False(Scenes.IsAvailable);
        Assert.Null(Scenes.Current);
        Scenes.Load("x.scene"); // no-throw
        Assert.Equal(expected: EntityHandle.None, actual: Scenes.LoadAdditive("x.scene"));

        Scenes.Backend = _scenes;
        try
        {
            Assert.Equal(expected: "assets/start.scene", actual: Scenes.Current);
            var h = Scenes.LoadAdditive(_levelPath);
            Assert.True(h.IsValid);
        }
        finally
        {
            Scenes.Backend = null;
        }
    }
}
