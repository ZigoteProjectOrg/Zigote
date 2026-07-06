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
        _root.AddChild(new SceneNode("Camera", NodeKind.Camera));

        var scripts = new ScriptWorld(new ScriptRegistry());
        _ecs = new EcsSceneBridge();
        _ecs.BuildFrom(_root);
        _world = new RuntimeWorldBackend(
            _root,
            scripts,
            _ecs,
            null
        );
        _scenes = new RuntimeScenesBackend(_world, "assets/start.scene");

        // A second level on disk: two nodes, one tagged.
        var level = new SceneGraph();
        level.Root.AddChild(
            new SceneNode("Boss") {
                Tag = "Enemy",
                Position = new Vec3(3f, 0f, 0f),
            }
        );
        level.Root.AddChild(new SceneNode("Exit"));
        _levelPath = Path.Combine(_dir, "level2.scene");
        level.Save(_levelPath);
    }

    public void Dispose()
    {
        _ecs.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void LoadAdditive_GraftsContent_AndDestroyUnloads()
    {
        var container = _scenes.LoadAdditive(_levelPath);

        Assert.True(container.IsValid);
        Assert.Equal("level2", _world.GetName(container));
        Assert.True(_world.Find("Boss").IsValid);
        Assert.Equal(1, _world.CountByTag("Enemy"));
        Assert.Equal("assets/start.scene", _scenes.Current); // additive does not change Current

        _world.DestroyNow(container);
        Assert.False(_world.Find("Boss").IsValid);
        Assert.Equal(0, _world.CountByTag("Enemy"));
    }

    [Fact]
    public void Load_IsDeferred_ThenSwapsEverything()
    {
        var player = _world.Find("Player");
        _scenes.Load(_levelPath, 0f);

        Assert.True(_world.IsAlive(player)); // not yet — applies at the tick's safe point

        _scenes.ApplyPending();

        Assert.False(_world.IsAlive(player));
        Assert.True(_world.Find("Boss").IsValid);
        Assert.Equal(_levelPath, _scenes.Current);
        Assert.Equal(1, _world.CountByTag("Enemy"));
        Assert.Equal(0, _world.CountByTag("Player"));
    }

    [Fact]
    public void Load_ThenRestoreSceneEdits_BringsTheAuthoredSceneBack()
    {
        _scenes.Load(_levelPath, 0f);
        _scenes.ApplyPending();
        Assert.DoesNotContain(_root.Children, c => c.Name == "Player");

        _world.RestoreSceneEdits();

        Assert.Equal("Player", _root.Children[0].Name); // authored order restored
        Assert.Equal("Camera", _root.Children[1].Name);
        Assert.DoesNotContain(_root.Children, c => c.Name == "level2");
    }

    [Fact]
    public void Load_WithFade_SwapsOnlyAtFullBlack()
    {
        _scenes.Load(_levelPath, 0.2f);

        _scenes.ApplyPending();
        Assert.True(_world.Find("Player").IsValid); // still fading out

        _scenes.TickFade(0.1f);
        Assert.InRange(_scenes.FadeAlpha, 0.4f, 0.6f);
        _scenes.ApplyPending();
        Assert.True(_world.Find("Player").IsValid);

        _scenes.TickFade(0.15f); // past full black
        Assert.Equal(1f, _scenes.FadeAlpha);
        _scenes.ApplyPending();
        Assert.False(_world.Find("Player").IsValid);
        Assert.True(_world.Find("Boss").IsValid);

        // Fade back in after the swap
        _scenes.TickFade(0.1f);
        Assert.InRange(_scenes.FadeAlpha, 0.4f, 0.6f);
        _scenes.TickFade(0.2f);
        Assert.Equal(0f, _scenes.FadeAlpha);
    }

    [Fact]
    public void Load_MissingScene_KeepsPlayingTheCurrentOne()
    {
        _scenes.Load(Path.Combine(_dir, "nope.scene"), 0f);
        _scenes.ApplyPending();

        Assert.True(_world.Find("Player").IsValid);
        Assert.Equal("assets/start.scene", _scenes.Current);
    }

    [Fact]
    public void Provider_IsSafeWithoutBackend_AndForwards()
    {
        Scenes.Backend = null;
        Assert.False(Scenes.IsAvailable);
        Assert.Null(Scenes.Current);
        Scenes.Load("x.scene"); // no-throw
        Assert.Equal(EntityHandle.None, Scenes.LoadAdditive("x.scene"));

        Scenes.Backend = _scenes;
        try
        {
            Assert.Equal("assets/start.scene", Scenes.Current);
            var h = Scenes.LoadAdditive(_levelPath);
            Assert.True(h.IsValid);
        }
        finally
        {
            Scenes.Backend = null;
        }
    }
}