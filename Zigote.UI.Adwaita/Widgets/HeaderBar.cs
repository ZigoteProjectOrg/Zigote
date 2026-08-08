namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwWindowTitle — the standard header-bar title: a centered column with a bold title and an
///     optional dim subtitle.
/// </summary>
public sealed class AdwWindowTitle : StatelessWidget
{
    private string _title;
    private string? _subtitle;

    public AdwWindowTitle(string title = "", string? subtitle = null)
    {
        _title = title;
        _subtitle = subtitle;
    }

    public string Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => this.Set(ref _subtitle, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var col = new Column(
            mainAxisSize: MainAxisSize.Min,
            mainAxisAlignment: MainAxisAlignment.Center
        );
        col.Children.Add(
            new Label(Title, AdwTypography.Heading, theme.OnBackground) {
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
            }
        );
        if (!string.IsNullOrEmpty(Subtitle))
            col.Children.Add(
                new Label(Subtitle!, AdwTypography.Caption, AdwPalette.For(theme).DimLabel) {
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                }
            );
        return col;
    }
}

/// <summary>
///     AdwHeaderBar — the GNOME window bar: start widgets (with an optional back button), an
///     absolutely-centered title, end widgets, and a bottom hairline unless <see cref="Flat" />.
/// </summary>
public sealed class AdwHeaderBar : StatelessWidget
{
    private string? _title;
    private Widget? _titleWidget;
    private bool _showBackButton;
    private bool _flat;
    private bool _showStartWindowControls = true;
    private bool _showEndWindowControls = true;

    /// <summary>Custom center widget; when null an <see cref="AdwWindowTitle" /> of <see cref="Title" /> is used.</summary>
    public Widget? TitleWidget
    {
        get => _titleWidget;
        set => this.Set(ref _titleWidget, value);
    }

    public string? Title
    {
        get => _title;
        set => this.Set(ref _title, value);
    }

    /// <summary>Widgets packed at the start (after the back button). Populate before mounting.</summary>
    public List<Widget> Start { get; init; } = [];

    /// <summary>Widgets packed at the end.</summary>
    public List<Widget> End { get; init; } = [];

    public bool ShowBackButton
    {
        get => _showBackButton;
        set => this.Set(ref _showBackButton, value);
    }

    public Action? OnBack { get; set; }

    /// <summary>Flat bar: transparent background, no bottom hairline (toolbar-view style).</summary>
    public bool Flat
    {
        get => _flat;
        set => this.Set(ref _flat, value);
    }

    /// <summary>
    ///     Host the window-frame buttons for the given titlebar side(s), per the system's GNOME
    ///     <c>button-layout</c> (rendered only under Adwaita CSD chrome). Both default true — a
    ///     single-headerbar window shows everything; split-view layouts turn the inner sides off
    ///     (sidebar bar keeps Start, content bar keeps End).
    /// </summary>
    public bool ShowStartWindowControls
    {
        get => _showStartWindowControls;
        set => this.Set(ref _showStartWindowControls, value);
    }

    /// <inheritdoc cref="ShowStartWindowControls" />
    public bool ShowEndWindowControls
    {
        get => _showEndWindowControls;
        set => this.Set(ref _showEndWindowControls, value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        var startRow = new Row(spacing: 6f, mainAxisSize: MainAxisSize.Min);
        if (ShowStartWindowControls)
            startRow.Children.Add(new AdwWindowControls(AdwControlsSide.Start));
        if (ShowBackButton)
            startRow.Children.Add(
                new AdwButton("Back", () => OnBack?.Invoke()) {
                    IconName = Icons.ArrowBack,
                    Style = AdwButtonStyle.Flat,
                    Circular = true,
                }
            );
        foreach (var w in Start) startRow.Children.Add(w);

        var endRow = new Row(spacing: 6f, mainAxisSize: MainAxisSize.Min);
        foreach (var w in End) endRow.Children.Add(w);
        if (ShowEndWindowControls)
            endRow.Children.Add(new AdwWindowControls(AdwControlsSide.End));

        var bar = new DecoratedBox {
            Fill = Flat ? Color.Transparent : theme.TitleBar,
            Child = new Padding(EdgeInsets.Symmetric(6f)) {
                Child = new HeaderLayout(
                    startRow,
                    TitleWidget ?? new AdwWindowTitle(Title ?? ""),
                    endRow
                ),
            },
        };

        if (Flat)
            return new AdwDragArea(new SizedBox(height: AdwMetrics.HeaderBarHeight, child: bar));

        // The whole bar doubles as the window drag surface under CSD chrome (no-op otherwise).
        return new AdwDragArea(
            new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new SizedBox(height: AdwMetrics.HeaderBarHeight - 1f, child: bar),
                    new Container {
                        Height = 1f,
                        Background = p.HeaderbarShade,
                    },
                },
            }
        );
    }

    /// <summary>
    ///     start · centered title · end, laid out the GTK way: the title is centered on the BAR
    ///     while it fits between the packed sides, and is otherwise pushed clear of them and
    ///     ellipsized into what is left. Stack-centering it instead draws the title straight over
    ///     the window controls whenever the bar is narrow — a 260px sidebar headerbar always is.
    /// </summary>
    private sealed class HeaderLayout(Widget start, Widget title, Widget end) : Widget
    {
        private const float Gap = 6f;
        private Size _endSize;
        private Size _size;
        private Size _startSize;
        private Size _titleSize;

        public override Size Measure(Constraints c)
        {
            var maxW = float.IsFinite(c.MaxWidth) ? c.MaxWidth : 0f;
            var slot = new Constraints(
                0f,
                float.IsFinite(c.MaxWidth) ? c.MaxWidth : float.PositiveInfinity,
                0f,
                c.MaxHeight
            );
            _startSize = start.Measure(slot);
            _endSize = end.Measure(slot);

            var free = MathF.Max(0f, maxW - _startSize.Width - _endSize.Width - Gap * 2f);
            _titleSize = title.Measure(
                new Constraints(
                    0f,
                    float.IsFinite(c.MaxWidth) ? free : float.PositiveInfinity,
                    0f,
                    c.MaxHeight
                )
            );

            var width = float.IsFinite(c.MaxWidth)
                ? maxW
                : _startSize.Width + _titleSize.Width + _endSize.Width + Gap * 2f;
            var height = MathF.Max(
                _titleSize.Height,
                MathF.Max(_startSize.Height, _endSize.Height)
            );
            _size = c.Constrain(new Size(width, height));
            return _size;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                origin.X,
                origin.Y,
                _size.Width,
                _size.Height
            );
            start.Layout(new Offset(origin.X, Center(origin.Y, _startSize.Height)));
            end.Layout(
                new Offset(
                    origin.X + _size.Width - _endSize.Width,
                    Center(origin.Y, _endSize.Height)
                )
            );

            var left = origin.X + _startSize.Width + Gap;
            var right = origin.X + _size.Width - _endSize.Width - Gap - _titleSize.Width;
            var centered = origin.X + (_size.Width - _titleSize.Width) / 2f;
            title.Layout(
                new Offset(
                    right >= left ? Math.Clamp(centered, left, right) : left,
                    Center(origin.Y, _titleSize.Height)
                )
            );
        }

        private float Center(float top, float childHeight)
        {
            return top + (_size.Height - childHeight) / 2f;
        }

        public override void Paint(PaintList paint)
        {
            title.Paint(paint);
            start.Paint(paint);
            end.Paint(paint);
        }

        public override Widget? HitTest(Offset point)
        {
            if (!Bounds.Contains(point.X, point.Y)) return null;
            // Sides first: they overlap nothing now, but a title widget that fills its slot (a view
            // switcher) must not swallow points that belong to a button.
            return start.HitTest(point) ?? end.HitTest(point) ?? title.HitTest(point) ?? this;
        }

        public override IEnumerable<Widget> GetChildren()
        {
            return [start, title, end];
        }
    }
}