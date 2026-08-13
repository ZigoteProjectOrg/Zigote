using System.Diagnostics;
using Zigote.Core;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Semantics;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     Accessibility-tree inspector: the live <see cref="SemanticsNode" /> tree the app would hand a
///     screen reader — role, accessible name/value, and state flags, indented by depth. Lets you verify
///     announcements without a native AT bridge. Rebuilt at ~5 Hz.
/// </summary>
public sealed class SemanticsPanel(App app) : IDevPanel
{
    private const double RefreshMs = 200.0;

    private readonly DevKeyValue _count = new("Nodes");

    private readonly Column _list = new(
        crossAxisAlignment: CrossAxisAlignment.Stretch,
        mainAxisSize: MainAxisSize.Min
    );

    private long _last;

    public string Title => "Semantics";
    public DevCategory Category => DevCategory.Ui2D;

    public Widget Build(BuildContext context)
    {
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                new DevSectionHeader("Accessibility tree"),
                _count,
                new SizedBox(height: Spacing.Xs),
                _list,
            },
        };
    }

    public void Refresh(float dt)
    {
        var now = Stopwatch.GetTimestamp();
        if ((now - _last) * 1000.0 / Stopwatch.Frequency < RefreshMs) return;
        _last = now;

        var t = app.Theme;
        SemanticsNode? root;
        try
        {
            root = app.BuildSemantics();
        }
        catch
        {
            root = null;
        }

        var rows = new List<Widget>();
        var n = 0;
        if (root is not null)
            foreach (var child in root.Children)
                n += Flatten(
                    child,
                    0,
                    rows,
                    t
                );

        _count.Value = n.ToString();
        _count.ValueColor = t.Hint;
        if (rows.Count == 0) rows.Add(new DevNote("No semantic nodes."));
        _list.SetChildren(rows);
    }

    private static int Flatten(SemanticsNode node, int depth, List<Widget> rows, ThemeData t)
    {
        var focused = node.Flags.HasFlag(SemanticsFlags.Focused);
        var disabled = node.Flags.HasFlag(SemanticsFlags.Disabled);
        var text = Describe(node);
        rows.Add(
            new Padding(
                EdgeInsets.Only(depth * 12f),
                new Label(
                    text,
                    DevKit.CaptionSize,
                    focused ? t.Primary : disabled ? t.Hint.WithAlpha(0.6f) : t.OnSurface
                ) {
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                }
            )
        );

        var count = 1;
        foreach (var child in node.Children)
            count += Flatten(
                child,
                depth + 1,
                rows,
                t
            );
        return count;
    }

    private static string Describe(SemanticsNode node)
    {
        var label = node.Label ?? node.Value ?? "";
        var s = node.Role + (label.Length > 0 ? $": {label}" : "");
        var flags = FlagSummary(node.Flags);
        return flags.Length > 0 ? $"{s}  [{flags}]" : s;
    }

    private static string FlagSummary(SemanticsFlags f)
    {
        var parts = new List<string>();
        if (f.HasFlag(SemanticsFlags.Checked)) parts.Add("checked");
        if (f.HasFlag(SemanticsFlags.Selected)) parts.Add("selected");
        if (f.HasFlag(SemanticsFlags.Disabled)) parts.Add("disabled");
        if (f.HasFlag(SemanticsFlags.Focused)) parts.Add("focused");
        return string.Join(", ", parts);
    }
}
