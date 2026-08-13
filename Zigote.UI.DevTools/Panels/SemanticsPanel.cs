using System.Diagnostics;
using Zigote.Core;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Host;
using Zigote.UI.Semantics;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     Accessibility-tree inspector: the live <see cref="SemanticsNode" /> tree the app would hand a
///     screen reader — role, accessible name/value, and state flags, indented by depth. Lets you
///     verify
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
        long now = Stopwatch.GetTimestamp();
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
        int n = 0;
        if (root is not null)
        {
            foreach (var child in root.Children)
            {
                n += Flatten(
                    node: child,
                    depth: 0,
                    rows: rows,
                    t: t
                );
            }
        }

        _count.Value = n.ToString();
        _count.ValueColor = t.Hint;
        if (rows.Count == 0) rows.Add(new DevNote("No semantic nodes."));
        _list.SetChildren(rows);
    }

    private static int Flatten(SemanticsNode node, int depth, List<Widget> rows, ThemeData t)
    {
        bool focused = node.Flags.HasFlag(SemanticsFlags.Focused);
        bool disabled = node.Flags.HasFlag(SemanticsFlags.Disabled);
        string text = Describe(node);
        rows.Add(
            new Padding(
                padding: EdgeInsets.Only(depth * 12f),
                child: new Label(
                    text: text,
                    fontSize: DevKit.CaptionSize,
                    color: focused ? t.Primary : disabled ? t.Hint.WithAlpha(0.6f) : t.OnSurface
                ) {
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                }
            )
        );

        int count = 1;
        foreach (var child in node.Children)
        {
            count += Flatten(
                node: child,
                depth: depth + 1,
                rows: rows,
                t: t
            );
        }

        return count;
    }

    private static string Describe(SemanticsNode node)
    {
        string label = node.Label ?? node.Value ?? "";
        string s = node.Role + (label.Length > 0 ? $": {label}" : "");
        string flags = FlagSummary(node.Flags);
        return flags.Length > 0 ? $"{s}  [{flags}]" : s;
    }

    private static string FlagSummary(SemanticsFlags f)
    {
        var parts = new List<string>();
        if (f.HasFlag(SemanticsFlags.Checked)) parts.Add("checked");
        if (f.HasFlag(SemanticsFlags.Selected)) parts.Add("selected");
        if (f.HasFlag(SemanticsFlags.Disabled)) parts.Add("disabled");
        if (f.HasFlag(SemanticsFlags.Focused)) parts.Add("focused");
        return string.Join(separator: ", ", values: parts);
    }
}
