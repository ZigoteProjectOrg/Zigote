using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.UI.Host;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets.Overlays;

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
    private static Color ToastSurface => new(r: 0.12f, g: 0.12f, b: 0.13f);

    private Color ToastOnSurface => new(r: 0.96f, g: 0.96f, b: 0.97f);

    public void Tick(float dt) => Remaining = MathF.Max(x: 0f, y: Remaining - dt);

    public override Size Measure(Constraints c)
    {
        _theme = ThemeProvider.Of(BuildContext.Current);
        _screen = new Size(width: c.MaxWidth, height: c.MaxHeight);
        // The toast is an app-level overlay, outside the root SafeArea — without the device insets
        // it sits on top of the home indicator.
        _safe = MediaQuery.Of(BuildContext.Current).Padding;
        return _screen;
    }

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _screen.Width,
            height: _screen.Height
        );

        var (surface, _, action) = Geometry();
        _actionRect = action;
        // Surface is referenced only for hit math symmetry; nothing else to store.
        _ = surface;
    }

    public override void Paint(PaintList paint)
    {
        (var surface, float messageX, var action) = Geometry();

        // Slide-up on appear, fade-out near the end.
        float appearT = MathF.Min(x: 1f, y: (Duration - Remaining) / AppearSeconds);
        float dismissT = Remaining < DismissSeconds ? Remaining / DismissSeconds : 1f;
        float alpha = appearT * dismissT;
        float yOff = (1f - appearT) * 24f;

        var sRect = new Rect(
            x: surface.X,
            y: surface.Y + yOff,
            width: surface.Width,
            height: surface.Height
        );

        paint.PushAlpha(alpha);

        // Flat opaque near-black surface on a soft Z2 shadow, in the theme's toast shape.
        float radius = MathF.Min(x: _theme.ToastRadius, y: sRect.Height / 2f);
        paint.AddElevation(bounds: sRect, radius: radius, style: Elevation.Z2);
        paint.AddRect(bounds: sRect, color: ToastSurface, radius: radius);

        // The surface is capped at the screen width but the message is drawn at its measured
        // width — clip so a long message ends at the toast instead of running off-screen.
        paint.AddClipStart(bounds: sRect, radius: radius);

        float fs = _theme.FontSizeBody;
        float baselineY = sRect.Y + ((Height - fs) / 2f) + (fs * 0.8f);

        if (!string.IsNullOrEmpty(message))
        {
            paint.AddText(
                text: message,
                baselineX: sRect.X + messageX,
                baselineY: baselineY,
                color: ToastOnSurface,
                fontSize: fs
            );
        }

        if (!string.IsNullOrEmpty(actionLabel))
        {
            var ac = _actionHovered ? _theme.Primary : _theme.Primary.WithAlpha(0.85f);
            paint.AddText(
                text: actionLabel,
                baselineX: action.X + ActionPadH,
                baselineY: baselineY,
                color: ac,
                fontSize: fs,
                fontWeight: FontWeight.Bold
            );
        }

        paint.AddClipEnd();
        paint.PopAlpha();
    }

    public override Widget? HitTest(Offset point)
    {
        // Only intercept clicks on the action button; let everything else through.
        if (!string.IsNullOrEmpty(actionLabel) && _actionRect.Contains(px: point.X, py: point.Y))
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
        float fs = _theme.FontSizeBody;

        float msgW = string.IsNullOrEmpty(message)
            ? 0f
            : TextMeasure.Width(text: message, fontSize: fs);

        float actionW = 0f;
        if (!string.IsNullOrEmpty(actionLabel))
        {
            actionW = TextMeasure.Width(text: actionLabel, fontSize: fs, weight: FontWeight.Bold) +
                      (ActionPadH * 2f);
        }

        float innerW = msgW + (actionW > 0f ? Gap + actionW : 0f);
        float usableW = _screen.Width - _safe.Horizontal;
        float surfaceW = MathF.Min(
            x: innerW + (PadH * 2f),
            y: MathF.Max(x: 120f, y: usableW - 32f)
        );

        float surfaceX = _safe.Left + ((usableW - surfaceW) / 2f);
        float surfaceY = _screen.Height - _safe.Bottom - Height - BottomMargin;

        var surface = OverlayPositioning.Clamp(
            rect: new Rect(
                x: surfaceX,
                y: surfaceY,
                width: surfaceW,
                height: Height
            ),
            screen: _screen,
            margin: BottomMargin,
            safe: _safe
        );

        float messageX = PadH;

        var action = Rect.Zero;
        if (actionW > 0f)
        {
            action = new Rect(
                x: surface.X + surface.Width - PadH - actionW,
                y: surface.Y,
                width: actionW,
                height: Height
            );
        }

        return (surface, messageX, action);
    }
}
