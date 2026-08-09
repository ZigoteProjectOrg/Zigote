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

    public AdwTabBar(AdwTabView view)
    {
        _view = view;
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
                    Height = StripHeight,
                    Background = theme.TitleBar,
                    Child = new ClipRect(new Watch(() => BuildStrip(theme, p))),
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
        var selected = _view.Selected.Value;
        if (_view.Pages.Count == 0) return new SizedBox();

        return new LayoutBuilder((_, bc) =>
            {
                var pinned = _view.Pages.Count(page => page.Pinned);
                var regular = _view.Pages.Count - pinned;
                var separators = _view.Pages.Count - 1;
                var avail = float.IsFinite(bc.MaxWidth) ? bc.MaxWidth : regular * 200f;
                avail -= pinned * PinnedWidth + separators;
                var tabWidth = regular > 0 ? Math.Clamp(avail / regular, 100f, 200f) : 0f;

                var row = new Row(mainAxisSize: MainAxisSize.Min);
                for (var i = 0; i < _view.Pages.Count; i++)
                {
                    if (i > 0)
                        row.Children.Add(
                            new Container {
                                Width = 1f,
                                Height = 20f,
                                Background = theme.Separator,
                            }
                        );
                    row.Children.Add(
                        Tab(
                            theme,
                            p,
                            i,
                            i == selected,
                            tabWidth
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
            content.Children.Add(new IconGlyph(icon, 14f, fg));
        if (!page.Pinned)
            content.Children.Add(
                new Label(page.Title, 13f, fg) {
                    FontWeight = selected ? FontWeight.Medium : FontWeight.Normal,
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                }
            );

        var body = new DecoratedBox {
            Child = new Center {
                // Symmetric side padding keeps a centered title clear of the close button.
                Child = new Padding(EdgeInsets.Symmetric(page.Pinned ? 0f : 22f), content),
            },
        };
        var select = new Pressable {
            Child = body,
            SemanticsLabel = page.Title,
            SelectedState = selected,
            OnPressed = () => _view.SelectedIndex = index,
        };

        // The selected tab is the window surface itself and never reacts to hover; the rest get
        // the standard flat-button wash, faded rather than snapped.
        if (selected) body.Fill = theme.Window;
        else select.WireFill(body, theme);

        if (page.Pinned) return new SizedBox(PinnedWidth, StripHeight, select);

        // Close button — overlaid (not nested: Pressable captures its whole rect) and revealed on
        // hover or selection via a retained Opacity.
        var closeBox = new DecoratedBox {
            Radius = 8f,
            Fill = Color.Transparent,
            Child = SizedBox.Square(
                16f,
                new Center { Child = new IconGlyph(Icons.Close, 12f, p.DimLabel) }
            ),
        };
        var close = new Pressable {
            Child = closeBox,
            FocusRadius = 8f,
            SemanticsLabel = "Close tab",
            OnPressed = () => _view.Close(page),
        };
        var closeReveal = new Opacity(selected ? 1f : 0f, close);
        close.WireFill(closeBox, theme);

        // Hovering either pressable reveals the close button, so chain onto whatever handler
        // WireFill installed rather than overwriting it.
        Chain(select);
        Chain(close);

        return new SizedBox(
            tabWidth,
            StripHeight,
            new Stack {
                Children = {
                    select,
                    new Align(
                        Alignment.CenterRight,
                        new Padding(EdgeInsets.Only(right: 6f), closeReveal)
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