namespace AdwaitaGallery;

/// <summary>
///     The GNOME 47 accent picker: the nine system hues as swatches, the active one checked.
///     Bound straight to <see cref="GalleryApp.Accent" />, so picking one re-themes every open
///     window on the next frame.
/// </summary>
internal sealed class AccentPicker : StatelessWidget
{
    public static readonly (AdwAccent Accent, string Name)[] Accents = [
        (AdwAccent.Blue, "Blue"),
        (AdwAccent.Teal, "Teal"),
        (AdwAccent.Green, "Green"),
        (AdwAccent.Yellow, "Yellow"),
        (AdwAccent.Orange, "Orange"),
        (AdwAccent.Red, "Red"),
        (AdwAccent.Pink, "Pink"),
        (AdwAccent.Purple, "Purple"),
        (AdwAccent.Slate, "Slate"),
    ];

    private readonly GalleryApp _app;

    public AccentPicker(GalleryApp app)
    {
        _app = app;
    }

    /// <summary>Swatch diameter — 28 in a row, larger on a page that shows them off.</summary>
    public float Size { get; set; } = 28f;

    protected override Widget Build(BuildContext context)
    {
        return new Watch(() =>
            {
                var active = _app.Accent.Value;
                var wrap = new Wrap(spacing: Spacing.Sm, runSpacing: Spacing.Sm);
                foreach (var (accent, name) in Accents)
                    wrap.Children.Add(Swatch(accent, name, accent == active));
                return wrap;
            }
        );
    }

    private Widget Swatch(AdwAccent accent, string name, bool selected)
    {
        var color = AdwAccentColors.Bg(accent);
        var box = new DecoratedBox {
            Fill = color,
            Radius = Size / 2f,
            BorderColor = selected ? color.Darken(0.25f) : Color.Transparent,
            BorderWidth = 2f,
            Child = SizedBox.Square(
                Size,
                selected
                    ? new Center {
                        Child = new IconGlyph(MaterialIcons.Check, Size * 0.55f, Color.White),
                    }
                    : null
            ),
        };
        var pressable = new Pressable {
            Child = box,
            FocusRadius = Size / 2f,
            SemanticsLabel = name,
            SelectedState = selected,
            OnPressed = () =>
            {
                // A manual pick means the user is steering — stop following the system accent.
                _app.FollowSystem.Value = false;
                _app.Accent.Value = accent;
            },
        };
        return new Tooltip(name, pressable);
    }
}