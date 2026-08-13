using Zigote.Core.Engine;
using Zigote.Core.Lod;
using Zigote.Core.Math3D;

namespace Zigote.Runtime.Scene;

/// <summary>
///     Generic, project-agnostic level-of-detail + distance culling for the editor scene. Walks the
///     scene each frame from the active camera and toggles native mesh visibility (a real draw-call
///     cull — see <see cref="ZigoteEngine.SceneSetNodeVisible" />), composing with the native frustum
///     culling that removes off-screen meshes. Two rules, both driven by fields on
///     <see cref="SceneNode" />:
///     <list type="bullet">
///         <item>
///             <see cref="SceneNode.LodGroup" />: show exactly one child (the nearest level whose
///             <see cref="SceneNode.LodMaxDistance" /> still reaches the camera), hide the rest; cull
///             the
///             whole group when beyond every level.
///         </item>
///         <item>
///             <see cref="SceneNode.LodMaxDistance" /> &gt; 0: hide the node (and subtree) past that
///             distance.
///         </item>
///     </list>
///     The distance logic lives in <see cref="LodMath" /> (headless-testable); this class is the
///     scene-walking glue. The visibility sink is injectable so the selection can be unit-tested.
/// </summary>
public static class LodSystem
{
    /// <summary>
    ///     Apply LOD/cull to the scene for a camera at <paramref name="cameraPos" />, driving native
    ///     visibility.
    /// </summary>
    public static void Apply(SceneNode root, Vec3 cameraPos)
    {
        Apply(root, cameraPos, NativeSink.Instance);
    }

    /// <summary>Apply with an explicit sink (used by tests to record the resulting visibility).</summary>
    public static void Apply(SceneNode root, Vec3 cameraPos, IVisibilitySink sink)
    {
        Visit(
            root,
            cameraPos,
            sink,
            null,
            StreamingPolicy.Unbounded,
            true,
            Transform3D.Identity
        );
    }

    /// <summary>
    ///     Apply LOD/cull <em>and</em> demand residency in the same walk: alongside visibility, each
    ///     mesh node's camera distance is fed to <paramref name="policy" /> and the resulting
    ///     load/drop decision is pushed to <paramref name="residency" />. With
    ///     <see cref="StreamingPolicy.Unbounded" /> (the editor default) nothing is dropped.
    /// </summary>
    public static void Apply(SceneNode root, Vec3 cameraPos, IVisibilitySink sink,
        IResidencySink residency, StreamingPolicy policy)
    {
        Visit(
            root,
            cameraPos,
            sink,
            residency,
            policy,
            true,
            Transform3D.Identity
        );
    }

    /// <summary>
    ///     Apply the default native visibility cull plus demand residency (the common streaming
    ///     call).
    /// </summary>
    public static void Apply(SceneNode root, Vec3 cameraPos, IResidencySink residency,
        StreamingPolicy policy)
    {
        Visit(
            root,
            cameraPos,
            NativeSink.Instance,
            residency,
            policy,
            true,
            Transform3D.Identity
        );
    }

    private static void Visit(SceneNode node, Vec3 cam, IVisibilitySink sink,
        IResidencySink? residency,
        StreamingPolicy policy, bool parentVisible, Transform3D parentWorld)
    {
        var world = Transform3D.Combine(
            parentWorld,
            new Transform3D(node.Position, node.Rotation, node.Scale)
        );
        var distance = (world.Position - cam).Length();

        // LOD group: its direct children are mutually-exclusive detail levels.
        if (node is { LodGroup: true, Children.Count: > 0 })
        {
            var n = node.Children.Count;
            var budgets = n <= 32 ? stackalloc float[n] : new float[n];
            for (var i = 0; i < n; i++) budgets[i] = node.Children[i].LodMaxDistance;
            var selected = LodMath.SelectLevel(budgets, distance);

            for (var i = 0; i < n; i++)
            {
                var child = node.Children[i];
                if (i == selected && parentVisible)
                    Visit(
                        child,
                        cam,
                        sink,
                        residency,
                        policy,
                        true,
                        world
                    ); // chosen level
                else
                    HideSubtree(child, sink); // unchosen level, or whole group culled
            }

            return;
        }

        var visible = parentVisible && !LodMath.CulledByDistance(node.LodMaxDistance, distance);
        sink.Set(node, visible);
        ApplyResidency(
            node,
            distance,
            residency,
            policy
        );
        foreach (var child in node.Children)
            Visit(
                child,
                cam,
                sink,
                residency,
                policy,
                visible,
                world
            );
    }

    private static void ApplyResidency(SceneNode node, float distance, IResidencySink? residency,
        StreamingPolicy policy)
    {
        if (residency is null || !policy.Enabled || node.Kind != NodeKind.Mesh) return;
        switch (policy.Decide(distance))
        {
            case ResidencyDecision.Want:
                residency.Want(node, distance);
                break;
            case ResidencyDecision.Drop:
                residency.Drop(node);
                break;
        }
    }

    private static void HideSubtree(SceneNode node, IVisibilitySink sink)
    {
        sink.Set(node, false);
        foreach (var child in node.Children)
            HideSubtree(child, sink);
    }

    public interface IVisibilitySink
    {
        void Set(SceneNode node, bool visible);
    }

    /// <summary>
    ///     Receives per-frame residency decisions for mesh nodes. Both calls must be idempotent — the
    ///     policy re-emits <see cref="Want" /> every frame a node stays in range and <see cref="Drop" />
    ///     every frame it stays out — so an implementation typically acquires/releases an
    ///     <c>AssetHandle</c> only on the transition (tracked in a per-node side-table).
    /// </summary>
    public interface IResidencySink
    {
        void Want(SceneNode node, float distance);
        void Drop(SceneNode node);
    }

    /// <summary>Drives real native visibility, deduping so an unchanged node costs no FFI call per frame.</summary>
    private sealed class NativeSink : IVisibilitySink
    {
        public static readonly NativeSink Instance = new();

        public void Set(SceneNode node, bool visible)
        {
            // Visibility only affects mesh renderers; containers/lights/cameras have nothing to cull.
            if (node.Kind != NodeKind.Mesh || node.Handle == 0) return;
            if (node.LodVisibleApplied == visible) return;
            node.LodVisibleApplied = visible;
            ZigoteEngine.Instance?.SceneSetNodeVisible(node.Handle, visible);
        }
    }
}
