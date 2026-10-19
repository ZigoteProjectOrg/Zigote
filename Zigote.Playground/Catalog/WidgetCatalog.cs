using System.Collections;
using System.Reflection;
using System.Text;

namespace Playground.Catalog;

internal sealed record CatalogEntry(Type Type, string Library, string Category)
{
    public string Title => WidgetCatalog.Name(Type);
}

internal sealed record CatalogSection(string Title, CatalogEntry[] Entries);

/// <summary>
///     The widget catalog, generated from the libraries themselves: every public, concrete
///     <see cref="Widget" /> in the kernel and in Adwaita, documented from the XML doc comments that
///     ship beside each DLL. Nothing here is written by hand except the optional curated samples in
///     <c>Catalog/Samples/</c>, so the catalog cannot drift from the code — a new widget appears the
///     next time the app starts.
/// </summary>
internal static class WidgetCatalog
{
    private static readonly Assembly Kernel = typeof(Widget).Assembly;
    private static readonly Assembly Adwaita = typeof(AdwButton).Assembly;

    // Declared before Sections: static initializers run in order, and Discover() formats names.
    private static readonly Dictionary<Type, string> Aliases = new() {
        [typeof(string)] = "string", [typeof(bool)] = "bool", [typeof(int)] = "int", [typeof(uint)] = "uint",
        [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double",
        [typeof(object)] = "object", [typeof(void)] = "void", [typeof(byte)] = "byte", [typeof(char)] = "char",
        [typeof(decimal)] = "decimal", [typeof(short)] = "short",
    };

    // Base-class plumbing every widget has; listing it on every page would bury what is specific.
    private static readonly HashSet<Type> PlumbingTypes = [typeof(Widget), typeof(LeafWidget), typeof(ComposedWidget), typeof(object)];

    public static readonly CatalogSection[] Sections = Discover();
    public static readonly CatalogEntry[] Entries = [.. Sections.SelectMany(s => s.Entries)];

    private static CatalogSection[] Discover()
    {
        XmlDocs.Load(Kernel);
        XmlDocs.Load(Adwaita);
        XmlDocs.Load(typeof(Color).Assembly);
        var entries = new List<CatalogEntry>();
        foreach (var asm in new[] { Kernel, Adwaita })
        {
            foreach (var t in asm.GetExportedTypes())
            {
                if (!t.IsClass || t.IsAbstract || t.IsNested || !typeof(Widget).IsAssignableFrom(t)) continue;
                if (t.GetCustomAttribute<ObsoleteAttribute>() is not null) continue;
                bool adw = asm == Adwaita;
                entries.Add(new CatalogEntry(t, adw ? "Adwaita" : "Kernel", adw ? AdwaitaCategory(t.Name) : KernelCategory(t)));
            }
        }

        return [
            .. entries
                .GroupBy(e => $"{e.Library} · {e.Category}")
                .OrderBy(g => g.First().Library == "Kernel" ? 0 : 1)
                .ThenBy(g => g.Key)
                .Select(g => new CatalogSection(g.Key, [.. g.OrderBy(e => e.Title, StringComparer.Ordinal)])),
        ];
    }

    private static string KernelCategory(Type t) => (t.Namespace ?? "").Split('.')[^1] switch {
        "Layout" => "Layout",
        "Controls" => "Controls",
        "Transitions" => "Transitions",
        "Navigation" => "Navigation",
        "Painting" => "Painting",
        "Menu" => "Menus",
        "Overlays" => "Overlays",
        "LiquidGlass" => "Liquid Glass",
        "Widgets" => "Core",
        var other => other,
    };

    // Adwaita is one namespace; group by what the name says the widget is. First match wins.
    private static string AdwaitaCategory(string name)
    {
        static bool Has(string n, params string[] parts) => parts.Any(n.Contains);
        if (Has(name, "Row", "PreferencesGroup", "PreferencesPage")) return "Boxed Lists & Preferences";
        if (Has(name, "Dialog", "Toast", "Banner", "Popover", "Menu", "Tooltip", "Sheet", "Alert")) return "Dialogs, Menus & Feedback";
        if (Has(name, "Entry", "TextField", "Spin", "Number", "Slider", "Scale", "Date", "Time", "Calendar", "Color", "Curve", "Gradient", "DropDown", "Combo", "Editor")) return "Inputs & Editors";
        if (Has(name, "Button", "Toggle", "Switch", "Check", "Chip", "Link", "Radio")) return "Buttons & Toggles";
        if (Has(name, "Navigation", "View", "Tab", "Split", "Paned", "Header", "Toolbar", "Window", "Sidebar", "Clamp", "Breakpoint", "Carousel", "Overlay", "Grid", "List", "Tree", "Column")) return "Layout & Navigation";
        if (Has(name, "Liquid", "Glass", "Surface", "Panel")) return "Materials & Surfaces";
        return "Display";
    }

    // ── Page generation ───────────────────────────────────────────────────────

    /// <summary>The overview page: what the catalog is, and what is in it.</summary>
    public static string IndexMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Widget Catalog").AppendLine();
        sb.AppendLine($"Every public widget in `Zigote.UI` (the kernel) and `Zigote.UI.Adwaita` — **{Entries.Length} widgets**, generated at startup from the libraries themselves and the XML documentation comments shipped beside them. Each page lists the constructors, every property with its default value, the methods and events, and a live sample you can edit and run.").AppendLine();
        sb.AppendLine("> The catalog is reference material, generated from code. The hand-written guides live under **Docs**. A sample marked *generated* is the simplest construction the generator could find; curated samples live in `Zigote.Playground/Catalog/Samples/`.").AppendLine();
        sb.AppendLine("Type names on every page are links: follow a property's type to see its enum values or presets, and use **Back** in the header to return.").AppendLine();
        foreach (var s in Sections)
        {
            sb.AppendLine($"## {s.Title}").AppendLine();
            sb.AppendLine(string.Join(" · ", s.Entries.Select(e => Link(e.Type)))).AppendLine();
        }

        return sb.ToString();
    }

    public static string PageMarkdown(CatalogEntry entry)
    {
        var t = entry.Type;
        var doc = XmlDocs.Type(t);
        var sb = new StringBuilder();
        sb.AppendLine($"# {entry.Title}").AppendLine();

        var chain = new List<string>();
        for (var b = t.BaseType; b is not null && b != typeof(object); b = b.BaseType) chain.Add(Link(b));
        sb.AppendLine($"`{t.Namespace}` · {entry.Library} · {entry.Category} · inherits {string.Join(" → ", chain)}").AppendLine();

        string summary = XmlDocs.Summary(doc, withRemarks: true);
        sb.AppendLine(summary.Length > 0 ? summary : "*No documentation comment yet.*").AppendLine();

        if (Sample(t) is { } sample)
        {
            sb.AppendLine("## Example").AppendLine();
            sb.AppendLine($"```csharp run height={sample.Height}").AppendLine(sample.Source).AppendLine("```").AppendLine();
            if (sample.Generated)
                sb.AppendLine("> *Generated sample* — the simplest construction the generator found. Change the arguments, set properties in an initializer `{ … }`, or wrap it in other widgets, then press Run.").AppendLine();
        }

        if (ActsOnWindow(t))
            sb.AppendLine("> No live sample: this widget acts on the real window (its buttons minimise, maximise and close it). Use it in your app's title bar.").AppendLine();

        AppendConstructors(sb, t);
        AppendProperties(sb, t);
        AppendMethods(sb, t);
        AppendEvents(sb, t);

        var subclasses = Entries.Where(e => e.Type.BaseType == t).Select(e => Link(e.Type)).ToArray();
        if (subclasses.Length > 0) sb.AppendLine("## Subclasses").AppendLine().AppendLine(string.Join(", ", subclasses)).AppendLine();
        return sb.ToString();
    }

    private static void AppendConstructors(StringBuilder sb, Type t)
    {
        var ctors = t.GetConstructors().OrderBy(c => c.GetParameters().Length).ToArray();
        if (ctors.Length == 0) return;
        sb.AppendLine("## Constructors").AppendLine().AppendLine("```csharp");
        foreach (var c in ctors)
            sb.AppendLine($"new {Name(t)}({string.Join(", ", c.GetParameters().Select(Signature))})");
        sb.AppendLine("```").AppendLine();

        // Every parameter once across the overloads: its type (a link when it is a Zigote type) and its doc.
        var seen = new HashSet<string>();
        foreach (var c in ctors)
        {
            var cdoc = XmlDocs.Member(c);
            foreach (var p in c.GetParameters())
            {
                if (!seen.Add(p.Name!)) continue;
                string text = XmlDocs.Param(cdoc, p.Name!);
                sb.AppendLine($"- `{p.Name}` ({Link(p.ParameterType)}){(text.Length > 0 ? " — " + text : "")}");
            }
        }

        if (seen.Count > 0) sb.AppendLine();
    }

    private static void AppendProperties(StringBuilder sb, Type t)
    {
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => !PlumbingTypes.Contains(p.DeclaringType!) && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();
        if (props.Length == 0) return;

        object? instance = TryCreateDefault(t);
        sb.AppendLine("## Properties").AppendLine();
        sb.AppendLine("| Property | Type | Default | Description |").AppendLine("|---|---|---|---|");
        foreach (var p in props)
        {
            string type = Link(p.PropertyType) + (p.SetMethod is { IsPublic: true } ? "" : " (read-only)");
            string dflt = instance is null ? "—" : DefaultOf(p, instance);
            string inherited = p.DeclaringType == t ? "" : $" *(from {Link(p.DeclaringType!)})*";
            sb.AppendLine($"| `{p.Name}` | {type} | {dflt} | {XmlDocs.Brief(XmlDocs.Member(p))}{inherited} |");
        }

        sb.AppendLine();
    }

    private static void AppendMethods(StringBuilder sb, Type t)
    {
        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetBaseDefinition().DeclaringType == t)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();
        if (methods.Length == 0) return;
        sb.AppendLine("## Methods").AppendLine();
        foreach (var m in methods)
        {
            string sig = $"{(m.IsStatic ? "static " : "")}{Name(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(Signature))})";
            string brief = XmlDocs.Brief(XmlDocs.Member(m));
            sb.AppendLine(brief.Length > 0 ? $"- `{sig}` — {brief}" : $"- `{sig}`");
        }

        sb.AppendLine();
    }

    private static void AppendEvents(StringBuilder sb, Type t)
    {
        var events = t.GetEvents(BindingFlags.Public | BindingFlags.Instance).Where(e => !PlumbingTypes.Contains(e.DeclaringType!)).ToArray();
        if (events.Length == 0) return;
        sb.AppendLine("## Events").AppendLine();
        foreach (var e in events)
        {
            string brief = XmlDocs.Brief(XmlDocs.Member(e));
            sb.AppendLine($"- `{e.Name}` ({Link(e.EventHandlerType!)}){(brief.Length > 0 ? " — " + brief : "")}");
        }

        sb.AppendLine();
    }

    // ── Defaults: read from a freshly constructed instance ────────────────────

    private static object? TryCreateDefault(Type t)
    {
        if (t.ContainsGenericParameters) return null;
        var ctor = t.GetConstructors().FirstOrDefault(c => c.GetParameters().All(p => p.IsOptional));
        if (ctor is null) return null;
        try
        {
            return ctor.Invoke([.. ctor.GetParameters().Select(p => p.DefaultValue is DBNull ? null : p.DefaultValue)]);
        }
        catch
        {
            return null; // a constructor that needs a running app: no defaults column, not a failure
        }
    }

    private static string DefaultOf(PropertyInfo p, object instance)
    {
        if (p.GetMethod is null) return "—";
        object? v;
        try
        {
            v = p.GetValue(instance);
        }
        catch
        {
            return "—";
        }

        return Format(v);
    }

    private static string Format(object? v)
    {
        // Values read like C# literals ("0.5"), whatever the reader's locale.
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return FormatInvariant(v);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    private static string FormatInvariant(object? v)
    {
        string text = v switch {
            null => "null",
            string s => $"\"{(s.Length > 24 ? s[..24] + "…" : s)}\"",
            bool b => b ? "true" : "false",
            float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Enum e => $"{e.GetType().Name}.{e}",
            Delegate => "(handler)",
            ICollection c => c.Count == 0 ? "empty" : $"{c.Count} items",
            Widget w => $"{Name(w.GetType())}",
            _ => v.ToString() ?? "",
        };
        text = text.Replace("|", "/").Replace("`", "'");
        return text.Length > 40 ? $"`{text[..39]}…`" : $"`{text}`";
    }

    // ── Links and type pages ──────────────────────────────────────────────────

    /// <summary>
    ///     A type name as Markdown: a link to its catalog page when it is a Zigote type (widgets go
    ///     to their widget page, everything else to a generated type page), plain code otherwise.
    ///     Generic constructions link their Zigote arguments: <c>Signal&lt;[AdwAccent]&gt;</c>.
    /// </summary>
    public static string Link(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is { } inner) return Link(inner) + "?";
        if (t.IsArray) return Link(t.GetElementType()!) + "[]";
        if (t.IsGenericType && !t.IsGenericTypeDefinition)
        {
            var args = t.GetGenericArguments();
            if (!args.Any(TypeIndex.IsZigote) && !TypeIndex.IsZigote(t)) return $"`{Name(t)}`";
            if (t.Name.IndexOf('`') < 0) return $"`{Name(t)}`";
            string baseName = t.Name[..t.Name.IndexOf('`')];
            return $"`{baseName}<`{string.Join("`, `", args.Select(Link))}`>`".Replace("``", "");
        }

        return !t.IsGenericParameter && TypeIndex.IsZigote(t) && t.FullName is { } full
            ? $"[`{Name(t)}`](type:{full})"
            : $"`{Name(t)}`";
    }

    /// <summary>The catalog entry for a widget type, or null for a non-widget (or non-public) type.</summary>
    public static int IndexOf(Type t) => Array.FindIndex(Entries, e => e.Type == t);

    // Which widget properties take each type — the "where can I use this?" list on a type page.
    private static readonly Lazy<Dictionary<Type, List<string>>> UsedBy = new(() =>
    {
        var map = new Dictionary<Type, List<string>>();
        foreach (var e in Entries)
        {
            foreach (var p in e.Type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (!TypeIndex.IsZigote(pt) || typeof(Widget).IsAssignableFrom(pt)) continue;
                if (!map.TryGetValue(pt, out var list)) map[pt] = list = [];
                list.Add($"[`{e.Title}.{p.Name}`](type:{e.Type.FullName})");
            }
        }

        return map;
    });

    /// <summary>A page for any public Zigote type that is not a catalog widget: enums, structs, records, classes.</summary>
    public static string TypePageMarkdown(Type t)
    {
        XmlDocs.Load(t.Assembly);
        var doc = XmlDocs.Type(t);
        var sb = new StringBuilder();
        string kind = t.IsEnum ? (t.GetCustomAttribute<FlagsAttribute>() is null ? "enum" : "flags enum")
            : t.IsInterface ? "interface"
            : t.IsValueType ? (t.GetMethod("<Clone>$") is null ? "struct" : "record struct")
            : t.IsAbstract && t.IsSealed ? "static class"
            : t.GetMethod("<Clone>$") is not null ? "record"
            : t.IsAbstract ? "abstract class" : "class";
        sb.AppendLine($"# {Name(t)}").AppendLine();
        sb.AppendLine($"`{t.Namespace}` · {kind} · `{t.Assembly.GetName().Name}`" +
                      (t.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum) ? $" · inherits {Link(b)}" : "")).AppendLine();
        string summary = XmlDocs.Summary(doc, withRemarks: true);
        sb.AppendLine(summary.Length > 0 ? summary : "*No documentation comment yet.*").AppendLine();

        if (t.IsEnum)
        {
            sb.AppendLine("## Values").AppendLine().AppendLine("| Value | Number | Description |").AppendLine("|---|---|---|");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                sb.AppendLine($"| `{Name(t)}.{f.Name}` | `{Convert.ToInt64(f.GetValue(null))}` | {XmlDocs.Brief(XmlDocs.Member(f))} |");
            sb.AppendLine();
        }
        else
        {
            AppendPresets(sb, t);
            if (!(t.IsAbstract && t.IsSealed)) AppendConstructors(sb, t);
            AppendTypeProperties(sb, t);
            AppendMethods(sb, t);
        }

        if (UsedBy.Value.TryGetValue(t, out var users))
            sb.AppendLine("## Used by").AppendLine().AppendLine(string.Join(" · ", users.Distinct())).AppendLine();
        return sb.ToString();
    }

    // Static fields and properties that hand out values of the type itself: Alignment.Center,
    // ThemeData.Dark, MotionSpec.Bouncy — the named values a reader picks from.
    private static void AppendPresets(StringBuilder sb, Type t)
    {
        var rows = new List<string>();
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == t || f.IsLiteral))
            rows.Add($"| `{Name(t)}.{f.Name}` | {Value(() => f.GetValue(null))} | {XmlDocs.Brief(XmlDocs.Member(f))} |");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == t && p.GetIndexParameters().Length == 0))
            rows.Add($"| `{Name(t)}.{p.Name}` | {Value(() => p.GetValue(null))} | {XmlDocs.Brief(XmlDocs.Member(p))} |");
        if (rows.Count == 0) return;
        sb.AppendLine("## Presets").AppendLine().AppendLine("| Name | Value | Description |").AppendLine("|---|---|---|");
        foreach (string r in rows) sb.AppendLine(r);
        sb.AppendLine();
    }

    private static void AppendTypeProperties(StringBuilder sb, Type t)
    {
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.DeclaringType != typeof(object))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
        if (props.Length == 0) return;
        object? instance = t.IsValueType && !t.ContainsGenericParameters ? Activator.CreateInstance(t) : TryCreateDefault(t);
        sb.AppendLine("## Properties").AppendLine().AppendLine("| Property | Type | Default | Description |").AppendLine("|---|---|---|---|");
        foreach (var p in props)
        {
            string type = Link(p.PropertyType) + (p.SetMethod is { IsPublic: true } ? "" : " (read-only)");
            sb.AppendLine($"| `{p.Name}` | {type} | {(instance is null ? "—" : DefaultOf(p, instance))} | {XmlDocs.Brief(XmlDocs.Member(p))} |");
        }

        sb.AppendLine();
    }

    private static string Value(Func<object?> read)
    {
        try
        {
            return Format(read());
        }
        catch
        {
            return "—";
        }
    }

    // ── Samples ───────────────────────────────────────────────────────────────

    internal sealed record SampleSource(string Source, int Height, bool Generated);

    /// <summary>
    ///     A curated sample from <c>Catalog/Samples/{TypeName}.cs</c> if there is one, otherwise the
    ///     simplest construction the generator can write: the constructor with the fewest required
    ///     parameters, each filled with a plain literal for its type. Null when no constructor can be
    ///     satisfied that way (generic widgets, widgets that take a builder or a model).
    /// </summary>
    // Widgets that act on the real window (minimise, maximise, close, drag): a live preview of one
    // would close the playground the first time someone tried it.
    private static readonly HashSet<string> WindowChrome = ["AdwWindowControls", "WindowTitleBar", "WindowChromeHost"];

    public static bool ActsOnWindow(Type t) => WindowChrome.Contains(t.Name);

    public static SampleSource? Sample(Type t)
    {
        if (ActsOnWindow(t)) return null;
        using (var stream = typeof(WidgetCatalog).Assembly.GetManifestResourceStream($"Samples/{t.Name}.cs"))
        {
            if (stream is not null)
            {
                string src = new StreamReader(stream).ReadToEnd().Trim();
                // A curated sample can ask for its preview height in its first line: // height: 300
                int height = 240;
                if (src.StartsWith("// height:") && src.IndexOf('\n') is var nl and > 0)
                {
                    if (int.TryParse(src[10..nl].Trim(), out int h)) height = h;
                    src = src[(nl + 1)..].TrimStart(); // a layout hint, not part of the example
                }

                return new SampleSource(src, height, false);
            }
        }

        if (t.ContainsGenericParameters) return null;
        foreach (var ctor in t.GetConstructors().OrderBy(c => c.GetParameters().Count(p => !p.IsOptional)))
        {
            var args = new List<string>();
            var usings = new SortedSet<string>(StringComparer.Ordinal) { t.Namespace! };
            bool ok = true;
            foreach (var p in ctor.GetParameters())
            {
                if (p.IsOptional)
                {
                    // Optional parameters that carry visible content are worth filling: a button
                    // with no label or a container with no child previews as an empty box.
                    if (VisibleContent.Contains(p.Name!) && Literal(p.ParameterType) is { } named)
                        args.Add($"{p.Name}: {named}");
                    continue;
                }

                if (Literal(p.ParameterType) is { } lit)
                {
                    args.Add(lit);
                    if (p.ParameterType.IsEnum) usings.Add(p.ParameterType.Namespace!);
                }
                else
                {
                    ok = false;
                    break;
                }
            }

            if (!ok) continue;
            string expr = $"new {Name(t)}({string.Join(", ", args)})";
            // The examples' implicit usings cover the common namespaces; a widget in another one
            // (Focus, LiquidGlass, …) brings its own, so the sample reads like real code.
            string header = string.Concat(usings.Where(n => !Compiler.Usings.Contains($"using {n};")).Select(n => $"using {n};\n"));
            string src = header + (header.Length > 0 ? "\n" : "") + $$"""
                class Demo : ComposedWidget
                {
                    protected override Widget Build(BuildContext context) =>
                        new Center({{expr}});
                }
                """;
            return new SampleSource(src, 200, true);
        }

        return null;
    }

    private static readonly HashSet<string> VisibleContent =
        ["label", "title", "text", "heading", "message", "subtitle", "child", "content", "placeholder", "data",
         // A control with no callback renders disabled; a no-op handler shows it as it really looks.
         "onPressed", "onTap", "onChanged", "onSelected", "onActive"];

    private static string? Literal(Type t)
    {
        if (t == typeof(string)) return "\"Sample\"";
        if (t == typeof(object)) return "\"tag\"";
        if (t == typeof(bool)) return "true";
        if (t == typeof(int) || t == typeof(long)) return "3";
        if (t == typeof(float) || t == typeof(double)) return "120";
        if (t == typeof(Color)) return "new Color(0xFF3584E4)";
        if (t == typeof(Widget)) return "new Label(\"Child\", fontSize: 16)";
        if (t == typeof(Action)) return "() => { }";
        if (t.IsEnum) return $"{Name(t)}.{Enum.GetNames(t)[0]}";
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Action<>)) return "_ => { }";
        // "Can a list of widgets be passed here" — not "is it some IEnumerable": covariance makes an
        // IEnumerable<AdwNavigationPage> look like IEnumerable<Widget> from the other side.
        if (t.IsAssignableFrom(typeof(List<Widget>)))
            return "[new Label(\"One\", fontSize: 16), new Label(\"Two\", fontSize: 16), new Label(\"Three\", fontSize: 16)]";
        if (t == typeof(IReadOnlyList<string>) || t == typeof(string[]) || t == typeof(List<string>))
            return "[\"One\", \"Two\", \"Three\"]";
        if (Nullable.GetUnderlyingType(t) is { } inner) return Literal(inner);
        return null;
    }

    // ── C#-flavoured type names ───────────────────────────────────────────────


    public static string Name(Type t)
    {
        if (Aliases.TryGetValue(t, out string? alias)) return alias;
        if (Nullable.GetUnderlyingType(t) is { } inner) return Name(inner) + "?";
        if (t.IsArray) return Name(t.GetElementType()!) + "[]";
        if (t.IsByRef) return Name(t.GetElementType()!);
        if (!t.IsGenericType) return t.Name;
        // A type nested in a generic one is generic too but has no `N suffix of its own.
        int tick = t.Name.IndexOf('`');
        if (tick < 0) return t.Name;
        string baseName = t.Name[..tick];
        return $"{baseName}<{string.Join(", ", t.GetGenericArguments().Select(Name))}>";
    }

    private static string Signature(ParameterInfo p)
    {
        string s = $"{Name(p.ParameterType)} {p.Name}";
        if (!p.IsOptional) return s;
        string d = p.DefaultValue switch {
            null or DBNull => "null",
            string str => $"\"{str}\"",
            bool b => b ? "true" : "false",
            float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture) + "f",
            double dd => dd.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Enum e => $"{e.GetType().Name}.{e}",
            var o => o.ToString() ?? "default",
        };
        return $"{s} = {d}";
    }
}
