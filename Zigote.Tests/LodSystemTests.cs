using Xunit;
using Zigote.Core.Lod;
using Zigote.Core.Math3D;
using Zigote.Runtime.Scene;

namespace Zigote.Tests;

/// <summary>
///     Headless coverage of the generic LOD/distance-cull logic: the pure <see cref="LodMath" />
///     selection rules and the <see cref="LodSystem" /> scene walk (via an injected sink, so no native
///     engine is touched). The camera sits at the origin; node X-positions set the camera distance.
/// </summary>
public class LodSystemTests
{
    [Theory]
    [InlineData(0f, 30f, false)] // no limit → never culled
    [InlineData(50f, 30f, false)] // within budget → visible
    [InlineData(50f, 50f, false)] // exactly at budget → visible
    [InlineData(50f, 70f, true)] // beyond budget → culled
    public void CulledByDistance_respects_budget(float maxDistance, float distance, bool culled)
    {
        Assert.Equal(culled, LodMath.CulledByDistance(maxDistance, distance));
    }

    [Fact]
    public void SelectLevel_picks_nearest_covering_then_fallback()
    {
        float[] budgets = [20f, 60f, 0f]; // near, mid, fallback ("covers all")
        Assert.Equal(0, LodMath.SelectLevel(budgets, 10f)); // high detail
        Assert.Equal(1, LodMath.SelectLevel(budgets, 40f)); // mid
        Assert.Equal(2, LodMath.SelectLevel(budgets, 500f)); // fallback
    }

    [Fact]
    public void SelectLevel_returns_minus_one_when_nothing_covers()
    {
        float[] budgets = [20f, 60f]; // no fallback level
        Assert.Equal(-1, LodMath.SelectLevel(budgets, 500f)); // whole group culled
    }

    [Fact]
    public void Apply_distance_culls_a_node_beyond_its_budget()
    {
        var root = new SceneNode("root");
        var node = new SceneNode("prop", NodeKind.Mesh) {
            Position = new Vec3(70f, 0f, 0f),
            LodMaxDistance = 50f,
        };
        root.AddChild(node);
        var sink = new RecordingSink();

        LodSystem.Apply(root, Vec3.Zero, sink);
        Assert.False(sink["prop"]); // 70 > 50 → hidden

        node.Position = new Vec3(30f, 0f, 0f);
        LodSystem.Apply(root, Vec3.Zero, sink);
        Assert.True(sink["prop"]); // 30 <= 50 → visible
    }

    [Fact]
    public void Apply_lod_group_shows_exactly_one_level()
    {
        var root = new SceneNode("root");
        var group = new SceneNode("group") {
            LodGroup = true,
            Position = new Vec3(40f, 0f, 0f),
        };
        group.AddChild(new SceneNode("near", NodeKind.Mesh) { LodMaxDistance = 20f });
        group.AddChild(new SceneNode("mid", NodeKind.Mesh) { LodMaxDistance = 60f });
        group.AddChild(new SceneNode("far", NodeKind.Mesh) { LodMaxDistance = 0f }); // fallback
        root.AddChild(group);
        var sink = new RecordingSink();

        LodSystem.Apply(root, Vec3.Zero, sink); // distance 40 → "mid"
        Assert.False(sink["near"]);
        Assert.True(sink["mid"]);
        Assert.False(sink["far"]);

        group.Position = new Vec3(500f, 0f, 0f);
        LodSystem.Apply(root, Vec3.Zero, sink); // beyond near+mid → fallback "far"
        Assert.False(sink["near"]);
        Assert.False(sink["mid"]);
        Assert.True(sink["far"]);
    }

    [Fact]
    public void Apply_hides_whole_subtree_of_a_culled_ancestor()
    {
        var root = new SceneNode("root");
        var parent = new SceneNode("parent", NodeKind.Mesh) {
            Position = new Vec3(100f, 0f, 0f),
            LodMaxDistance = 50f,
        };
        var child = new SceneNode("child", NodeKind.Mesh); // no own limit
        parent.AddChild(child);
        root.AddChild(parent);
        var sink = new RecordingSink();

        LodSystem.Apply(root, Vec3.Zero, sink);
        Assert.False(sink["parent"]); // ancestor beyond budget
        Assert.False(sink["child"]); // inherits hidden
    }

    // ── Demand-residency policy + LodSystem residency walk ──────────────────────

    [Theory]
    [InlineData(10f, ResidencyDecision.Want)] // within load band
    [InlineData(50f, ResidencyDecision.Want)] // exactly at load distance
    [InlineData(75f, ResidencyDecision.Keep)] // hysteresis band → untouched
    [InlineData(100f, ResidencyDecision.Drop)] // at evict distance
    [InlineData(200f, ResidencyDecision.Drop)] // far → drop
    public void StreamingPolicy_decides_by_distance_bands(float distance,
        ResidencyDecision expected)
    {
        var policy = new StreamingPolicy(50f, 100f);
        Assert.Equal(expected, policy.Decide(distance));
    }

    [Fact]
    public void StreamingPolicy_unbounded_always_wants_never_drops()
    {
        var p = StreamingPolicy.Unbounded;
        Assert.False(p.Enabled);
        Assert.Equal(ResidencyDecision.Want, p.Decide(0f));
        Assert.Equal(ResidencyDecision.Want, p.Decide(1e9f));
    }

    [Fact]
    public void StreamingPolicy_hysteresis_evict_beyond_load()
    {
        var p = StreamingPolicy.WithHysteresis(100f, 1.5f);
        Assert.Equal(100f, p.LoadDistance);
        Assert.Equal(150f, p.EvictDistance);
        Assert.Equal(ResidencyDecision.Keep, p.Decide(120f)); // in the band
    }

    [Fact]
    public void Apply_residency_wants_near_meshes_drops_far_ones()
    {
        var root = new SceneNode("root");
        var near = new SceneNode("near", NodeKind.Mesh) { Position = new Vec3(10f, 0f, 0f) };
        var far = new SceneNode("far", NodeKind.Mesh) { Position = new Vec3(300f, 0f, 0f) };
        var empty =
            new SceneNode("empty") { Position = new Vec3(10f, 0f, 0f) }; // not a mesh → ignored
        root.AddChild(near);
        root.AddChild(far);
        root.AddChild(empty);

        var vis = new RecordingSink();
        var res = new RecordingResidency();
        LodSystem.Apply(
            root,
            Vec3.Zero,
            vis,
            res,
            new StreamingPolicy(50f, 100f)
        );

        Assert.Equal("want", res["near"]);
        Assert.Equal("drop", res["far"]);
        Assert.False(res.Saw("empty")); // non-mesh nodes are not streamable
    }

    [Fact]
    public void Apply_without_residency_sink_is_unchanged()
    {
        // The two-arg overload must still work (no residency) — regression guard for the seam.
        var root = new SceneNode("root");
        root.AddChild(
            new SceneNode("m", NodeKind.Mesh) {
                Position = new Vec3(10f, 0f, 0f),
                LodMaxDistance = 50f,
            }
        );
        var sink = new RecordingSink();
        LodSystem.Apply(root, Vec3.Zero, sink);
        Assert.True(sink["m"]);
    }

    private sealed class RecordingSink : LodSystem.IVisibilitySink
    {
        private readonly Dictionary<string, bool> _visible = new();
        public bool this[string name] => _visible[name];

        public void Set(SceneNode node, bool visible)
        {
            _visible[node.Name] = visible;
        }
    }

    private sealed class RecordingResidency : LodSystem.IResidencySink
    {
        private readonly Dictionary<string, string> _calls = new();
        public string this[string name] => _calls[name];

        public void Want(SceneNode node, float distance)
        {
            _calls[node.Name] = "want";
        }

        public void Drop(SceneNode node)
        {
            _calls[node.Name] = "drop";
        }

        public bool Saw(string name)
        {
            return _calls.ContainsKey(name);
        }
    }
}