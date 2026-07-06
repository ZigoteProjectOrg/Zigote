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
    private readonly Checkbox _loop;

    private readonly Button _playBtn;
    private readonly Slider _scrub;
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

        _playBtn = new Button("Play", _state.ToggleAnimationPlay);
        _timeLabel = new Label("0.00 / 0.00", theme.FontSizeCaption, theme.Hint);
        _scrub = new Slider(0f) {
            Min = 0f,
            Max = 1f,
            OnChanged = _state.SeekAnimation,
        };
        _loop = new Checkbox(_state.AnimationPlayer.Loop, v => _state.AnimationPlayer.Loop = v);

        _content = Build();
    }

    private Widget Build()
    {
        _builtClipCount = _state.AnimationClips.Count;

        if (_builtClipCount == 0)
            return new Padding(
                EdgeInsets.All(10f),
                new Label(
                    "No animation — import an animated glTF.",
                    _theme.FontSizeCaption,
                    _theme.Hint
                )
            );

        var row = new Row {
            Children = {
                new SizedBox(64f, 28f, _playBtn),
                new SizedBox(6f),
                new SizedBox(
                    56f,
                    28f,
                    new Button(
                        "Stop",
                        () =>
                        {
                            _state.AnimationPlayer.Stop();
                            _state.SeekAnimation(0f);
                        }
                    )
                ),
                new SizedBox(10f),
                new Expanded(_scrub),
                new SizedBox(10f),
                new SizedBox(96f, child: _timeLabel),
                new SizedBox(8f),
                new Label("Loop", _theme.FontSizeCaption, _theme.OnSurface),
                _loop,
            },
        };

        // Clip selector only when there's a choice to make.
        if (_builtClipCount > 1)
        {
            var indices = Enumerable.Range(0, _builtClipCount).ToList();
            var selected = Math.Max(0, IndexOfActiveClip());
            row.Children.Add(new SizedBox(8f));
            row.Children.Add(
                new Expanded(
                    new Dropdown<int>(
                        indices,
                        selected,
                        i => _state.AnimationClips[i].Name,
                        (i, _) => _state.SetActiveClip(i)
                    )
                )
            );
        }

        return new Padding(EdgeInsets.Symmetric(8f, 6f), row);
    }

    private int IndexOfActiveClip()
    {
        for (var i = 0; i < _state.AnimationClips.Count; i++)
            if (ReferenceEquals(_state.AnimationClips[i], _state.AnimationPlayer.Clip))
                return i;
        return -1;
    }

    private void SyncFromPlayer()
    {
        // Rebuild only when the clip set changes (cheap content), but always live-sync the transport.
        if (_state.AnimationClips.Count != _builtClipCount) _content = Build();

        var p = _state.AnimationPlayer;
        var dur = p.Clip?.Duration ?? 0f;
        _scrub.Max = MathF.Max(dur, 0.0001f);
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
            origin.X,
            origin.Y,
            _size.Width,
            _size.Height
        );
        _content.Layout(origin);
    }

    public override void Paint(PaintList paint)
    {
        _content.Paint(paint);
    }

    public override Widget? HitTest(Offset point)
    {
        if (!Bounds.Contains(point.X, point.Y)) return null;
        return _content.HitTest(point);
    }
}