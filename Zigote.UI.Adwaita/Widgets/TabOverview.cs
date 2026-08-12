using Zigote.Core.State;
using Zigote.UI.Host;
using Zigote.UI.Widgets.Focus;

namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwTabOverview — the grid of every open tab, shown over the content: pick one to switch to
///     it, close one from its ✕, or start a new one. This is what <see cref="AdwTabButton" /> opens,
///     and the reason a tab bar can stay legible past the point where a strip of tabs cannot.
///     <para>
///         Mount it as an overlay (<c>App.PushOverlay</c>) via <see cref="Show" />; it dismisses on
///         Escape, on picking a tab, or on <see cref="Close" />.
///     </para>
/// </summary>
public sealed class AdwTabOverview : ComposedWidget, IDismissableOverlay
{
    private const float CardW = 200f;
    private const float CardH = 140f;

    private readonly App _app;
    private readonly Trigger _dirty = new();
    private readonly AdwTabView _view;

    public AdwTabOverview(AdwTabView view, App? app = null)
    {
        _view = view;
        _app = app ?? App.Active ??
            throw new InvalidOperationException("No active App found.");
    }

    /// <summary>Invoked by the "New Tab" button. Null hides the button.</summary>
    public Action? OnCreateTab { get; set; }

    /// <summary>Heading over the grid.</summary>
    public string Title { get; init; } = "Tabs";

    public bool IsOpen { get; private set; }

    public bool RequestDismiss()
    {
        Close();
        return true;
    }

    public void Show()
    {
        if (IsOpen) return;
        IsOpen = true;
        _app.PushOverlay(this);
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _app.PopOverlay(this);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return new Watch(() =>
            {
                _dirty.Depend();
                var grid = new AdwWrapBox { ChildSpacing = Spacing.Md, LineSpacing = Spacing.Md };
                for (var i = 0; i < _view.Pages.Count; i++)
                    grid.Children.Add(Card(theme, _view.Pages[i], i));

                var header = new AdwHeaderBar {
                    Title = Title,
                    Flat = true,
                    ShowStartWindowControls = false,
                    ShowEndWindowControls = false,
                    End = {
                        new AdwButton("Close Overview", Close) {
                            IconName = Icons.Close,
                            Style = AdwButtonStyle.Flat,
                            Circular = true,
                        },
                    },
                };
                if (OnCreateTab is { } create)
                    header.Start.Add(
                        new AdwButton("New Tab", () =>
                            {
                                create();
                                _dirty.Fire();
                            }
                        ) { IconName = Icons.Add, Style = AdwButtonStyle.Flat, Circular = true }
                    );

                Widget body = _view.Pages.Count == 0
                    ? new AdwStatusPage {
                        IconName = MaterialIcons.Tab,
                        Title = "No Open Tabs",
                        Compact = true,
                    }
                    : new ScrollView(new Padding(EdgeInsets.All(Spacing.Xl), grid));

                // Opaque sheet on --overview-bg-color: the overview replaces the content rather
                // than floating over a scrim, and it gets its OWN surface (a shade off the window)
                // so the thumbnails sitting on it read as cards.
                return new ColoredBox(
                    AdwPalette.For(theme).OverviewBg,
                    new AdwToolbarView(body) { TopBars = { header } }
                );
            }
        );
    }

    private Widget Card(ThemeData theme, AdwTabPage page, int index)
    {
        var p = AdwPalette.For(theme);
        var selected = _view.SelectedIndex == index;

        var titleRow = new Row(spacing: Spacing.Xs) {
            Children = {
                new Expanded(
                    new Label(page.Title, AdwTypography.Caption, theme.OnBackground) {
                        MaxLines = 1,
                        Overflow = TextOverflow.Ellipsis,
                    }
                ),
                new AdwButton("Close Tab", () =>
                    {
                        _view.Close(page);
                        _dirty.Fire();
                    }
                ) { IconName = Icons.Close, Style = AdwButtonStyle.Flat, Circular = true },
            },
        };
        if (page.IconName is { } icon)
            titleRow.Children.Insert(0, new IconGlyph(icon, AdwMetrics.IconSize, p.DimLabel));

        // `tabthumbnail { border-radius: $card_radius + 4px }` — a thumbnail is rounder than the
        // card inside it, on the thumbnail surface rather than the card one.
        var card = new DecoratedBox {
            Radius = AdwMetrics.CardRadius + 4f,
            Fill = p.ThumbnailBg,
            // The selected tab is ringed in the accent, the way the overview marks "you are here".
            BorderColor = selected ? theme.Accent : theme.Border,
            BorderWidth = selected ? 2f : 1f,
            Child = new Column(crossAxisAlignment: CrossAxisAlignment.Stretch) {
                Children = {
                    new Expanded(
                        // ponytail: a flat placeholder, not a live thumbnail — capturing page
                        // content needs a render-to-texture pass per tab. Swap in a captured
                        // texture here when one is available; nothing else about this changes.
                        new ColoredBox(p.ViewBg)
                    ),
                    new Padding(EdgeInsets.Symmetric(Spacing.Sm, Spacing.Xs), titleRow),
                },
            },
        };

        return new SizedBox(
            CardW,
            CardH,
            new Pressable {
                Child = card,
                FocusRadius = AdwMetrics.CardRadius + 4f,
                SemanticsLabel = page.Title,
                SelectedState = selected,
                OnPressed = () =>
                {
                    _view.SelectedIndex = index;
                    Close();
                },
            }
        );
    }
}
