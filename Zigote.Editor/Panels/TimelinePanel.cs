using Zigote.Core;
using Zigote.Core.Paint;
using Zigote.Editor.Scene;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Panels;

/// <summary>
///     Animation timeline / dope-sheet transport (Task 3): play/pause, stop, a scrubber bound to the
///     active clip, a time read-out, a loop toggle, and a clip selector. Drives
///     <see cref="EditorState.AnimationPlayer" />; the scrubber both reflects playback and seeks.
/// </summary>
public sealed class TimelinePanel : Widget
{
    private readonly AdwCheckButton _loop;

    private readonly AdwButton _playBtn;
    private readonly AdwSlider _scrub;
    private readonly EditorState _state;
    private readonly ThemeData _theme;
    private readonly Label _timeLabel;
    private int _builtClipCount = -1;

    private Widget _content;
    private Size _size;

    public TimelinePanel(EditorState state, ThemeData theme)
    {
        _state = state;
        _theme = theme;

        _playBtn =
            new AdwButton(label: "Play", onPressed: _state.ToggleAnimationPlay) { Compact = true };
        _timeLabel = new Label(
            text: "0.00 / 0.00",
            fontSize: theme.FontSizeCaption,
            color: theme.Hint
        );
        _scrub = new AdwSlider() {
            Min = 0f,
            Max = 1f,
            OnChanged = _state.SeekAnimation,
        };
        _loop = new AdwCheckButton(
            label: "Loop",
            value: _state.AnimationPlayer.Loop,
            onChanged: v => _state.AnimationPlayer.Loop = v
        );

        _content = Build();
    }

    private Widget Build()
    {
        _builtClipCount = _state.AnimationClips.Count;

        if (_builtClipCount == 0)
        {
            return new Padding(
                padding: EdgeInsets.All(10f),
                child: new Label(
                    text: "No animation — import an animated glTF.",
                    fontSize: _theme.FontSizeCaption,
                    color: _theme.Hint
                )
            );
        }

        var row = new Row {
            Children = {
                _playBtn,
                new SizedBox(6f),
                new AdwButton(
                    label: "Stop",
                    onPressed: () =>
                    {
                        _state.AnimationPlayer.Stop();
                        _state.SeekAnimation(0f);
                    }
                ) { Compact = true },
                new SizedBox(10f),
                new Expanded(_scrub),
                new SizedBox(10f),
                new SizedBox(width: 96f, child: _timeLabel),
                new SizedBox(8f),
                _loop,
            },
        };

        // Clip selector only when there's a choice to make.
        if (_builtClipCount > 1)
        {
            var indices = Enumerable.Range(start: 0, count: _builtClipCount).ToList();
            int selected = Math.Max(val1: 0, val2: IndexOfActiveClip());
            row.Children.Add(new SizedBox(8f));
            row.Children.Add(
                new Expanded(
                    new AdwDropDown(
                        items: [.. indices.Select(i => _state.AnimationClips[i].Name)],
                        selectedIndex: selected,
                        onSelected: _state.SetActiveClip
                    ) { Compact = true }
                )
            );
        }

        return new Padding(padding: EdgeInsets.Symmetric(horizontal: 8f, vertical: 6f), child: row);
    }

    private int IndexOfActiveClip()
    {
        for (int i = 0; i < _state.AnimationClips.Count; i++)
        {
            if (ReferenceEquals(objA: _state.AnimationClips[i], objB: _state.AnimationPlayer.Clip))
                return i;
        }

        return -1;
    }

    private void SyncFromPlayer()
    {
        // Rebuild only when the clip set changes (cheap content), but always live-sync the transport.
        if (_state.AnimationClips.Count != _builtClipCount) _content = Build();

        var p = _state.AnimationPlayer;
        float dur = p.Clip?.Duration ?? 0f;
        _scrub.Max = MathF.Max(x: dur, y: 0.0001f);
        _scrub.Value = p.Time;
        _timeLabel.Text = $"{p.Time:0.00} / {dur:0.00}";
        _playBtn.Label = p.Playing ? "Pause" : "Play";
    }

    public override Size Measure(Constraints c)
    {
        SyncFromPlayer();
        _size = _content.Measure(c);
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
        _content.Layout(origin);
    }

    public override void Paint(PaintList paint) => _content.Paint(paint);

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(px: point.X, py: point.Y)) return null;
        return _content.HitTest(point);
    }
}
