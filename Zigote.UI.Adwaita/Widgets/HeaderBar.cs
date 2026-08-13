namespace Zigote.UI.Adwaita;

/// <summary>
///     AdwWindowTitle — the standard header-bar title: a centered column with a bold title and an
///     optional dim subtitle.
/// </summary>
public sealed class AdwWindowTitle : ComposedWidget
{
    private string? _subtitle;
    private string _title;

    public AdwWindowTitle(string title = "", string? subtitle = null)
    {
        _title = title;
        _subtitle = subtitle;
    }

    public string Title
    {
        get => _title;
        set => this.Set(field: ref _title, value: value);
    }

    public string? Subtitle
    {
        get => _subtitle;
        set => this.Set(field: ref _subtitle, value: value);
    }

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        // `headerbar .title, windowtitle .title { padding-left: 12px; padding-right: 12px }` — the
        // title keeps its own breathing room from whatever is packed beside it.
        var col = new Column(
            mainAxisSize: MainAxisSize.Min,
            mainAxisAlignment: MainAxisAlignment.Center
        );
        col.Children.Add(
            new Label(text: Title, style: AdwTypography.Heading, color: theme.OnBackground) {
                MaxLines = 1,
                Overflow = TextOverflow.Ellipsis,
            }
        );
        if (!string.IsNullOrEmpty(Subtitle))
        {
            col.Children.Add(
                new Label(
                    text: Subtitle!,
                    style: AdwTypography.Caption,
                    color: AdwPalette.For(theme).DimLabel
                ) {
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                }
            );
        }

        return new Padding(padding: EdgeInsets.Symmetric(AdwMetrics.RowPaddingX), child: col);
    }
}

/// <summary>
///     AdwHeaderBar — the GNOME window bar: start widgets (with an optional back button), an
///     absolutely-centered title, end widgets, and a bottom hairline unless <see cref="Flat" />.
/// </summary>
public sealed class AdwHeaderBar : ComposedWidget
{
    private bool _flat;

    private HeaderLayout? _layout;
    private bool _showBackButton;
    private bool _showEndWindowControls = true;
    private bool _showStartWindowControls = true;
    private string? _title;
    private Widget? _titleWidget;

    /// <summary>
    ///     Custom center widget; when null an <see cref="AdwWindowTitle" /> of <see cref="Title" />
    ///     is used.
    /// </summary>
    public Widget? TitleWidget
    {
        get => _titleWidget;
        set => this.Set(field: ref _titleWidget, value: value);
    }

    public string? Title
    {
        get => _title;
        set => this.Set(field: ref _title, value: value);
    }

    /// <summary>Widgets packed at the start (after the back button). Populate before mounting.</summary>
    public List<Widget> Start { get; init; } = [];

    /// <summary>Widgets packed at the end.</summary>
    public List<Widget> End { get; init; } = [];

    public bool ShowBackButton
    {
        get => _showBackButton;
        set => this.Set(field: ref _showBackButton, value: value);
    }

    public Action? OnBack { get; set; }

    /// <summary>Flat bar: transparent background, no bottom hairline (toolbar-view style).</summary>
    public bool Flat
    {
        get => _flat;
        set => this.Set(field: ref _flat, value: value);
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
        set => this.Set(field: ref _showStartWindowControls, value: value);
    }

    /// <inheritdoc cref="ShowStartWindowControls" />
    public bool ShowEndWindowControls
    {
        get => _showEndWindowControls;
        set => this.Set(field: ref _showEndWindowControls, value: value);
    }

    /// <summary>
    ///     Whether the last measure found more packed into the bar than it can hold. The overflow is
    ///     clipped rather than drawn over its neighbours, so this is the difference between "tight"
    ///     and "something is not visible" — worth asserting on at a phone width, where a bar that
    ///     fits on a desktop stops fitting.
    /// </summary>
    public bool Overflowing => _layout?.Overflowing ?? false;

    protected override Widget Build(BuildContext context)
    {
        var theme = ThemeProvider.Of(context);
        var p = AdwPalette.For(theme);

        var startRow = new Row(
            spacing: AdwMetrics.HeaderBarPadding,
            mainAxisSize: MainAxisSize.Min
        );
        if (ShowStartWindowControls)
            startRow.Children.Add(new AdwWindowControls(AdwControlsSide.Start));
        if (ShowBackButton)
        {
            startRow.Children.Add(
                new AdwButton(label: "Back", onPressed: () => OnBack?.Invoke()) {
                    IconName = Icons.ArrowBack,
                    Style = AdwButtonStyle.Flat,
                    Circular = true,
                }
            );
        }

        foreach (var w in Start) startRow.Children.Add(w);

        var endRow = new Row(
            spacing: AdwMetrics.HeaderBarPadding,
            mainAxisSize: MainAxisSize.Min
        );
        foreach (var w in End) endRow.Children.Add(w);
        if (ShowEndWindowControls)
            endRow.Children.Add(new AdwWindowControls(AdwControlsSide.End));

        _layout = new HeaderLayout(
            start: startRow,
            title: TitleWidget ?? new AdwWindowTitle(Title ?? ""),
            end: endRow
        );

        // A headerbar that IS the window's titlebar loses a pixel to the window's own outline:
        // 46px with 6px of bottom padding, against 47/7 for a bar packed inside the content.
        bool titlebar = AdwWindowControls.IsWindowChrome(this);
        float height = titlebar ? AdwMetrics.TitleBarHeight : AdwMetrics.HeaderBarHeight;

        var bar = new DecoratedBox {
            Fill = Flat ? Color.Transparent : theme.TitleBar,
            // `> windowhandle > box { padding: 6px 7px 7px 7px }`.
            Child = new Padding(
                EdgeInsets.FromLtrb(
                    left: AdwMetrics.HeaderBarPaddingX,
                    top: AdwMetrics.HeaderBarPadding,
                    right: AdwMetrics.HeaderBarPaddingX,
                    bottom: titlebar
                        ? AdwMetrics.HeaderBarPadding
                        : AdwMetrics.HeaderBarPadding + 1f
                )
            ) {
                Child = _layout,
            },
        };

        if (Flat)
            return new AdwDragArea(new SizedBox(height: height, child: bar));

        // The whole bar doubles as the window drag surface under CSD chrome (no-op otherwise).
        return new AdwDragArea(
            new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new SizedBox(height: height - 1f, child: bar),
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

        /// <summary>
        ///     More packed in than the bar can hold. Clipping is gated on it: a bar with room to
        ///     spare must not have its focus rings and shadows shaved at the edges, and a too-full one
        ///     must lose its tail rather than stack controls on top of each other.
        /// </summary>
        public bool Overflowing =>
            _startSize.Width + _titleSize.Width + _endSize.Width + (Gap * 2f) > _size.Width + 0.5f;

        public override Size Measure(Constraints c)
        {
            bool bounded = float.IsFinite(c.MaxWidth);
            float maxW = bounded ? c.MaxWidth : 0f;
            var slot = new Constraints(
                minWidth: 0f,
                maxWidth: bounded ? c.MaxWidth : float.PositiveInfinity,
                minHeight: 0f,
                maxHeight: c.MaxHeight
            );
            _startSize = start.Measure(slot);
            // Only what the start side left over: measuring both against the whole bar let a packed
            // pair each claim the full width, and Layout then put one flush left and the other flush
            // right — drawn on top of each other. A side that can shrink (anything with an
            // ellipsizing label) now does; one that cannot overflows into the clip below instead.
            _endSize = end.Measure(
                bounded
                    ? new Constraints(
                        minWidth: 0f,
                        maxWidth: MathF.Max(x: 0f, y: maxW - _startSize.Width),
                        minHeight: 0f,
                        maxHeight: c.MaxHeight
                    )
                    : slot
            );

            float free = MathF.Max(x: 0f, y: maxW - _startSize.Width - _endSize.Width - (Gap * 2f));
            _titleSize = title.Measure(
                new Constraints(
                    minWidth: 0f,
                    maxWidth: float.IsFinite(c.MaxWidth) ? free : float.PositiveInfinity,
                    minHeight: 0f,
                    maxHeight: c.MaxHeight
                )
            );

            float width = float.IsFinite(c.MaxWidth)
                ? maxW
                : _startSize.Width + _titleSize.Width + _endSize.Width + (Gap * 2f);
            float height = MathF.Max(
                x: _titleSize.Height,
                y: MathF.Max(x: _startSize.Height, y: _endSize.Height)
            );
            _size = c.Constrain(new Size(width: width, height: height));
            return _size;
        }

        public override void Layout(Offset origin)
        {
            Bounds = new Rect(
                x: origin.X,
                y: origin.Y,
                width: _size.Width,
                height: _size.Height
            );
            start.Layout(
                new Offset(x: origin.X, y: Center(top: origin.Y, childHeight: _startSize.Height))
            );
            // Right-aligned, but never back past where the start side ends: with more packed in than
            // the bar can hold, the end side runs off the right edge (and is clipped) rather than
            // sliding left over its neighbour.
            end.Layout(
                new Offset(
                    x: MathF.Max(
                        x: origin.X + _startSize.Width + Gap,
                        y: origin.X + _size.Width - _endSize.Width
                    ),
                    y: Center(top: origin.Y, childHeight: _endSize.Height)
                )
            );

            float left = origin.X + _startSize.Width + Gap;
            float right = origin.X + _size.Width - _endSize.Width - Gap - _titleSize.Width;
            float centered = origin.X + ((_size.Width - _titleSize.Width) / 2f);
            title.Layout(
                new Offset(
                    x: right >= left ? Math.Clamp(value: centered, min: left, max: right) : left,
                    y: Center(top: origin.Y, childHeight: _titleSize.Height)
                )
            );
        }

        private float Center(float top, float childHeight) =>
            top + ((_size.Height - childHeight) / 2f);

        public override void Paint(PaintList paint)
        {
            bool overflowing = Overflowing;
            if (overflowing) paint.AddClipStart(Bounds);
            title.Paint(paint);
            start.Paint(paint);
            end.Paint(paint);
            if (overflowing) paint.AddClipEnd();
        }

        public override Widget? HitTest(Offset point)
        {
            if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
            // Sides first: they overlap nothing now, but a title widget that fills its slot (a view
            // switcher) must not swallow points that belong to a button.
            return start.HitTest(point) ?? end.HitTest(point) ?? title.HitTest(point) ?? this;
        }

        public override IEnumerable<Widget> GetChildren() => [start, title, end];
    }
}
