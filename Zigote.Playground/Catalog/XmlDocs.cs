using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Playground.Catalog;

/// <summary>
///     The XML documentation files that ship next to the Zigote assemblies, indexed by member ID
///     (<c>T:</c>, <c>P:</c>, <c>M:</c>, <c>E:</c>) and rendered to the Markdown subset the
///     playground's page renderer understands.
/// </summary>
internal static class XmlDocs
{
    private static readonly Dictionary<string, XElement> Members = [];
    private static readonly HashSet<Assembly> Loaded = [];

    /// <summary>Index one assembly's <c>.xml</c> (next to its DLL). Missing files are fine.</summary>
    public static void Load(Assembly assembly)
    {
        if (!Loaded.Add(assembly)) return;
        string path = Path.ChangeExtension(assembly.Location, ".xml");
        if (!File.Exists(path)) return;
        foreach (var member in XDocument.Load(path).Descendants("member"))
            if (member.Attribute("name")?.Value is { } name) Members[name] = member;
    }

    public static XElement? Type(Type t) => Find("T:" + TypeId(t));

    public static XElement? Member(MemberInfo m) => m switch {
        PropertyInfo p => Find($"P:{TypeId(p.DeclaringType!)}.{p.Name}") ?? Inherited(p),
        EventInfo e => Find($"E:{TypeId(e.DeclaringType!)}.{e.Name}"),
        FieldInfo f => Find($"F:{TypeId(f.DeclaringType!)}.{f.Name}"),
        ConstructorInfo c => Find($"M:{TypeId(c.DeclaringType!)}.#ctor{Params(c)}"),
        MethodInfo mi => Find($"M:{TypeId(mi.DeclaringType!)}.{mi.Name}{Generic(mi)}{Params(mi)}"),
        _ => null,
    };

    private static XElement? Find(string id) => Members.GetValueOrDefault(id);

    // An override documented with <inheritdoc/> (or not at all) borrows its base's comment.
    private static XElement? Inherited(PropertyInfo p)
    {
        var baseType = p.DeclaringType?.BaseType;
        var baseProp = baseType?.GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance);
        return baseProp is not null && baseProp.DeclaringType != p.DeclaringType ? Member(baseProp) : null;
    }

    // ── Member IDs (the C# compiler's documentation-comment ID format) ─────────

    private static string TypeId(Type t) => (t.FullName ?? t.Name).Replace('+', '.');

    private static string Generic(MethodInfo m) =>
        m.IsGenericMethodDefinition ? $"``{m.GetGenericArguments().Length}" : "";

    private static string Params(MethodBase m)
    {
        var ps = m.GetParameters();
        return ps.Length == 0 ? "" : "(" + string.Join(",", ps.Select(p => ParamId(p.ParameterType))) + ")";
    }

    private static string ParamId(Type t)
    {
        if (t.IsByRef) return ParamId(t.GetElementType()!) + "@";
        if (t.IsArray) return ParamId(t.GetElementType()!) + "[]";
        if (t.IsGenericParameter)
            return t.DeclaringMethod is not null ? $"``{t.GenericParameterPosition}" : $"`{t.GenericParameterPosition}";
        if (t.IsGenericType)
        {
            string name = (t.GetGenericTypeDefinition().FullName ?? t.Name).Replace('+', '.');
            name = name[..name.IndexOf('`')];
            return $"{name}{{{string.Join(",", t.GetGenericArguments().Select(ParamId))}}}";
        }

        return TypeId(t);
    }

    // ── XML → Markdown ────────────────────────────────────────────────────────

    /// <summary>The <c>&lt;summary&gt;</c> (and <c>&lt;remarks&gt;</c>) as Markdown paragraphs.</summary>
    public static string Summary(XElement? doc, bool withRemarks = false)
    {
        if (doc is null) return "";
        var sb = new StringBuilder(Render(doc.Element("summary")));
        if (withRemarks && doc.Element("remarks") is { } remarks) sb.Append("\n\n").Append(Render(remarks));
        return sb.ToString().Trim();
    }

    /// <summary>The first sentence of the summary, on one line — for table cells and lists.</summary>
    public static string Brief(XElement? doc)
    {
        string text = Summary(doc);
        int para = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (para > 0) text = text[..para];
        text = Regex.Replace(text, @"\s+", " ").Replace("|", "/").Trim();
        if (text.Length <= 220) return text;
        // Cut at a word, and never inside a [label](target) link or a `code` run.
        string cut = text[..217];
        int open = cut.LastIndexOf('[');
        if (open >= 0 && cut.IndexOf(')', open) < 0) cut = cut[..open];
        if (cut.Count(c => c == '`') % 2 == 1) cut = cut[..cut.LastIndexOf('`')];
        int space = cut.LastIndexOf(' ');
        return (space > 150 ? cut[..space] : cut).TrimEnd() + "…";
    }

    public static string Param(XElement? doc, string name) =>
        doc?.Elements("param").FirstOrDefault(p => p.Attribute("name")?.Value == name) is { } p
            ? Regex.Replace(Render(p), @"\s+", " ").Trim()
            : "";

    private static string Render(XElement? element)
    {
        if (element is null) return "";
        var sb = new StringBuilder();
        foreach (var node in element.Nodes()) Append(sb, node);
        // Collapse the source indentation inside paragraphs; keep paragraph and list breaks.
        var paragraphs = Regex.Split(sb.ToString(), @"\n[ \t]*\n");
        return string.Join("\n\n", paragraphs
            .Select(p => p.StartsWith("```") || p.TrimStart().StartsWith("- ")
                ? p.Trim()
                : Regex.Replace(p, @"\s+", " ").Trim())
            .Where(p => p.Length > 0));
    }

    private static void Append(StringBuilder sb, XNode node)
    {
        if (node is XText text)
        {
            sb.Append(text.Value);
            return;
        }

        if (node is not XElement e) return;
        switch (e.Name.LocalName)
        {
            case "c":
                sb.Append('`').Append(Regex.Replace(e.Value, @"\s+", " ").Trim()).Append('`');
                break;
            case "code":
                sb.Append("\n\n```csharp\n").Append(Dedent(e.Value)).Append("\n```\n\n");
                break;
            case "see" or "seealso":
                if (e.Attribute("langword")?.Value is { } word) sb.Append('`').Append(word).Append('`');
                else if (e.Value.Length > 0) sb.Append(e.Value);
                else sb.Append(CrefMarkdown(e.Attribute("cref")?.Value ?? e.Attribute("href")?.Value ?? ""));
                break;
            case "paramref" or "typeparamref":
                sb.Append('`').Append(e.Attribute("name")?.Value).Append('`');
                break;
            case "para":
                sb.Append("\n\n");
                foreach (var n in e.Nodes()) Append(sb, n);
                sb.Append("\n\n");
                break;
            case "b" or "strong":
                sb.Append("**").Append(Regex.Replace(e.Value, @"\s+", " ").Trim()).Append("**");
                break;
            case "i" or "em":
                sb.Append('*').Append(Regex.Replace(e.Value, @"\s+", " ").Trim()).Append('*');
                break;
            case "list":
                sb.Append("\n\n");
                foreach (var item in e.Elements("item"))
                {
                    var inner = new StringBuilder();
                    if (item.Element("term") is { } term) inner.Append("**").Append(term.Value.Trim()).Append("** — ");
                    foreach (var n in (item.Element("description") ?? item).Nodes())
                        if (n is not XElement { Name.LocalName: "term" }) Append(inner, n);
                    sb.Append("- ").Append(Regex.Replace(inner.ToString(), @"\s+", " ").Trim()).Append('\n');
                }

                sb.Append('\n');
                break;
            case "br":
                sb.Append(' ');
                break;
            default:
                foreach (var n in e.Nodes()) Append(sb, n);
                break;
        }
    }

    // A reference to a Zigote type (or one of its members) becomes a catalog link to that type's page.
    private static string CrefMarkdown(string cref)
    {
        string name = CrefName(cref);
        string s = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        int paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        string typeName = cref.StartsWith("T:") ? s : s.Contains('.') ? s[..s.LastIndexOf('.')] : s;
        return TypeIndex.Find(typeName) is { } t ? $"[`{name}`](type:{t.FullName})" : $"`{name}`";
    }

    // "T:Zigote.UI.Widgets.Watch" → "Watch"; "M:Zigote.UI.App.Post(System.Action)" → "App.Post".
    private static string CrefName(string cref)
    {
        string s = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        int paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        s = Regex.Replace(s, @"`+\d+", "").Replace("#ctor", "new");
        var parts = s.Split('.');
        return cref.StartsWith("T:") || parts.Length < 2 ? parts[^1] : $"{parts[^2]}.{parts[^1]}";
    }

    private static string Dedent(string code)
    {
        var lines = code.Replace("\r", "").Split('\n').SkipWhile(string.IsNullOrWhiteSpace).Reverse()
            .SkipWhile(string.IsNullOrWhiteSpace).Reverse().ToArray();
        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join('\n', lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()));
    }
}

/// <summary>Every public type in the Zigote assemblies, by full name — the targets catalog links can reach.</summary>
internal static class TypeIndex
{
    private static readonly Lazy<Dictionary<string, Type>> All = new(() =>
    {
        var map = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || asm.GetName().Name is not { } n || !n.StartsWith("Zigote.", StringComparison.Ordinal)) continue;
            // Compiled examples are collectible "ExampleN" assemblies, so the prefix check keeps them out.
            Type[] types;
            try
            {
                types = asm.GetExportedTypes();
            }
            catch
            {
                continue;
            }

            foreach (var t in types)
                if (t.FullName is { } full)
                {
                    map[full] = t;
                    map.TryAdd(full.Replace('+', '.'), t); // doc IDs spell nested types with '.'
                }
        }

        return map;
    });

    public static Type? Find(string fullName) => All.Value.GetValueOrDefault(fullName);

    public static bool IsZigote(Type t) => t.Assembly.GetName().Name?.StartsWith("Zigote.", StringComparison.Ordinal) == true;
}
