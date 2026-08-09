using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Overlays;
using Zigote.UI.Host;

namespace Zigote.UI.Widgets.Controls;

/// <summary>
///     Flat toast shown near the bottom-centre of the window. An opaque near-black surface — the
///     OSD colour both Material and Adwaita keep dark in either appearance — floats on a soft Z2
///     shadow, shaped by <see cref="ThemeData.ToastRadius" /> (a rounded rectangle under Material, a
///     capsule under Adwaita) and carrying an optional accent action label. Created by
///     <c>App.ShowSnackbar</c>; auto-dismissed after <see cref="Duration" /> seconds.
/// </summary>
/// <remarks>
///     The action button's hit-rect is derived from the measured label width (via
///     <see cref="TextMeasure" />), so clicks land exactly on the painted text — no estimate.
/// </remarks>
public sealed class Snackbar(
    App app,
    string message,
    float duration = 3f,
    string? actionLabel = null,
    Action? onAction = null)
    : Widget
{
    private const float Height = 44f;
    private const float PadH = 16f; // surface inner horizontal padding
    private const float Gap = 16f; // gap between message and action
    private const float ActionPadH = 10f; // hit-padding around the action label
    private const float BottomMargin = 16f; // distance from the bottom screen edge
    private const float AppearSeconds = 0.18f;
    private const float DismissSeconds = 0.3f;

    private readonly App _app = app;
    private bool _actionHovered;

    // Action button hit-rect, recomputed in Layout to match the painted label exactly.
    private Rect _actionRect;
    private EdgeInsets _safe;
    private Size _screen;
    private ThemeData _theme = ThemeData.Dark;

    public float Duration { get; } = duration;
    public float Remaining { get; private set; } = duration;
    public bool IsDone => Remaining <= 0f;

    /// <summary>Near-black flat toast fill, distinct from any surface tint so it reads on both themes.</summary>
    private static Color ToastSurface => new(0.12f, 0.12f, 0.13f);

    private Color ToastOnSurface => new(0.96f, 0.96f, 0.97f);

    public void Tick(float dt)
    {
        Remaining = MathF.Max(0f, Remaining - dt);
    }

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _screen = new Size(c.MaxWidth, c.MaxHeight);
        // The toast is an app-level overlay, outside the root SafeArea — without the device insets
        // it sits on top of the home indicator.
        _safe = MediaQuery.Of(BuildContext.Current).Padding;
        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            origin.X,
            origin.Y,
            _screen.Width,
            _screen.Height
        );

        var (surface, _, action) = Geometry();
        _actionRect = action;
        // Surface is referenced only for hit math symmetry; nothing else to store.
        _ = surface;
    }

    public override void Paint(PaintList paint)
    {
        var (surface, messageX, action) = Geometry();

        // Slide-up on appear, fade-out near the end.
        var appearT = MathF.Min(1f, (Duration - Remaining) / AppearSeconds);
        var dismissT = Remaining < DismissSeconds ? Remaining / DismissSeconds : 1f;
        var alpha = appearT * dismissT;
        var yOff = (1f - appearT) * 24f;

        var sRect = new Rect(
            surface.X,
            surface.Y + yOff,
            surface.Width,
            surface.Height
        );

        paint.PushAlpha(alpha);

        // Flat opaque near-black surface on a soft Z2 shadow, in the theme's toast shape.
        var radius = MathF.Min(_theme.ToastRadius, sRect.Height / 2f);
        paint.AddElevation(sRect, radius, Elevation.Z2);
        paint.AddRect(sRect, ToastSurface, radius);

        // The surface is capped at the screen width but the message is drawn at its measured
        // width — clip so a long message ends at the toast instead of running off-screen.
        paint.AddClipStart(sRect, radius);

        var fs = _theme.FontSizeBody;
        var baselineY = sRect.Y + (Height - fs) / 2f + fs * 0.8f;

        if (!string.IsNullOrEmpty(message))
            paint.AddText(
                message,
                sRect.X + messageX,
                baselineY,
                ToastOnSurface,
                fs
            );

        if (!string.IsNullOrEmpty(actionLabel))
        {
            var ac = _actionHovered ? _theme.Primary : _theme.Primary.WithAlpha(0.85f);
            paint.AddText(
                actionLabel,
                action.X + ActionPadH,
                baselineY,
                ac,
                fs,
                fontWeight: FontWeight.Bold
            );
        }

        paint.AddClipEnd();
        paint.PopAlpha();
    }

    public override Widget? HitTest(Offset point)
    {
        // Only intercept clicks on the action button; let everything else through.
        if (!string.IsNullOrEmpty(actionLabel) && _actionRect.Contains(point.X, point.Y))
            return this;
        return null;
    }

    public override void OnPointerDown(Offset _)
    {
        onAction?.Invoke();
        Remaining = 0f; // dismiss immediately on action
    }

    public override void OnPointerEnter()
    {
        _actionHovered = true;
        MarkNeedsPaint();
    }

    public override void OnPointerExit()
    {
        _actionHovered = false;
        MarkNeedsPaint();
    }

    /// <summary>
    ///     Computes the toast geometry from measured text so Layout's hit-rect and Paint's drawing use
    ///     identical maths. Returns the surface rect (at rest, before slide offset), the message's X
    ///     offset within the surface, and the action button's screen-space hit-rect.
    /// </summary>
    private (Rect Surface, float MessageX, Rect Action) Geometry()
    {
        var fs = _theme.FontSizeBody;

        var msgW = string.IsNullOrEmpty(message) ? 0f : TextMeasure.Width(message, fs);

        var actionW = 0f;
        if (!string.IsNullOrEmpty(actionLabel))
            actionW = TextMeasure.Width(actionLabel, fs, FontWeight.Bold) + ActionPadH * 2f;

        var innerW = msgW + (actionW > 0f ? Gap + actionW : 0f);
        var usableW = _screen.Width - _safe.Horizontal;
        var surfaceW = MathF.Min(innerW + PadH * 2f, MathF.Max(120f, usableW - 32f));

        var surfaceX = _safe.Left + (usableW - surfaceW) / 2f;
        var surfaceY = _screen.Height - _safe.Bottom - Height - BottomMargin;

        var surface = OverlayPositioning.Clamp(
            new Rect(
                surfaceX,
                surfaceY,
                surfaceW,
                Height
            ),
            _screen,
            BottomMargin,
            _safe
        );

        var messageX = PadH;

        var action = Rect.Zero;
        if (actionW > 0f)
            action = new Rect(
                surface.X + surface.Width - PadH - actionW,
                surface.Y,
                actionW,
                Height
            );

        return (surface, messageX, action);
    }
}