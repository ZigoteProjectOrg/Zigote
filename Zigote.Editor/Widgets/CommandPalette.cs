using Zigote.Core;
using Zigote.Core.Events;
using Zigote.Core.Paint;
using Zigote.UI.TextShaping;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;

namespace Zigote.Editor.Widgets;

/// <summary>One palette row: what it is, where it comes from, and what choosing it does.</summary>
/// <param name="Title">The searchable name ("Save", "Cube", "player.cs").</param>
/// <param name="Group">The dim prefix column ("File", "Add", "Node", "Asset").</param>
/// <param name="Run">Invoked after the palette has closed.</param>
/// <param name="Accel">Display-form shortcut shown at the row's trailing edge, if any.</param>
public readonly record struct PaletteCommand(
    string Title,
    string Group,
    Action Run,
    string? Accel = null);

/// <summary>
///     The editor's command palette (⌘⇧P): one search box over every menu action, panel, node kind,
///     scene node and project file. Keyboard-first — type to filter, ↑/↓ to move, Enter to run,
///     Esc to dismiss — and shown as an <see cref="AdwDialog" /> so it inherits the modal barrier.
/// </summary>
public sealed class CommandPalette : Widget
{
    private const float Width = 580f;
    private const float Height = 420f;
    private const float Pad = 10f;
    private const float RowH = AdwMetrics.MenuRowHeight;
    private const float RowsTop = Pad + AdwMetrics.EntryHeight + Pad;
    private const float GroupW = 64f;

    // SDL scancodes.
    private const uint ScReturn = 40, ScKpEnter = 88, ScPageUp = 75, ScPageDown = 78, ScDown = 81,
        ScUp = 82;

    private readonly IReadOnlyList<PaletteCommand> _all;
    private readonly AdwSearchEntry _entry;
    private readonly List<PaletteCommand> _hits = [];
    private readonly ThemeData _theme;
    private AdwDialog? _dialog;
    private int _first;
    private int _selected;
    private Size _size;

    private CommandPalette(IReadOnlyList<PaletteCommand> commands, ThemeData theme)
    {
        _all = commands;
        _theme = theme;
        _entry = new AdwSearchEntry {
            Placeholder = "Search actions, nodes and files",
            OnChanged = _ => Refresh(),
            OnSubmitted = _ => Choose(_selected),
            OnKeyDown = (scancode, _) =>
            {
                switch (scancode)
                {
                    case ScReturn or ScKpEnter:
                        Choose(_selected);
                        return true;
                    case ScDown:
                        Move(1);
                        return true;
                    case ScUp:
                        Move(-1);
                        return true;
                    case ScPageDown:
                        Move(Math.Max(val1: 1, val2: VisibleRows));
                        return true;
                    case ScPageUp:
                        Move(-Math.Max(val1: 1, val2: VisibleRows));
                        return true;
                    default: return false;
                }
            },
        };
        Refresh();
    }

    private int VisibleRows => Math.Max(val1: 0, val2: (int)((_size.Height - RowsTop - Pad) / RowH));

    private Rect RowsBounds => new(
        x: Bounds.X + Pad,
        y: Bounds.Y + RowsTop,
        width: MathF.Max(x: 0f, y: Bounds.Width - (Pad * 2f)),
        height: VisibleRows * RowH
    );

    /// <summary>Open the palette over the active app with the search box focused.</summary>
    public static void Show(IReadOnlyList<PaletteCommand> commands, ThemeData theme)
    {
        var palette = new CommandPalette(commands: commands, theme: theme);
        palette._dialog = AdwDialog.Show(child: palette, width: Width, height: Height);
        palette._entry.Focus();
    }

    /// <summary>
    ///     Rank <paramref name="commands" /> against <paramref name="query" />, fuzzily: each
    ///     whitespace-separated term must appear as a subsequence of the title or the group
    ///     ("svsc" finds "Save Scene As"). Best score first; ties keep their original order.
    /// </summary>
    public static List<PaletteCommand> Filter(IReadOnlyList<PaletteCommand> commands, string query)
    {
        string[] terms = query.Split(
            separator: ' ',
            options: StringSplitOptions.RemoveEmptyEntries
        );
        if (terms.Length == 0) return [..commands];

        List<(PaletteCommand Command, int Score)> scored = [];
        foreach (var command in commands)
        {
            int total = 0;
            foreach (string term in terms)
            {
                // A hit in the title outweighs the same hit in the group column.
                int best = Math.Max(
                    val1: Score(text: command.Title, term: term) * 2,
                    val2: Score(text: command.Group, term: term)
                );
                if (best < 0)
                {
                    total = -1;
                    break;
                }

                total += best;
            }

            if (total >= 0) scored.Add((command, total));
        }

        return scored.OrderByDescending(s => s.Score).Select(s => s.Command).ToList();
    }

    /// <summary>
    ///     How well <paramref name="term" /> matches <paramref name="text" /> as a case-insensitive
    ///     subsequence, or -1 when it does not. Runs of adjacent letters and letters that start a
    ///     word (after a separator, or a camelCase hump) score higher, the very first letter most.
    /// </summary>
    public static int Score(string text, string term)
    {
        // ponytail: greedy leftmost match, so a later, tighter alignment can be under-scored
        // ("ab" in "a_b ab"); switch to a DP alignment if ranking ever looks wrong.
        int matched = 0, score = 0, run = 0;
        for (int i = 0; i < text.Length && matched < term.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(term[matched]))
            {
                run = 0;
                continue;
            }

            bool wordStart = i == 0 || !char.IsLetterOrDigit(text[i - 1]) ||
                             (char.IsUpper(text[i]) && char.IsLower(text[i - 1]));
            score += 1 + (run * 2) + (wordStart ? 4 : 0) + (i == 0 ? 4 : 0);
            run++;
            matched++;
        }

        return matched == term.Length ? score : -1;
    }

    private void Refresh()
    {
        _hits.Clear();
        _hits.AddRange(Filter(commands: _all, query: _entry.Text));
        _selected = _first = 0;
        MarkNeedsPaint();
    }

    private void Choose(int index)
    {
        if (index < 0 || index >= _hits.Count) return;
        var run = _hits[index].Run;
        _dialog?.Close();
        run();
    }

    private void Move(int delta)
    {
        _selected = Math.Clamp(
            value: _selected + delta,
            min: 0,
            max: Math.Max(val1: 0, val2: _hits.Count - 1)
        );
        if (_selected < _first) _first = _selected;
        if (VisibleRows > 0 && _selected >= _first + VisibleRows)
            _first = _selected - VisibleRows + 1;
        MarkNeedsPaint();
    }

    public override Size Measure(Constraints c)
    {
        _size = c.Constrain(new Size(width: Width, height: Height));
        _entry.Measure(
            Constraints.Tight(
                width: MathF.Max(x: 0f, y: _size.Width - (Pad * 2f)),
                height: AdwMetrics.EntryHeight
            )
        );
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
        _entry.Layout(new Offset(x: origin.X + Pad, y: origin.Y + Pad));
    }

    public override void Paint(PaintList paint)
    {
        _entry.Paint(paint);

        var rows = RowsBounds;
        float fs = _theme.FontSizeBody;
        float small = _theme.FontSizeCaption;
        if (_hits.Count == 0)
        {
            paint.AddText(
                text: "No matching commands",
                baselineX: rows.X + 8f,
                baselineY: rows.Y + 22f,
                color: _theme.TextMuted,
                fontSize: fs
            );
            return;
        }

        paint.AddClipStart(rows);
        int end = Math.Min(val1: _hits.Count, val2: _first + VisibleRows);
        for (int i = _first; i < end; i++)
        {
            var hit = _hits[i];
            float y = rows.Y + ((i - _first) * RowH);
            float baseline = y + ((RowH - fs) / 2f) + (fs * 0.8f);
            if (i == _selected)
            {
                paint.AddRect(
                    bounds: new Rect(
                        x: rows.X,
                        y: y,
                        width: rows.Width,
                        height: RowH
                    ),
                    color: _theme.Fill3,
                    radius: AdwMetrics.ControlRadius
                );
            }

            paint.AddText(
                text: hit.Group,
                baselineX: rows.X + 10f,
                baselineY: baseline,
                color: _theme.TextMuted,
                fontSize: small
            );
            paint.AddText(
                text: hit.Title,
                baselineX: rows.X + 10f + GroupW,
                baselineY: baseline,
                color: _theme.OnSurface,
                fontSize: fs
            );
            if (hit.Accel is { Length: > 0 } accel)
            {
                float w = TextMeasure.Width(text: accel, fontSize: small);
                paint.AddText(
                    text: accel,
                    baselineX: rows.Right - 10f - w,
                    baselineY: baseline,
                    color: _theme.TextMuted,
                    fontSize: small
                );
            }
        }

        paint.AddClipEnd();
    }

    private int HitRow(Offset point)
    {
        var rows = RowsBounds;
        if (!rows.Contains(px: point.X, py: point.Y)) return -1;
        int index = _first + (int)((point.Y - rows.Y) / RowH);
        return index < _hits.Count ? index : -1;
    }

    public override void OnPointerDown(Offset point) => Choose(HitRow(point));

    public override void OnPointerMove(Offset point)
    {
        int index = HitRow(point);
        if (index < 0 || index == _selected) return;
        _selected = index;
        MarkNeedsPaint();
    }

    public override void OnScroll(float dx, float dy)
    {
        if (VisibleRows == 0) return;
        _first = Math.Clamp(
            value: _first - ((int)MathF.Sign(dy) * 2),
            min: 0,
            max: Math.Max(val1: 0, val2: _hits.Count - VisibleRows)
        );
        MarkNeedsPaint();
    }

    public override Widget? HitTest(Offset point) =>
        !Bounds.Contains(px: point.X, py: point.Y) ? null : _entry.HitTest(point) ?? this;

    public override IEnumerable<Widget> GetChildren() => [_entry];
}
