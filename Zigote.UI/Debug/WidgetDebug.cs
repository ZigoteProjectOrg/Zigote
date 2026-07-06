using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Zigote.Core;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Debug;

/// <summary>
///     Reflective widget-tree introspection shared by the 2D UI inspector panel and the overlay's
///     on-screen layers (repaint rainbow / overflow). Kept in one place so both walk the tree
///     identically.
/// </summary>
public static class WidgetDebug
{
    /// <summary>Names handled by the fixed header rows or never useful in the dump.</summary>
    private static readonly HashSet<string> SkipProps = new(StringComparer.Ordinal) {
        "Bounds",
        "Focusable",
        "Focused",
        "TooltipText",
        "ScrollParent",
        "Parent",
        "Owner",
        "Key",
        "DebugLastConstraints",
        "MeasuredSize",
        "MeasureCount",
        "LayoutCount",
        "PaintCount",
        "RebuildCount",
        "NeedsBuild",
        "NeedsLayout",
        "NeedsPaint",
        "SemanticsId",
    };

    // The debug layers walk the whole tree every frame, so the reflection fallback must not
    // re-run GetProperties (a fresh PropertyInfo[] per call) or GetValue value-type props (a box
    // per call) — cache, per type, only the properties that could actually hold widgets.
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ChildProps = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> DumpProps = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> SummaryProps = new();

    /// <summary>Content-bearing properties worth surfacing inline next to the type name, best first.</summary>
    private static readonly string[] SummaryNames = ["Text", "Label", "Hint", "Value", "Glyph"];

    private static bool CouldHoldWidgets(Type pt)
    {
        return typeof(Widget).IsAssignableFrom(pt) ||
               typeof(IEnumerable<Widget>).IsAssignableFrom(pt) ||
               pt.IsAssignableFrom(typeof(Widget)) ||
               (typeof(IEnumerable).IsAssignableFrom(pt) && pt != typeof(string));
    }

    private static PropertyInfo[] ChildCandidates(Type type)
    {
        return ChildProps.GetOrAdd(
            type,
            static t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                .Where(p =>
                    p.Name is not ("Items" or "Cells" or "Theme" or "ScrollParent" or "Parent")
                )
                .Where(p => CouldHoldWidgets(p.PropertyType))
                .ToArray()
        );
    }

    /// <summary>
    ///     The logical children of a widget — its declared <see cref="Widget.GetChildren" /> if any,
    ///     otherwise any <see cref="Widget" /> / <see cref="IEnumerable{Widget}" /> public properties.
    /// </summary>
    public static IEnumerable<Widget> Children(Widget w)
    {
        var children = w.GetChildren();
        if (children is IReadOnlyCollection<Widget> rc)
        {
            if (rc.Count > 0) return children;
        }
        else if (children != null && children.Any())
        {
            return children;
        }

        var props = ChildCandidates(w.GetType());
        if (props.Length == 0) return [];

        List<Widget>? result = null;
        foreach (var prop in props)
        {
            object? val;
            try
            {
                val = prop.GetValue(w);
            }
            catch
            {
                continue;
            }

            if (val is IEnumerable<Widget> list) (result ??= []).AddRange(list.OfType<Widget>());
            else if (val is Widget child) (result ??= []).Add(child);
        }

        return result ?? (IEnumerable<Widget>)[];
    }

    /// <summary>
    ///     A short inline detail for a tree row: the widget's key text (Label/Button
    ///     text, slider value, …) if it has one, else its <see cref="Widget.Key" />. Null when there is
    ///     nothing more useful than the type name. Cold path — runs only on inspector tree rebuilds.
    /// </summary>
    public static string? Describe(Widget w)
    {
        var prop = SummaryProps.GetOrAdd(
            w.GetType(),
            static t =>
            {
                foreach (var name in SummaryNames)
                {
                    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p is { CanRead: true } && p.GetIndexParameters().Length == 0 &&
                        !typeof(Widget).IsAssignableFrom(p.PropertyType) &&
                        !typeof(Delegate).IsAssignableFrom(p.PropertyType))
                        return p;
                }

                return null;
            }
        );

        if (prop is not null)
        {
            object? val;
            try
            {
                val = prop.GetValue(w);
            }
            catch
            {
                val = null;
            }

            if (val is not null)
            {
                var s = Format(val);
                if (s.Length > 0 && s != "\"\"") return s.Length > 40 ? s[..37] + "…" : s;
            }
        }

        return w.Key is not null ? "key: " + w.Key : null;
    }

    /// <summary>
    ///     The deepest widget whose bounds contain <paramref name="point" /> — the inspector's
    ///     tap-to-select pick. Later (topmost-painted) children win over earlier ones; widgets with
    ///     unpaintable bounds are skipped. Unlike <see cref="Widget.HitTest" /> this never consults
    ///     hit-transparency, so decorative leaves (labels, boxes) are pickable.
    /// </summary>
    public static Widget? DeepestAt(Widget root, Offset point, int maxDepth = 64)
    {
        return Descend(root, point, maxDepth);

        static Widget? Descend(Widget w, Offset p, int depth)
        {
            var b = w.Bounds;
            var self = b is { Width: > 0f, Height: > 0f } && b.Contains(p.X, p.Y);
            if (depth <= 0) return self ? w : null;

            Widget? best = null;
            var kids = Children(w);
            if (kids is IReadOnlyList<Widget> list)
            {
                for (var i = list.Count - 1; i >= 0 && best is null; i--)
                    best = Descend(list[i], p, depth - 1);
            }
            else
            {
                // Cold path: materialize to walk topmost-first.
                var all = kids.ToList();
                for (var i = all.Count - 1; i >= 0 && best is null; i--)
                    best = Descend(all[i], p, depth - 1);
            }

            return best ?? (self ? w : null);
        }
    }

    /// <summary>The root→widget ancestor chain (inclusive), via <see cref="Widget.Parent" />.</summary>
    public static List<Widget> PathTo(Widget w, int cap = 64)
    {
        var path = new List<Widget> { w };
        var cur = w.Parent;
        while (cur is not null && path.Count < cap)
        {
            path.Add(cur);
            cur = cur.Parent;
        }

        path.Reverse();
        return path;
    }

    /// <summary>"0≤w≤400 · 28≤h≤∞" — the constraints a widget last measured against.</summary>
    public static string FormatConstraints(Constraints c)
    {
        return c.MinWidth == c.MaxWidth && c.MinHeight == c.MaxHeight
            ? $"tight {Num(c.MaxWidth)}×{Num(c.MaxHeight)}"
            : $"{Num(c.MinWidth)}≤w≤{Num(c.MaxWidth)} · {Num(c.MinHeight)}≤h≤{Num(c.MaxHeight)}";
    }

    /// <summary>Count nodes in the tree (capped to avoid pathological graphs).</summary>
    public static int Count(Widget? w, int cap = 5000)
    {
        if (w is null) return 0;
        var n = 0;
        var stack = new Stack<Widget>();
        var seen = new HashSet<Widget>(ReferenceEqualityComparer.Instance);
        stack.Push(w);
        while (stack.Count > 0 && n < cap)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur)) continue;
            n++;
            var kids = Children(cur);
            if (kids is IReadOnlyList<Widget> list)
                for (var i = 0; i < list.Count; i++)
                    stack.Push(list[i]);
            else
                foreach (var c in kids)
                    stack.Push(c);
        }

        return n;
    }

    /// <summary>A small, human-readable property list for the inspector (header rows + reflected props).</summary>
    public static List<(string Name, string Value)> Properties(Widget w)
    {
        var type = w.GetType();
        var list = new List<(string, string)> {
            ("Type", type.Name),
            ("Bounds",
                $"{w.Bounds.X:0.#}, {w.Bounds.Y:0.#}  {w.Bounds.Width:0.#}×{w.Bounds.Height:0.#}"),
            ("Dirty", $"B:{Bit(w.NeedsBuild)} L:{Bit(w.NeedsLayout)} P:{Bit(w.NeedsPaint)}"),
            ("Counts", $"M:{w.MeasureCount} L:{w.LayoutCount} P:{w.PaintCount} R:{w.RebuildCount}"),
            ("Focusable", Bool(w.Focusable)),
        };

        if (w.Key is not null) list.Add(("Key", w.Key.ToString() ?? ""));
        if (w.TooltipText is not null) list.Add(("Tooltip", Format(w.TooltipText)));

        // Reflect the widget's properties, declared (most-derived) ones first so the type-specific state
        // (Text/Value/Checked/Color/…) shows above the inherited base-widget plumbing.
        var props = DumpProps.GetOrAdd(
            type,
            static t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p =>
                    p.GetIndexParameters().Length == 0 && p.CanRead && !SkipProps.Contains(p.Name)
                )
                .Where(p => !typeof(Widget).IsAssignableFrom(p.PropertyType))
                .Where(p => !typeof(IEnumerable<Widget>).IsAssignableFrom(p.PropertyType))
                .OrderBy(p => p.DeclaringType == t ? 0 : 1)
                .ToArray()
        );

        foreach (var prop in props)
        {
            object? val;
            try
            {
                val = prop.GetValue(w);
            }
            catch
            {
                continue;
            }

            if (val is null) continue;

            string s;
            try
            {
                s = Format(val);
            }
            catch
            {
                s = "{" + val.GetType().Name + "}";
            }

            if (s.Length > 160) s = s[..157] + "…";
            list.Add((prop.Name, s));
        }

        return list;
    }

    private static string Bool(bool b)
    {
        return b ? "true" : "false";
    }

    private static string Bit(bool b)
    {
        return b ? "1" : "0";
    }

    private static string Num(float f)
    {
        if (float.IsNaN(f)) return "NaN";
        if (float.IsInfinity(f)) return f > 0 ? "∞" : "-∞";
        if (MathF.Abs(f) < 1e9f && f == MathF.Floor(f)) return ((long)f).ToString();
        return f.ToString("0.###");
    }

    /// <summary>Turn any property value into a compact, readable string for the inspector.</summary>
    private static string Format(object val)
    {
        switch (val)
        {
            case bool b: return Bool(b);
            case string str: return str.Length == 0 ? "\"\"" : "\"" + Escape(str) + "\"";
            case char ch: return $"'{ch}'";
            case float f: return Num(f);
            case double d: return Num((float)d);
            case Color col:
                var hex = $"#{(int)(col.R * 255):X2}{(int)(col.G * 255):X2}{(int)(col.B * 255):X2}";
                return col.A < 0.999f ? $"{hex} a{col.A:0.##}" : hex;
            case Size sz: return $"{Num(sz.Width)}×{Num(sz.Height)}";
            case Offset off: return $"({Num(off.X)}, {Num(off.Y)})";
            case Rect r: return $"{Num(r.X)},{Num(r.Y)} {Num(r.Width)}×{Num(r.Height)}";
            case EdgeInsets e:
                return $"L{Num(e.Left)} T{Num(e.Top)} R{Num(e.Right)} B{Num(e.Bottom)}";
            case Alignment a: return $"({a.X:0.##}, {a.Y:0.##})";
            case Enum en: return en.ToString();
            case Delegate del: return FormatDelegate(del);
            case ICollection coll: return $"[{coll.Count}]";
            case IEnumerable: return "[…]";
            default:
                var s = val.ToString() ?? "";
                // Default object.ToString returns the type name — show a short braced form instead.
                return s.Length == 0 || s == val.GetType().ToString()
                    ? "{" + val.GetType().Name + "}"
                    : s;
        }
    }

    private static string FormatDelegate(Delegate del)
    {
        var handlers = del.GetInvocationList().Length;
        if (handlers > 1) return $"ƒ ×{handlers}";
        var name = del.Method.Name;
        // Compiler-generated lambda / local-function names look like "<Build>b__7_0" — show them as ƒ.
        return name.StartsWith('<') ? "ƒ" : $"ƒ {name}";
    }

    private static string Escape(string s)
    {
        return s.Replace("\r", "").Replace("\n", "⏎").Replace("\t", "⇥");
    }
}