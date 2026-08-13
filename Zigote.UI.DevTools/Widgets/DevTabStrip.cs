using Zigote.Core;
using Zigote.UI.Adwaita;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Widgets;

/// <summary>
///     The panel selector: a horizontally scrollable strip of Adwaita pills. It can run to a dozen
///     entries and has to survive a 390px phone, which rules out a joined toggle group — the top-level
///     category switcher, which is three segments, uses one of those instead (see
///     <c>DevToolsView.CategorySwitcher</c>). Tabs/selection are set via <see cref="Set" />, and the
///     strip only rebuilds when they actually change.
/// </summary>
public sealed class DevTabStrip : ComposedWidget
{
    public DevTabStrip(IReadOnlyList<string> tabs, int selected, Action<int> onSelect)
    {
        Tabs = tabs;
        Selected = selected;
        OnSelect = onSelect;
    }

    public Action<int> OnSelect { get; }

    public IReadOnlyList<string> Tabs { get; private set; }

    public int Selected { get; private set; }

    /// <summary>Update the tab labels and/or the selected index, rebuilding only when changed.</summary>
    public void Set(IReadOnlyList<string> tabs, int selected)
    {
        bool changed = selected != Selected || tabs.Count != Tabs.Count;
        if (!changed)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                if (tabs[i] != Tabs[i])
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed) return;
        Tabs = tabs;
        Selected = selected;
        MarkNeedsBuild();
    }

    protected override Widget Build(BuildContext context)
    {
        var t = ThemeProvider.Of(context);
        var row = new Row(spacing: Spacing.Xs, mainAxisSize: MainAxisSize.Min);
        for (int i = 0; i < Tabs.Count; i++)
        {
            int idx = i;
            row.Children.Add(
                Pill(
                    label: Tabs[i],
                    selected: i == Selected,
                    onTap: () => OnSelect(idx),
                    t: t
                )
            );
        }

        float height = DevKit.Compact
            ? ControlMetrics.MinTouchTarget
            : AdwMetrics.CompactControlHeight;
        return new SizedBox(
            height: height,
            child: new ScrollView(row) {
                ScrollVertical = false,
                ScrollHorizontal = true,
                // A bar under a single row of tabs reads as a divider, not as a scrollbar.
                ShowScrollbars = false,
            }
        );
    }

    private static Widget Pill(string label, bool selected, Action onTap, ThemeData t)
    {
        var p = AdwPalette.For(t);
        var box = new DecoratedBox {
            Radius = AdwMetrics.Pill,
            Child = new Padding(
                padding: EdgeInsets.Symmetric(horizontal: Spacing.Md, vertical: Spacing.Xs),
                child: new Center(
                    new Label(
                        text: label,
                        style: selected ? AdwTypography.CaptionHeading : AdwTypography.Caption,
                        color: selected ? t.OnBackground : p.DimLabel
                    ) { MaxLines = 1 }
                )
            ),
        };

        var press = new Pressable {
            Child = box,
            FocusRadius = AdwMetrics.Pill,
            OnPressed = onTap,
            SelectedState = selected,
            SemanticsLabel = label,
        };

        void Recolor()
        {
            box.Fill = selected
                ? p.ButtonFillActive
                : press.Pressed
                    ? p.ButtonFillHover
                    : press.Hovered
                        ? p.ButtonFill
                        : Color.Transparent;
            box.MarkNeedsPaint();
        }

        Recolor();
        press.OnStateChanged = Recolor;
        return press;
    }
}
