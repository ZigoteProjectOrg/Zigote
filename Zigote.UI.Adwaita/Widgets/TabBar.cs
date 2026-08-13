namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwTabBar — the browser-style tab strip for an <see cref="AdwTabView" />: equal-width tabs
///     (100–200px) with a centered icon+title, a close button shown on hover / selection, 1px
///     separators, the selected tab lifted to the window background over the title-bar strip.
///     <para>
///         ponytail: no drag-to-reorder and no overflow scrolling. Once the tabs no longer fit at
///         their 100px minimum the strip simply clips at the right edge — the tabs past it are
///         unreachable, with no scroll arrow or overflow menu to say so. Add pointer-drag
///         reordering and a scrolling strip if a real app outgrows this.
///     </para>
/// </summary>
public sealed class AdwTabBar : ComposedWidget
{
    private const float StripHeight = 37f; // + 1px hairline = 38 total
    private const float PinnedWidth = 40f;

    private readonly AdwTabView _view;

    public AdwTabBar(AdwTabView view) => _view = view;

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
                    Height = StripHeight,
                    Background = theme.TitleBar,
                    Child = new ClipRect(new Watch(() => BuildStrip(theme: theme, p: p))),
                },
                new Container {
                    Height = 1f,
                    Background = p.HeaderbarShade,
                },
            },
        };
    }

    private Widget BuildStrip(ThemeData theme, AdwColors p)
    {
        _view.PagesChanged.Depend();
        int selected = _view.Selected.Value;
        if (_view.Pages.Count == 0) return new SizedBox();

        return new LayoutBuilder((_, bc) =>
            {
                int pinned = _view.Pages.Count(page => page.Pinned);
                int regular = _view.Pages.Count - pinned;
                int separators = _view.Pages.Count - 1;
                float avail = float.IsFinite(bc.MaxWidth) ? bc.MaxWidth : regular * 200f;
                avail -= (pinned * PinnedWidth) + separators;
                float tabWidth = regular > 0
                    ? Math.Clamp(value: avail / regular, min: 100f, max: 200f)
                    : 0f;

                var row = new Row(mainAxisSize: MainAxisSize.Min);
                for (int i = 0; i < _view.Pages.Count; i++)
                {
                    if (i > 0)
                        // `tabbox > separator { margin-top: 3px; margin-bottom: 3px }`, hidden
                        // where it would fence in the selected tab's own rounded fill.
                    {
                        row.Children.Add(
                            new Container {
                                Width = 1f,
                                Height = StripHeight - (AdwMetrics.ToggleGroupPadding * 2f),
                                Background = i == selected || i - 1 == selected
                                    ? Color.Transparent
                                    : theme.Separator,
                            }
                        );
                    }

                    row.Children.Add(
                        Tab(
                            theme: theme,
                            p: p,
                            index: i,
                            selected: i == selected,
                            tabWidth: tabWidth
                        )
                    );
                }

                return row;
            }
        );
    }

    private Widget Tab(ThemeData theme, AdwColors p, int index, bool selected, float tabWidth)
    {
        var page = _view.Pages[index];
        var fg = selected ? theme.OnBackground : p.DimLabel;

        var content = new Row(spacing: 6f, mainAxisSize: MainAxisSize.Min);
        if (page.IconName is { } icon)
            content.Children.Add(new IconGlyph(glyph: icon, size: 14f, color: fg));
        if (!page.Pinned)
        {
            content.Children.Add(
                new Label(text: page.Title, fontSize: 13f, color: fg) {
                    FontWeight = selected ? FontWeight.Medium : FontWeight.Normal,
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                }
            );
        }

        // `tab { border-radius: $button_radius }` — an Adwaita tab is a rounded chip on the strip,
        // not a notebook page fused to the content below it.
        var body = new DecoratedBox {
            Radius = AdwMetrics.ControlRadius,
            Child = new Center {
                // Symmetric side padding keeps a centered title clear of the close button.
                Child = new Padding(
                    padding: EdgeInsets.Symmetric(page.Pinned ? 0f : 22f),
                    child: content
                ),
            },
        };
        var select = new Pressable {
            Child = body,
            FocusRadius = AdwMetrics.ControlRadius,
            SemanticsLabel = page.Title,
            SelectedState = selected,
            OnPressed = () => _view.SelectedIndex = index,
        };

        // `tab:selected { background-color: $selected_color }`, climbing to 13/19% under the
        // pointer; unselected tabs ride the 7/16% ladder.
        var tabFill = new FillTransition(c =>
            {
                body.Fill = c;
                body.MarkNeedsPaint();
            }
        );
        tabFill.Snap(
            AdwStyle.SidebarRowFill(
                theme: theme,
                hovered: false,
                pressed: false,
                selected: selected
            )
        );
        select.OnStateChanged = () => tabFill.Target(
            AdwStyle.SidebarRowFill(
                theme: theme,
                hovered: select.Hovered,
                pressed: select.Pressed,
                selected: selected
            )
        );

        if (page.Pinned)
            return new SizedBox(width: PinnedWidth, height: StripHeight, child: select);

        // Close button — overlaid (not nested: Pressable captures its whole rect) and revealed on
        // hover or selection via a retained Opacity.
        // `tab button.image-button { min-width: 24px; border-radius: 99px }`.
        var closeBox = new DecoratedBox {
            Radius = AdwMetrics.Pill,
            Fill = Color.Transparent,
            Child = SizedBox.Square(
                size: 24f,
                child: new Center {
                    Child = new IconGlyph(glyph: Icons.Close, size: 12f, color: p.DimLabel),
                }
            ),
        };
        var close = new Pressable {
            Child = closeBox,
            FocusRadius = AdwMetrics.Pill,
            SemanticsLabel = "Close tab",
            OnPressed = () => _view.Close(page),
        };
        var closeReveal = new Opacity(opacity: selected ? 1f : 0f, child: close);
        close.WireFill(box: closeBox, theme: theme);

        // Hovering either pressable reveals the close button, so chain onto whatever handler
        // the tab fill / WireFill installed rather than overwriting it.
        Chain(select);
        Chain(close);

        return new SizedBox(
            width: tabWidth,
            height: StripHeight,
            child: new Stack {
                Children = {
                    select,
                    new Align(
                        alignment: Alignment.CenterRight,
                        child: new Padding(padding: EdgeInsets.Only(right: 6f), child: closeReveal)
                    ),
                },
            }
        );

        void Chain(Pressable pressable)
        {
            var fill = pressable.OnStateChanged;
            pressable.OnStateChanged = () =>
            {
                fill?.Invoke();
                closeReveal.Value = selected || select.Hovered || close.Hovered ? 1f : 0f;
                closeReveal.MarkNeedsPaint();
            };
        }
    }
}
