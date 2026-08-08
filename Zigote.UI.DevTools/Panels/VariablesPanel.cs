using System.Globalization;
using Zigote.Core.Diagnostics;
using Zigote.UI.DevTools.Widgets;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.DevTools.Panels;

/// <summary>
///     The editable <see cref="DebugVariables" /> registry: bool → toggle, enum → cycle stepper,
///     int/float → ± stepper, read-only → value row, grouped by category. Every edit goes through
///     <see cref="DebugVariable.TrySet" /> (which validates + clamps). Displays refresh each frame so a
///     variable changed elsewhere (console, another panel) stays in sync.
/// </summary>
public sealed class VariablesPanel : IDevPanel
{
    private readonly List<Action> _sync = [];

    public string Title => "Variables";
    public DevCategory Category => DevCategory.Generic;

    public Widget Build(BuildContext context)
    {
        _sync.Clear();
        var col = new Column(
            crossAxisAlignment: CrossAxisAlignment.Stretch,
            mainAxisSize: MainAxisSize.Min
        );
        string? category = null;
        foreach (var v in DebugVariables.All)
        {
            if (v.Category != category)
            {
                category = v.Category;
                col.Children.Add(new DevSectionHeader(category));
            }

            col.Children.Add(RowFor(v));
        }

        if (col.Children.Count == 0)
            col.Children.Add(new DevNote("No debug variables registered."));
        return col;
    }

    public void Refresh(float dt)
    {
        foreach (var s in _sync) s();
    }

    private Widget RowFor(DebugVariable v)
    {
        if (v.IsReadOnly)
        {
            var row = new DevKeyValue(v.Name, v.Display());
            _sync.Add(() => row.Value = v.Display());
            return row;
        }

        switch (v.Type)
        {
            case DebugVarType.Bool:
            {
                var toggle = new DevToggle(v.Name, Bool(v), on => v.TrySet(on ? "true" : "false"));
                _sync.Add(() => toggle.Value = Bool(v));
                return toggle;
            }
            case DebugVarType.Enum:
            {
                var step = new DevStepper(
                    v.Name,
                    v.Display(),
                    () => CycleEnum(v, -1),
                    () => CycleEnum(v, 1)
                );
                _sync.Add(() => step.Value = v.Display());
                return step;
            }
            default:
            {
                var step = new DevStepper(
                    v.Name,
                    v.Display(),
                    () => Nudge(v, -1),
                    () => Nudge(v, 1)
                );
                _sync.Add(() => step.Value = v.Display());
                return step;
            }
        }
    }

    private static bool Bool(DebugVariable v)
    {
        return v.Value is bool b && b;
    }

    private static void CycleEnum(DebugVariable v, int dir)
    {
        if (v.EnumNames is not { Length: > 0 } names) return;
        var idx = v.Value is int i ? i : 0;
        v.TrySet(names[(idx + dir + names.Length) % names.Length]);
    }

    private static void Nudge(DebugVariable v, int dir)
    {
        if (v.Type == DebugVarType.Int)
        {
            var cur = v.Value is int i ? i : 0;
            v.TrySet((cur + dir).ToString(CultureInfo.InvariantCulture));
            return;
        }

        var f = v.Value is float x ? x : 0f;
        var stepSize = v.Min is float mn && v.Max is float mx && mx > mn ? (mx - mn) / 20f : 0.05f;
        v.TrySet((f + dir * stepSize).ToString("0.###", CultureInfo.InvariantCulture));
    }
}