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
        // A Watch has no property worth summarising, but it has the one number the inspector is most
        // often opened to find: how often this subtree has rebuilt. The row that keeps climbing while
        // the screen is still is the one to look at.
        if (w is Watch watch)
            return watch.RebuildCount == 0 ? null : $"{watch.RebuildCount} rebuilds";

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
    ///     unpaintable bounds are skipped. Unlike <see cref="Widget.HitTest" /> this does not require
    ///     hit-transparency to pick a widget, so decorative leaves (labels, boxes) stay pickable — it
    ///     only consults it to break a tie between overlapping siblings, see below.
    /// </summary>
    public static Widget? DeepestAt(Widget root, Offset point, int maxDepth = 64)
    {
        return Descend(root, point, maxDepth);

        static Widget? Descend(Widget w, Offset p, int depth)
        {
            var b = w.Bounds;
            var self = b is { Width: > 0f, Height: > 0f } && b.Contains(p.X, p.Y);
            if (depth <= 0) return self ? w : null;

            var kids = Children(w);
            var list = kids as IReadOnlyList<Widget> ?? kids.ToList();

            // Topmost-first, but a sibling only *wins* if the real hit test also reaches into it.
            // A full-screen overlay layer that is currently showing nothing (AdwToastOverlay's
            // Align with a null child, a dismissed scrim, an idle drag layer) covers the whole
            // window by bounds while being completely transparent to input, and taking it on bounds
            // alone made every pick anywhere on screen select that empty layer. Its bounds hit is
            // kept only as a fallback, for when no sibling claims the point at all — that is what
            // keeps a wholly decorative subtree (a Column of Labels) pickable.
            Widget? best = null;
            Widget? fallback = null;
            for (var i = list.Count - 1; i >= 0 && best is null; i--)
            {
                var hit = Descend(list[i], p, depth - 1);
                if (hit is null) continue;
                fallback ??= hit;
                if (list[i].HitTest(p) is not null) best = hit;
            }

            return best ?? fallback ?? (self ? w : null);
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
        var list = new List<(string, string)> {
            ("Type", w.GetType().Name),
            ("Bounds",
                $"{w.Bounds.X:0.#}, {w.Bounds.Y:0.#}  {w.Bounds.Width:0.#}×{w.Bounds.Height:0.#}"),
            ("Dirty", $"B:{Bit(w.NeedsBuild)} L:{Bit(w.NeedsLayout)} P:{Bit(w.NeedsPaint)}"),
            ("Counts", $"M:{w.MeasureCount} L:{w.LayoutCount} P:{w.PaintCount} R:{w.RebuildCount}"),
        };

        foreach (var m in Members(w)) list.Add((m.Name, m.Value));
        return list;
    }

    // ── Nested member walk (property tree / JSON view) ──

    /// <summary>
    ///     One row of the property tree: the display name, its formatted one-line value, the boxed
    ///     value itself (null when the property is null) and whether it has members worth expanding.
    /// </summary>
    public readonly record struct DebugMember(
        string Name,
        string Value,
        object? Raw,
        bool Expandable
    );

    /// <summary>Cap on how many elements of a collection are listed / serialized.</summary>
    private const int MaxItems = 200;

    /// <summary>Values that <see cref="Format" /> already renders in full — never expanded.</summary>
    private static bool IsLeaf(object v)
    {
        return v is string or char or bool or Enum or Delegate or Color or Size or Offset or Rect
                   or EdgeInsets or Alignment or decimal ||
               v.GetType().IsPrimitive;
    }

    /// <summary>True when a value has nested members the inspector can drill into. Type-level, so cheap.</summary>
    public static bool CanExpand(object? v)
    {
        return v is not null && !IsLeaf(v) &&
               (v is IEnumerable || MemberProps(v.GetType()).Length > 0);
    }

    private static PropertyInfo[] MemberProps(Type type)
    {
        return ObjProps.GetOrAdd(
            type,
            static t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                .Where(p => !typeof(Widget).IsAssignableFrom(p.PropertyType))
                .Where(p => !typeof(IEnumerable<Widget>).IsAssignableFrom(p.PropertyType))
                .OrderBy(p => p.DeclaringType == t ? 0 : 1)
                .ToArray()
        );
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ObjProps = new();

    /// <summary>
    ///     The inspectable members of any value: a widget's reflected properties (declared ones first,
    ///     plumbing skipped), a collection's elements, or a plain object's public properties. Null
    ///     properties are dropped for widgets (noise) but kept for nested objects, where "this token is
    ///     unset" is the answer you opened the row for.
    /// </summary>
    public static List<DebugMember> Members(object o)
    {
        var list = new List<DebugMember>();
        var isWidget = o is Widget;

        if (o is Widget w)
        {
            list.Add(
                new DebugMember(
                    "Focusable",
                    Bool(w.Focusable),
                    w.Focusable,
                    false
                )
            );
            if (w.Key is not null) list.Add(Member("Key", w.Key));
            if (w.TooltipText is not null) list.Add(Member("Tooltip", w.TooltipText));
        }
        else if (o is IEnumerable seq)
        {
            var i = 0;
            foreach (var item in seq)
            {
                if (i >= MaxItems)
                {
                    list.Add(
                        new DebugMember(
                            "…",
                            "more elements",
                            null,
                            false
                        )
                    );
                    break;
                }

                list.Add(Member($"[{i++}]", item));
            }

            return list;
        }

        // Declared (most-derived) properties first so the type-specific state (Text/Value/Color/…)
        // shows above the inherited base plumbing.
        var props = isWidget
            ? DumpProps.GetOrAdd(
                o.GetType(),
                static t => MemberProps(t).Where(p => !SkipProps.Contains(p.Name)).ToArray()
            )
            : MemberProps(o.GetType());

        foreach (var prop in props)
        {
            object? val;
            try
            {
                val = prop.GetValue(o);
            }
            catch
            {
                continue;
            }

            if (val is null && isWidget) continue;
            list.Add(Member(prop.Name, val));
        }

        return list;
    }

    private static DebugMember Member(string name, object? val)
    {
        if (val is null)
            return new DebugMember(
                name,
                "null",
                null,
                false
            );

        var expandable = CanExpand(val);
        string s;
        try
        {
            // An expandable object's own ToString is the noise the tree exists to replace — a record's
            // full member dump on one clipped line. Show the type; the children carry the detail.
            s = expandable && val is not IEnumerable ? "{" + val.GetType().Name + "}" : Format(val);
        }
        catch
        {
            s = "{" + val.GetType().Name + "}";
        }

        if (s.Length > 160) s = s[..157] + "…";
        return new DebugMember(
            name,
            s,
            val,
            expandable
        );
    }

    /// <summary>
    ///     The same member walk rendered as JSON, for copying a whole widget's state out in one go.
    ///     Bounded on every axis a live object graph can run away on: <paramref name="maxDepth" />,
    ///     <see cref="MaxItems" /> per collection, a reference-cycle guard, and a total length cap.
    /// </summary>
    public static string ToJson(object? root, int maxDepth = 4, int maxChars = 64_000)
    {
        var sb = new System.Text.StringBuilder();
        var path = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Write(root, 0);
        if (sb.Length >= maxChars) sb.Append("\n…truncated");
        return sb.ToString();

        void Write(object? v, int depth)
        {
            switch (v)
            {
                case null:
                    sb.Append("null");
                    return;
                case bool b:
                    sb.Append(Bool(b));
                    return;
                case float f:
                    sb.Append(Num(f));
                    return;
                case double d:
                    sb.Append(Num((float)d));
                    return;
                case string s:
                    Str(s);
                    return;
            }

            if (v.GetType().IsPrimitive && v is not char)
            {
                sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            if (!CanExpand(v))
            {
                Str(Format(v));
                return;
            }

            if (depth >= maxDepth || sb.Length >= maxChars)
            {
                Str(v is IEnumerable ? "[…]" : "{" + v.GetType().Name + "}");
                return;
            }

            if (!path.Add(v))
            {
                Str("↻ " + v.GetType().Name);
                return;
            }

            var array = v is IEnumerable;
            var members = Members(v);
            sb.Append(array ? '[' : '{');
            for (var i = 0; i < members.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('\n').Append(' ', (depth + 1) * 2);
                if (!array) Str(members[i].Name).Append(": ");
                if (members[i].Raw is null && members[i].Value is not "null") Str(members[i].Value);
                else Write(members[i].Raw, depth + 1);
                if (sb.Length >= maxChars) break;
            }

            if (members.Count > 0) sb.Append('\n').Append(' ', depth * 2);
            sb.Append(array ? ']' : '}');
            path.Remove(v);
        }

        System.Text.StringBuilder Str(string s)
        {
            sb.Append('"');
            foreach (var c in s)
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }

            return sb.Append('"');
        }
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