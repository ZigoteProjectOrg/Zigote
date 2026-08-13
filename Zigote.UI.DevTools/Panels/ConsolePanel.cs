using Zigote.Core;
using Zigote.Core.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     Interactive console: a real focusable <see cref="DevConsoleField" /> over a live tail of the
///     <see cref="DebugLog" /> (command echoes + results). Runs the <see cref="DebugCommands" />
///     registry —
///     try <c>help</c>. Click the field (or focus it) to type; Tab completes, ↑/↓ recall history.
/// </summary>
public sealed class ConsolePanel : IDevPanel
{
    private const int MaxRows = 250;

    private readonly List<DebugLogEntry> _all = [];
    private readonly DevConsoleField _field = new();

    private readonly Column _tail = new(
        crossAxisAlignment: CrossAxisAlignment.Stretch,
        mainAxisSize: MainAxisSize.Min
    );

    private int _version = -1;

    public string Title => "Console";
    public DevCategory Category => DevCategory.Generic;

    public Widget Build(BuildContext context)
    {
        _field.OnSubmitted = () => _version = -1;
        return new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        ) {
            Children = {
                _field,
                new DevSectionHeader("Output"),
                _tail,
            },
        };
    }

    public void Refresh(float dt)
    {
        if (DebugLog.Version == _version) return;
        _version = DebugLog.Version;

        DebugLog.CopyInto(_all);
        var t = App.Active?.Theme ?? ThemeData.Dark;
        var rows = new List<Widget>();
        int start = Math.Max(val1: 0, val2: _all.Count - MaxRows);
        for (int i = start; i < _all.Count; i++)
        {
            var e = _all[i];
            rows.Add(
                new Label(
                    text: e.Message,
                    fontSize: DevKit.CaptionSize,
                    color: LevelColor(level: e.Level, t: t)
                ) {
                    MaxLines = 1,
                    Overflow = TextOverflow.Ellipsis,
                    FontFamily = "code",
                }
            );
        }

        if (rows.Count == 0) rows.Add(new DevNote("Console ready — type 'help'."));
        _tail.SetChildren(rows);
    }

    private static Color LevelColor(DebugLogLevel level, ThemeData t)
    {
        return level switch {
            DebugLogLevel.Error or DebugLogLevel.Fatal => t.Error,
            DebugLogLevel.Warning => Color.Amber,
            _ => t.OnSurface,
        };
    }
}
