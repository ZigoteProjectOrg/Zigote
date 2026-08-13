using Zigote.Core;
using Zigote.UI.Widgets;

namespace Zigote.UI.Semantics;

/// <summary>
///     Walks a retained widget tree and produces the collapsed <see cref="SemanticsNode" /> tree the
///     accessibility layer consumes. The walk is pure (no widget mutation beyond assigning a stable
///     <see cref="Widget.SemanticsId" />) so it is fully headless-testable — build a widget tree, lay
///     it
///     out, call <see cref="Build" />, and assert on roles/labels/flags without a native window.
///     <para>
///         Collapsing rule: a widget that contributes a
///         <see cref="SemanticsConfiguration.HasContent" />
///         config becomes a node wrapping its descendants' nodes; a transparent widget (empty config)
///         is elided and promotes its descendants into the parent. A widget that sets
///         <see cref="SemanticsConfiguration.IsLeaf" /> (a composed control) stops the descent so its
///         decorative inner widgets never produce duplicate nodes.
///         <see cref="Widget.ExcludeSemantics" />
///         drops a whole subtree.
///     </para>
/// </summary>
public static class SemanticsBuilder
{
    private static int _nextId = 1;

    /// <summary>
    ///     Build a single synthetic root node (role <see cref="SemanticsRole.Group" />, covering
    ///     <paramref name="screen" />) whose children are the collapsed semantics of
    ///     <paramref name="root" />
    ///     followed by each overlay (so a modal dialog's nodes sit above the page, matching paint order).
    /// </summary>
    public static SemanticsNode Build(Widget? root, IReadOnlyList<Widget> overlays, Size screen)
    {
        var node = new SemanticsNode(
            0,
            SemanticsRole.Group,
            new Rect(
                0f,
                0f,
                screen.Width,
                screen.Height
            )
        );
        if (root is not null) node.Children.AddRange(Collect(root));
        foreach (var overlay in overlays)
            node.Children.AddRange(Collect(overlay));
        return node;
    }

    private static List<SemanticsNode> Collect(Widget w)
    {
        if (w.ExcludeSemantics) return [];

        var cfg = new SemanticsConfiguration();
        w.DescribeSemantics(cfg);

        var childNodes = new List<SemanticsNode>();
        if (!cfg.IsLeaf)
            // Visible children only — hidden TabView pages / covered navigator routes must not be
            // announced by a screen reader (parity with focus traversal).
            foreach (var child in w.GetVisibleChildren())
                childNodes.AddRange(Collect(child));

        if (!cfg.HasContent)
            return childNodes; // transparent — hoist descendants into the parent

        if (w.SemanticsId == 0) w.SemanticsId = _nextId++;
        var node = new SemanticsNode(w.SemanticsId, cfg.Role, w.Bounds) {
            Label = cfg.Label,
            Value = cfg.Value,
            Hint = cfg.Hint,
            Flags = cfg.Flags,
            Actions = cfg.Actions,
            Source = w,
        };
        node.Children.AddRange(childNodes);
        return [node];
    }
}
