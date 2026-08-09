namespace Zigote.UI.Adwaita;

/// <summary>Appearance of an <see cref="AdwAlertDialog" /> response button.</summary>
public enum AdwResponseAppearance
{
    Default,

    /// <summary>Accent-colored label — the safe/primary choice.</summary>
    Suggested,

    /// <summary>Red label — an irreversible action.</summary>
    Destructive,
}

/// <summary>
///     AdwAlertDialog — the libadwaita alert: a 360px sheet with a centered bold heading, a dim
///     centered body, an optional extra child, and a hairline-separated response area (two
///     responses side-by-side, one or three-plus stacked — unless
///     <see cref="PreferWideLayout" /> keeps them in a row). A response press invokes
///     <see cref="OnResponse" /> with its id and closes; closing any other way (Escape, scrim)
///     emits <see cref="CloseResponse" /> when set.
/// </summary>
public sealed class AdwAlertDialog : AdwDialog
{
    private const float ResponseHeight = 44f;

    private readonly List<(string Id, string Label, AdwResponseAppearance Appearance)> _responses =
        [];

    private Pressable? _defaultButton;
    private bool _defaultFocused;
    private bool _responded;

    public AdwAlertDialog(string heading = "", string? body = null)
    {
        Heading = heading;
        Body = body;
        ContentWidth = 360f;
        Child = new Content(this);
    }

    // init, not set: the content sheet is built once, in the constructor, so a post-construction
    // assignment would never reach the screen. An alert is configured then shown.
    public string Heading { get; init; }
    public string? Body { get; init; }

    /// <summary>Optional widget between the body and the response area.</summary>
    public Widget? ExtraChild { get; init; }

    /// <summary>
    ///     Lay three or more responses side by side instead of stacking them — libadwaita's
    ///     <c>prefer-wide-layout</c>. It is a preference, not a command: short labels fit in a row,
    ///     and the stack remains the honest answer for long ones, so this is worth setting only
    ///     when the responses really are one or two words each.
    /// </summary>
    public bool PreferWideLayout { get; init; }

    /// <summary>Invoked once with the id of the chosen response (or <see cref="CloseResponse" />).</summary>
    public Action<string>? OnResponse { get; set; }

    /// <summary>Response id emitted when the dialog is closed without pressing a response.</summary>
    public string? CloseResponse { get; set; }

    /// <summary>
    ///     Id of the response Enter activates, libadwaita's <c>default-response</c>. Without it the
    ///     App auto-focuses the first focusable in the overlay, which is the first response in
    ///     <see cref="AddResponse" /> order — so on a Save/Discard/Cancel alert Enter would discard.
    ///     Set before <see cref="AdwDialog.Show()" />.
    /// </summary>
    public string? DefaultResponse { get; init; }

    /// <summary>Append a response button. Call before <see cref="AdwDialog.Show()" />.</summary>
    public void AddResponse(string id, string label,
        AdwResponseAppearance appearance = AdwResponseAppearance.Default)
    {
        _responses.Add((id, label, appearance));
    }

    public override void Close()
    {
        if (!_responded && CloseResponse is { } cr)
        {
            _responded = true;
            OnResponse?.Invoke(cr);
        }

        base.Close();
    }

    /// <summary>
    ///     Claim focus for <see cref="DefaultResponse" />. App's overlay auto-focus runs after the
    ///     layout pass and before the paint, so a Layout-time request would simply be overwritten;
    ///     the first paint is the earliest moment that outlives it. Strictly once — repeating it
    ///     every frame would drag focus back off whatever the user tabbed to.
    /// </summary>
    public override void Paint(PaintList paint)
    {
        if (!_defaultFocused && _defaultButton is { } button)
        {
            _defaultFocused = true;
            Owner?.RequestFocus(button);
        }

        base.Paint(paint);
    }

    private void Respond(string id)
    {
        // Buttons stay live during the ~150ms exit animation; only the first response counts.
        if (_responded) return;
        _responded = true;
        OnResponse?.Invoke(id);
        base.Close();
    }

    private sealed class Content(AdwAlertDialog owner) : ComposedWidget
    {
        protected override Widget Build(BuildContext context)
        {
            var theme = ThemeProvider.Of(context);
            var p = AdwPalette.For(theme);

            var head = new Column(
                spacing: Spacing.Sm,
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Label(owner.Heading, AdwTypography.Title4, theme.OnBackground) {
                        Align = TextAlign.Center,
                    },
                },
            };
            if (!string.IsNullOrEmpty(owner.Body))
                head.Children.Add(
                    new Label(owner.Body!, AdwTypography.Body, p.DimLabel) {
                        Align = TextAlign.Center,
                    }
                );
            if (owner.ExtraChild is { } extra)
                head.Children.Add(new Padding(EdgeInsets.Only(top: Spacing.Sm), extra));

            var col = new Column(
                crossAxisAlignment: CrossAxisAlignment.Stretch,
                mainAxisSize: MainAxisSize.Min
            ) {
                Children = {
                    new Padding(
                        EdgeInsets.FromLtrb(
                            Spacing.Xxl,
                            Spacing.Xxl + Spacing.Xs,
                            Spacing.Xxl,
                            Spacing.Xl
                        ),
                        head
                    ),
                },
            };

            if (owner._responses.Count == 0) return col;

            col.Children.Add(Hairline(theme, true));

            // Two responses always sit side by side; more only when the caller asked for wide.
            if (owner._responses.Count == 2 ||
                (owner.PreferWideLayout && owner._responses.Count > 2))
            {
                var row = new Row();
                for (var i = 0; i < owner._responses.Count; i++)
                {
                    if (i > 0) row.Children.Add(Hairline(theme, false));
                    row.Children.Add(new Expanded(ResponseButton(theme, p, owner._responses[i])));
                }

                col.Children.Add(row);
            }
            else
                for (var i = 0; i < owner._responses.Count; i++)
                {
                    if (i > 0) col.Children.Add(Hairline(theme, true));
                    col.Children.Add(ResponseButton(theme, p, owner._responses[i]));
                }

            return col;
        }

        private static Widget Hairline(ThemeData theme, bool horizontal)
        {
            return new Container {
                Width = horizontal ? null : (float?)1f,
                Height = horizontal ? 1f : ResponseHeight,
                Background = theme.Separator,
            };
        }

        private Widget ResponseButton(ThemeData theme, AdwColors p,
            (string Id, string Label, AdwResponseAppearance Appearance) response)
        {
            var fg = response.Appearance switch {
                AdwResponseAppearance.Suggested => theme.PrimaryDark,
                AdwResponseAppearance.Destructive => p.Destructive,
                _ => theme.OnBackground,
            };
            var box = new DecoratedBox {
                Fill = Color.Transparent,
                Child = new SizedBox(
                    height: ResponseHeight,
                    child: new Center {
                        Child = new Label(response.Label, AdwTypography.Heading, fg),
                    }
                ),
            };
            var pressable = new Pressable {
                Child = box,
                OnPressed = () => owner.Respond(response.Id),
                SemanticsLabel = response.Label,
            };
            pressable.WireFill(box, theme);
            if (response.Id == owner.DefaultResponse) owner._defaultButton = pressable;
            return pressable;
        }
    }
}