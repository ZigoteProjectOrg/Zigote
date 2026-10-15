using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Editor.Scene;
using Zigote.UI.Host;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Widgets;

/// <summary>
///     The strip along the bottom of the shell, IDE-style: what the editor is doing (ready /
///     building scripts / playing / paused) and what is selected at the start; the console's
///     error + warning counts (click → Console), the command-palette hint (click → palette) and the
///     frame rate at the end. Self-painting — it reads live state on every paint and repaints on
///     the events that change it; the log counts and fps ride along with the shell's own repaints.
/// </summary>
public sealed class StatusBar : Widget
{
    private const float BarH = 24f;
    private readonly string _paletteHint;
    private readonly Action _showConsole;
    private readonly Action _showPalette;
    private readonly EditorState _state;

    private readonly ThemeData _theme;

    // Frame-rate readout, refreshed twice a second from the frames in between: a per-frame value
    // changes digit count (and colour) every paint, which reads as flicker.
    private const float FpsInterval = 0.5f;
    private int _fpsFrames;
    private float _fpsFrameTime;
    private float _fpsShown;
    private float _fpsWindowStart = -1f;

    private Rect _countsRect;
    private Rect _paletteRect;
    private Size _size;

    public StatusBar(EditorState state, ThemeData theme, string paletteHint, Action showConsole,
        Action showPalette)
    {
        _state = state;
        _theme = theme;
        _paletteHint = paletteHint;
        _showConsole = showConsole;
        _showPalette = showPalette;
        state.SelectionSignal.Changed += _ => MarkNeedsPaint();
        state.ScriptBuildStatusChanged += MarkNeedsPaint;
        state.PlayPausedChanged += MarkNeedsPaint;
        state.PlayStarted += MarkNeedsPaint;
    }

    public override Size Measure(Constraints c) =>
        _size = c.Constrain(new Size(width: c.MaxWidth, height: BarH));

    public override void Layout(Offset origin)
    {
        Bounds = new Rect(
            x: origin.X,
            y: origin.Y,
            width: _size.Width,
            height: _size.Height
        );
    }

    public override void Paint(PaintList paint)
    {
        paint.AddRect(bounds: Bounds, color: _theme.Surface);
        paint.AddRect(
            bounds: new Rect(
                x: Bounds.X,
                y: Bounds.Y,
                width: Bounds.Width,
                height: 1f
            ),
            color: _theme.Separator
        );

        float fs = _theme.FontSizeCaption;
        float baseline = TextBaseline.Centered(Bounds.Y, BarH, fs);

        // ── Start: activity + selection ────────────────────────────────────────
        int scriptErrors = _state.ScriptDiagnostics.Count;
        (string status, var tone) = _state switch {
            { IsPlaying: true, IsPaused: true } => ("Paused", _theme.Warning),
            { IsPlaying: true } => ("Playing", _theme.Success),
            { IsScriptBuilding: true } => ("Building scripts…", _theme.Warning),
            _ when scriptErrors > 0 => ($"Script build: {scriptErrors} diagnostic(s)",
                _theme.Error),
            _ => ("Ready", _theme.TextMuted),
        };
        float x = Bounds.X + 10f;
        paint.AddRect(
            bounds: new Rect(
                x: x,
                y: Bounds.Y + ((BarH - 8f) / 2f),
                width: 8f,
                height: 8f
            ),
            color: tone,
            radius: 4f
        );
        x += 14f;
        Text(
            paint: paint,
            text: status,
            x: x,
            baseline: baseline,
            color: _theme.OnSurface
        );
        // The selection starts after a slot wide enough for the usual states, so it does not jump
        // sideways each time the activity text changes (a diagnostics message may still run longer).
        x += MathF.Max(
            x: Slot("Building scripts…"),
            y: TextMeasure.Width(text: status, fontSize: fs)
        );

        int selected = _state.SelectedNodes.Count;
        string? selection = selected > 1 ? $"{selected} nodes selected" :
            _state.Selected is { } node ? node.Name : null;
        if (selection is not null)
        {
            Text(
                paint: paint,
                text: "·  " + selection,
                x: x + 10f,
                baseline: baseline,
                color: _theme.TextMuted
            );
        }

        // ── End (laid out right to left): fps, palette hint, log counts ────────
        // Every item sits in a fixed-width slot sized for its widest value, so a changing number
        // never moves its neighbours.
        float fps = SampleFps();
        float right = Bounds.Right - 10f;
        TextRight(
            paint: paint,
            text: fps > 0f ? $"{fps:F0} fps" : "idle",
            right: right,
            baseline: baseline,
            color: fps <= 0f ? _theme.TextMuted :
            fps >= 50f ? _theme.Success :
            fps >= 25f ? _theme.Warning : _theme.Error
        );
        right -= Slot("000 fps") + 14f;

        _paletteRect = TextRight(
            paint: paint,
            text: _paletteHint + "  Commands",
            right: right,
            baseline: baseline,
            color: _theme.TextMuted
        );
        right = _paletteRect.X - 14f;

        (int err, int warn, _) = EditorLog.Counts();
        float countSlot = 18f + Slot("999+");
        var warnRect = Count(
            paint: paint,
            icon: Icons.Warning,
            count: warn,
            tone: _theme.Warning,
            x: right - countSlot,
            baseline: baseline
        );
        var errRect = Count(
            paint: paint,
            icon: Icons.Error,
            count: err,
            tone: _theme.Error,
            x: warnRect.X - 8f - countSlot,
            baseline: baseline
        );
        _countsRect = new Rect(
            x: errRect.X,
            y: Bounds.Y,
            width: right - errRect.X,
            height: BarH
        );
    }

    private float Slot(string widest) =>
        TextMeasure.Width(text: widest, fontSize: _theme.FontSizeCaption);

    /// <summary>
    ///     The average frame rate over the last <see cref="FpsInterval" />, held between updates.
    ///     Averages frame times, and skips frames that follow an idle wait: the editor only paints
    ///     on demand, so a wake-up after a pause is not a slow frame. 0 until there is a sample.
    /// </summary>
    private float SampleFps()
    {
        const float idleGap = 0.09f; // longer than this is a wait for work, not rendering
        float now = App.Active?.Time ?? 0f;
        float dt = App.Active?.DeltaTime ?? 0f;
        if (_fpsWindowStart < 0f || now < _fpsWindowStart) _fpsWindowStart = now;
        if (dt is > 0f and < idleGap)
        {
            _fpsFrames++;
            _fpsFrameTime += dt;
        }

        if (now - _fpsWindowStart >= FpsInterval)
        {
            if (_fpsFrames > 0) _fpsShown = _fpsFrames / _fpsFrameTime;
            _fpsFrames = 0;
            _fpsFrameTime = 0f;
            _fpsWindowStart = now;
        }

        return _fpsShown;
    }

    private float Text(PaintList paint, string text, float x, float baseline, Color color)
    {
        float fs = _theme.FontSizeCaption;
        paint.AddText(
            text: text,
            baselineX: x,
            baselineY: baseline,
            color: color,
            fontSize: fs
        );
        return x + TextMeasure.Width(text: text, fontSize: fs);
    }

    /// <summary>Paint <paramref name="text" /> ending at <paramref name="right" />; returns its box.</summary>
    private Rect TextRight(PaintList paint, string text, float right, float baseline, Color color)
    {
        float fs = _theme.FontSizeCaption;
        float w = TextMeasure.Width(text: text, fontSize: fs);
        paint.AddText(
            text: text,
            baselineX: right - w,
            baselineY: baseline,
            color: color,
            fontSize: fs
        );
        return new Rect(
            x: right - w,
            y: Bounds.Y,
            width: w,
            height: BarH
        );
    }

    /// <summary>
    ///     An icon + count pair starting at <paramref name="x" /> — left-anchored in its slot so the
    ///     icon stays put as the number grows. Dimmed while zero; capped at "999+".
    /// </summary>
    private Rect Count(PaintList paint, string icon, int count, Color tone, float x,
        float baseline)
    {
        var box = new Rect(
            x: x,
            y: Bounds.Y,
            width: 16f,
            height: BarH
        );
        Icons.Draw(
            paint: paint,
            glyph: icon,
            box: box,
            color: count > 0 ? tone : _theme.TextMuted,
            size: 14f
        );
        float end = Text(
            paint: paint,
            text: count > 999 ? "999+" : count.ToString(),
            x: x + 18f,
            baseline: baseline,
            color: count > 0 ? _theme.OnSurface : _theme.TextMuted
        );
        return new Rect(
            x: x,
            y: Bounds.Y,
            width: end - x,
            height: BarH
        );
    }

    public override Widget? HitTest(Offset point) =>
        Bounds.Contains(px: point.X, py: point.Y) ? this : null;

    public override void OnPointerDown(Offset point)
    {
        if (_countsRect.Contains(px: point.X, py: point.Y)) _showConsole();
        else if (_paletteRect.Contains(px: point.X, py: point.Y)) _showPalette();
    }

    public override MouseCursor? GetCursor(Offset point) =>
        _countsRect.Contains(px: point.X, py: point.Y) ||
        _paletteRect.Contains(px: point.X, py: point.Y)
            ? MouseCursor.Pointer
            : null;
}
