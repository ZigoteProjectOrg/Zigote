namespace AdwaitaGallery.Pages;

/// <summary>
///     The two entry-adjacent pickers: a colour button that opens the GNOME palette over a full HSV
///     picker, and an entry with a type-ahead completion list. Both are live here — the swatch and
///     the committed value below them follow what you do.
/// </summary>
public sealed class ColorAndCompletionPage : ComposedWidget
{
    private static readonly string[] Assets = [
        "assets/textures/brick_albedo.png",
        "assets/textures/brick_normal.png",
        "assets/textures/metal_albedo.png",
        "assets/textures/wood_albedo.png",
        "assets/models/barrel.zmesh",
        "assets/models/crate.zmesh",
        "assets/audio/footstep.ogg",
    ];

    private readonly Signal<Color> _color = new(AdwAccentColors.Bg(AdwAccent.Purple));
    private readonly Signal<string> _committed = new("(nothing committed yet)");

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);

        return new GalleryPage(
            "Colour & Completion",
            "A colour button over the GNOME palette, and an entry that completes as you type.",
            MaterialIcons.Palette
        ) {
            ClampWidth = 720f,
            Children = {
                Demo.Group(
                    "Colour Button",
                    "The swatch opens a chooser: the nine named accent hues and the neutrals on " +
                    "top, a full hue/saturation picker under them.",
                    new AdwActionRow("Material tint") {
                        Suffixes = {
                            new AdwColorButton(_color.Peek()) {
                                OnChanged = c => _color.Value = c,
                            },
                        },
                    },
                    new Watch(() => new AdwActionRow("Value") {
                            Suffixes = {
                                Demo.Value(
                                    $"#{(int)(_color.Value.R * 255):X2}" +
                                    $"{(int)(_color.Value.G * 255):X2}" +
                                    $"{(int)(_color.Value.B * 255):X2}"
                                ),
                            },
                        }
                    )
                ),
                new Watch(() => Demo.Stage(
                        new SizedBox(
                            height: 64f,
                            child: new DecoratedBox {
                                Fill = _color.Value,
                                Radius = AdwMetrics.CardRadius,
                            }
                        ),
                        Spacing.Md
                    )
                ),
                Demo.Group(
                    "Suggestion Entry",
                    "Type “albedo” or “zmesh”. The list filters as you type and never takes the " +
                    "caret, so typing is uninterrupted; Enter commits whatever is in the field, " +
                    "suggested or not.",
                    new AdwActionRow("Asset path") {
                        Suffixes = {
                            new SizedBox(
                                280f,
                                child: new AdwSuggestionEntry(
                                    "",
                                    Suggest,
                                    v => _committed.Value = v
                                ) { Placeholder = "assets/…" }
                            ),
                        },
                    },
                    new Watch(() => new AdwActionRow("Committed") {
                            Suffixes = { Demo.Value(_committed.Value) },
                        }
                    )
                ),
                Demo.Titled(
                    "Separator",
                    "GTK's rule, horizontal or vertical, with an optional inset at both ends.",
                    Demo.Stage(
                        new Column(
                            spacing: Spacing.Md,
                            mainAxisSize: MainAxisSize.Min,
                            crossAxisAlignment: CrossAxisAlignment.Stretch
                        ) {
                            Children = {
                                new Label("Above", AdwTypography.Body, theme.OnBackground),
                                new AdwSeparator(),
                                new Label("Below", AdwTypography.Body, theme.OnBackground),
                                new SizedBox(height: Spacing.Sm),
                                new SizedBox(
                                    height: AdwMetrics.ButtonHeight,
                                    child: new Row(spacing: Spacing.Md) {
                                        Children = {
                                            new AdwButton("Cut"),
                                            new AdwSeparator(true, 4f) {
                                                Length = AdwMetrics.ButtonHeight,
                                            },
                                            new AdwButton("Copy"),
                                            new AdwButton("Paste"),
                                        },
                                    }
                                ),
                            },
                        },
                        Spacing.Lg
                    )
                ),
            },
        };
    }

    /// <summary>Substring match over the demo asset list, newest-typed first.</summary>
    private static IReadOnlyList<(string Value, string Display)> Suggest(string typed)
    {
        var q = typed.Trim();
        return Assets
            .Where(a => q.Length == 0 || a.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(a => (a, System.IO.Path.GetFileName(a)))
            .ToList();
    }
}
