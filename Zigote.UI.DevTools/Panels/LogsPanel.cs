using Zigote.Core;
using Zigote.Core.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;
using Zigote.UI.Host;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     Severity-filtered, colour-coded tail over the <see cref="DebugLog" /> ring. Filter chips toggle
///     each level; the list rebuilds only when the log version or a filter changes. A Clear button
///     empties the ring.
/// </summary>
public sealed class LogsPanel : IDevPanel
{
    private const int MaxRows = 300;

    private readonly List<DebugLogEntry> _all = [];
    private readonly HashSet<DebugLogLevel> _hidden = [];

    private readonly Column _list = new(
        crossAxisAlignment: CrossAxisAlignment.Stretch,
        mainAxisSize: MainAxisSize.Min
    );

    private bool _dirty = true;
    private int _version = -1;

    public string Title => "Logs";
    public DevCategory Category => DevCategory.Generic;

    public Widget Build(BuildContext context)
    {
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                new Row(crossAxisAlignment: CrossAxisAlignment.Center) {
                    Children = {
                        Chip("Err", DebugLogLevel.Error),
                        Chip("Warn", DebugLogLevel.Warning),
                        Chip("Info", DebugLogLevel.Info),
                        Chip("Dbg", DebugLogLevel.Debug),
                        new Spacer(),
                        new Button(
                            "Clear",
                            () =>
                            {
                                DebugLog.Clear();
                                _dirty = true;
                            }
                        ) {
                            Style = ButtonStyle.Flat,
                            FontSize = DevKit.CaptionSize,
                        },
                    },
                },
                new SizedBox(height: Spacing.Xs),
                _list,
            },
        };
    }

    public void Refresh(float dt)
    {
        if (!_dirty && DebugLog.Version == _version) return;
        _dirty = false;
        _version = DebugLog.Version;

        DebugLog.CopyInto(_all);
        var t = App.Active?.Theme ?? ThemeData.Dark;
        var rows = new List<Widget>();
        var from = 0;
        // Count visible from the end so the newest MaxRows survive the cap.
        var visible = 0;
        for (var i = _all.Count - 1; i >= 0 && visible < MaxRows; i--)
            if (!_hidden.Contains(_all[i].Level))
            {
                visible++;
                from = i;
            }

        for (var i = from; i < _all.Count; i++)
        {
            var e = _all[i];
            if (_hidden.Contains(e.Level)) continue;
            rows.Add(
                new Label(e.Message, DevKit.CaptionSize, LevelColor(e.Level, t)) {
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                    FontFamily = "code",
                }
            );
        }

        if (rows.Count == 0) rows.Add(new DevNote("No log entries."));
        _list.SetChildren(rows);
    }

    private Padding Chip(string label, DebugLogLevel level)
    {
        var box = new DecoratedBox { Radius = 4f };
        var text = new Label(label, DevKit.CaptionSize) { MaxLines = 1 };

        void Recolor()
        {
            var t = App.Active?.Theme ?? ThemeData.Dark;
            var on = !_hidden.Contains(level);
            box.Fill = on ? LevelColor(level, t).WithAlpha(0.22f) : Color.Transparent;
            box.BorderColor = on ? LevelColor(level, t).WithAlpha(0.5f) : t.Separator;
            text.Color = on ? t.OnSurface : t.Hint;
        }

        box.Child = new Padding(EdgeInsets.Symmetric(Spacing.Sm, 2f), text);
        Recolor();
        return new Padding(
            EdgeInsets.Only(right: Spacing.Xs),
            new Pressable {
                Child = box,
                FocusRadius = 4f,
                OnPressed = () =>
                {
                    if (!_hidden.Add(level)) _hidden.Remove(level);
                    _dirty = true;
                    Recolor();
                },
                OnStateChanged = Recolor,
            }
        );
    }

    private static Color LevelColor(DebugLogLevel level, ThemeData t)
    {
        return level switch {
            DebugLogLevel.Error or DebugLogLevel.Fatal => t.Error,
            DebugLogLevel.Warning => Color.Amber,
            DebugLogLevel.Info => t.OnSurface,
            _ => t.Hint,
        };
    }
}
