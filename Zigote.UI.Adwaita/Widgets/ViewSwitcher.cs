namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwViewSwitcher — one flat toggle per <see cref="AdwViewStack" /> page: 16px icon + bold
///     label, the active page carrying the neutral active fill, badge counts as small accent pills.
///     Clicking a toggle sets <see cref="AdwViewStack.VisibleName" />.
/// </summary>
public sealed class AdwViewSwitcher : ComposedWidget
{
    private readonly AdwViewStack _stack;

    public AdwViewSwitcher(AdwViewStack stack)
    {
        _stack = stack;
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        return new Watch(() =>
            {
                var visible = _stack.Visible.Value;
                // `viewswitcher { border-spacing: 3px }`.
                var row = new Row(
                    spacing: AdwMetrics.ToggleGroupPadding,
                    mainAxisSize: MainAxisSize.Min
                );
                foreach (var page in _stack.Pages)
                    row.Children.Add(
                        Toggle(
                            theme,
                            p,
                            page,
                            page.Name == visible
                        )
                    );
                return row;
            }
        );
    }

    private Widget Toggle(ThemeData theme, AdwColors p, AdwViewStackPage page, bool active)
    {
        var fg = active ? theme.OnBackground : p.DimLabel;
        var content = new Row(spacing: 6f, mainAxisSize: MainAxisSize.Min);
        if (page.IconName is { } icon)
            content.Children.Add(new IconGlyph(icon, AdwMetrics.IconSize, fg));
        content.Children.Add(new Label(page.Title, 14f, fg) { FontWeight = FontWeight.Bold });
        if (page.Badge > 0)
            content.Children.Add(Badge(theme, page.Badge));

        // A header-bar view switcher is made of FLAT toggles: checked is $selected_color (10%),
        // not the 30% a raised button latches to — the switcher has to sit quietly in the chrome.
        var box = new DecoratedBox {
            Radius = AdwMetrics.ControlRadius,
            Fill = AdwStyle.ButtonFill(
                theme,
                AdwButtonStyle.Flat,
                @checked: active
            ),
            // Zero-width strut fixes the height at 34 and cross-centers the content without
            // expanding to the available width (a Center here would).
            Child = new Row(mainAxisSize: MainAxisSize.Min) {
                Children = {
                    new SizedBox(height: AdwMetrics.ButtonHeight),
                    // `> stack > box.wide { padding: 2px 12px }`.
                    new Padding(EdgeInsets.Symmetric(AdwMetrics.RowPaddingX, 2f), content),
                },
            },
        };
        var pressable = new Pressable {
            Child = box,
            FocusRadius = AdwMetrics.ControlRadius,
            SemanticsLabel = page.Title,
            SelectedState = active,
            OnPressed = () => _stack.VisibleName = page.Name,
        };
        pressable.WireFill(
            box,
            theme,
            AdwButtonStyle.Flat,
            @checked: () => active
        );
        return pressable;
    }

    private static Widget Badge(ThemeData theme, int count)
    {
        return new DecoratedBox {
            Radius = AdwMetrics.Pill,
            Fill = theme.Accent,
            Child = new Padding(
                EdgeInsets.Symmetric(6f, 1f),
                new Label(count.ToString(), 10f, theme.OnPrimary) {
                    FontWeight = FontWeight.Bold,
                }
            ),
        };
    }
}

/// <summary>
///     AdwViewSwitcherBar — a bottom bar (title-bar background, top hairline) hosting a centered
///     <see cref="AdwViewSwitcher" />; the narrow-window companion of a header-bar switcher.
/// </summary>
public sealed class AdwViewSwitcherBar : ComposedWidget
{
    private readonly AdwViewStack _stack;

    public AdwViewSwitcherBar(AdwViewStack stack)
    {
        _stack = stack;
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);
        return new Column(
            mainAxisSize: MainAxisSize.Min,
            crossAxisAlignment: CrossAxisAlignment.Stretch
        ) {
            Children = {
                new Container {
                    Height = 1f,
                    Background = p.HeaderbarShade,
                },
                new Container {
                    Height = AdwMetrics.HeaderBarHeight - 1f,
                    Background = theme.TitleBar,
                    Child = new Center { Child = new AdwViewSwitcher(_stack) },
                },
            },
        };
    }
}

/// <summary>
///     AdwInlineViewSwitcher — the libadwaita 1.7 joined-toggle look: one filled capsule (or 9px
///     rounded) container with 1px separators, the active segment carrying the stronger fill.
/// </summary>
public sealed class AdwInlineViewSwitcher : ComposedWidget
{
    private readonly AdwViewStack _stack;
    private bool _round;

    public AdwInlineViewSwitcher(AdwViewStack stack)
    {
        _stack = stack;
    }

    /// <summary>Capsule (pill) shape instead of the 9px rounded rectangle.</summary>
    public bool Round
    {
        get => _round;
        set => this.Set(ref _round, value);
    }

    protected override Widget Build(BuildContext context)
    {
        // This is literally an AdwToggleGroup over the stack's pages — it used to be a third
        // hand-rolled copy of one, with its own (snapping) hover handling.
        return new Watch(() =>
            {
                var visible = _stack.Visible.Value;
                var active = _stack.Pages.FindIndex(p => p.Name == visible);
                return new AdwToggleGroup(
                    [.. _stack.Pages.Select(p => new AdwToggle(p.Title, p.IconName))],
                    Math.Max(active, 0),
                    i => _stack.VisibleName = _stack.Pages[i].Name
                ) { Round = _round };
            }
        );
    }
}
