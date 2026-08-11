using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Zigote.Generators;

/// <summary>
///     Compile-safe localizations (a <c>gen_l10n</c>-style resource generator). Ingests the project's ARB
///     catalog files (<c>AdditionalFiles</c> ending in <c>.arb</c>) and emits one strongly-typed
///     resource class: a property per parameterless message and a method per parameterized one, with
///     parameter types inferred from the ICU placeholders (<c>plural</c>/<c>number → double</c>,
///     <c>date</c>/<c>time → DateTime</c>, <c>select → string</c>, plain → <c>object</c>).
///     <para>
///         The template catalog is the <c>en</c> file (else the first, path-sorted). It may carry two
///         custom global attributes: <c>@@class</c> (generated type name) and <c>@@namespace</c>.
///         Every other catalog is validated against the template — a missing key is a build warning
///         and falls back to the template text at generation time, so the typed accessors are total.
///     </para>
///     <para>
///         The generated class plugs into <c>Zigote.UI.Localizations</c>: register
///         <c>{Class}.Delegate</c> on the <c>LocalizationsScope</c> and read
///         <c>{Class}.Of(context)</c> (registers a rebuild dependency, like <c>context.Tr</c>).
///     </para>
/// </summary>
[Generator]
public sealed class LocalizationsGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor MalformedArb = new(
        "ZGL001",
        "Malformed ARB file",
        "Localization file '{0}' could not be parsed: {1}",
        "Zigote.Localizations",
        DiagnosticSeverity.Error,
        true
    );

    private static readonly DiagnosticDescriptor MissingKey = new(
        "ZGL002",
        "Missing translation",
        "Locale '{0}' is missing key '{1}' — the template ({2}) text is used",
        "Zigote.Localizations",
        DiagnosticSeverity.Warning,
        true
    );

    private static readonly DiagnosticDescriptor UnknownPlaceholder = new(
        "ZGL003",
        "Unknown placeholder",
        "Locale '{0}', key '{1}' references placeholder '{2}' that the template does not declare — it will render unsubstituted",
        "Zigote.Localizations",
        DiagnosticSeverity.Warning,
        true
    );

    private static readonly DiagnosticDescriptor OrphanKey = new(
        "ZGL004",
        "Orphan translation",
        "Locale '{0}' defines key '{1}' that the template does not — no typed accessor is generated for it",
        "Zigote.Localizations",
        DiagnosticSeverity.Warning,
        true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var arbFiles = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".arb", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, ct) =>
                (file.Path.Replace('\\', '/'), file.GetText(ct)?.ToString() ?? string.Empty)
            )
            .Collect();

        context.RegisterSourceOutput(arbFiles, static (spc, files) => Emit(spc, files));
    }

    private static void Emit(SourceProductionContext spc,
        IReadOnlyList<(string Path, string Text)> files)
    {
        if (files.Count == 0) return;

        var catalogs = new List<ArbCatalog>();
        foreach (var (path, text) in files.OrderBy(static f => f.Path, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                catalogs.Add(ArbCatalog.Parse(path, text));
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(
                    Diagnostic.Create(
                        MalformedArb,
                        Location.None,
                        path,
                        ex.Message
                    )
                );
            }
        }

        if (catalogs.Count == 0) return;

        var template = catalogs.FirstOrDefault(static c => c.Locale == "en") ?? catalogs[0];
        var className = template.ClassName ?? "AppLocalizations";
        var ns = template.Namespace ?? "Zigote.Localizations.Generated";

        // Member model from the template: key → (member name, ordered typed args).
        var members = new List<MessageMember>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in template.Messages)
        {
            var member = new MessageMember(pair.Key, MemberName(pair.Key, usedNames));
            IcuArgs.Scan(pair.Value, member.Args);
            members.Add(member);
        }

        // Validate the other catalogs against the template.
        foreach (var catalog in catalogs)
        {
            if (ReferenceEquals(catalog, template)) continue;

            foreach (var member in members)
                if (!catalog.Messages.ContainsKey(member.Key))
                    spc.ReportDiagnostic(
                        Diagnostic.Create(
                            MissingKey,
                            Location.None,
                            catalog.Locale,
                            member.Key,
                            template.Locale
                        )
                    );

            foreach (var pair in catalog.Messages)
            {
                var member = members.Find(m => m.Key == pair.Key);
                if (member is null)
                {
                    spc.ReportDiagnostic(
                        Diagnostic.Create(
                            OrphanKey,
                            Location.None,
                            catalog.Locale,
                            pair.Key
                        )
                    );
                    continue;
                }

                var localeArgs = new List<IcuArg>();
                IcuArgs.Scan(pair.Value, localeArgs);
                foreach (var arg in localeArgs)
                    if (!member.Args.Exists(a => a.Name == arg.Name))
                        spc.ReportDiagnostic(
                            Diagnostic.Create(
                                UnknownPlaceholder,
                                Location.None,
                                catalog.Locale,
                                pair.Key,
                                arg.Name
                            )
                        );
            }
        }

        spc.AddSource(
            $"{className}.g.cs",
            SourceText.From(
                GenerateSource(
                    ns,
                    className,
                    template,
                    catalogs,
                    members
                ),
                Encoding.UTF8
            )
        );
    }

    // ── Source emission ───────────────────────────────────────────────────────

    private static string GenerateSource(
        string ns,
        string className,
        ArbCatalog template,
        IReadOnlyList<ArbCatalog> catalogs,
        IReadOnlyList<MessageMember> members)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine(
            "// Generated by Zigote.Generators.LocalizationsGenerator from the project's .arb files."
        );
        sb.AppendLine("// Do not edit — change the .arb catalogs instead.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Zigote.UI.Localizations;");
        sb.AppendLine("using Zigote.UI.Widgets;");
        sb.AppendLine();
        sb.Append("namespace ").Append(ns).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.Append("///     Strongly-typed localizations generated from ")
            .Append(catalogs.Count)
            .AppendLine(" ARB catalog(s).");
        sb.Append(
                "///     Register <see cref=\"Delegate\" /> on the <c>LocalizationsScope</c> and read "
            )
            .Append("<see cref=\"Of\" /> in <c>Build</c>.").AppendLine();
        sb.AppendLine("/// </summary>");
        sb.Append("public sealed partial class ").AppendLine(className);
        sb.AppendLine("{");

        // ── plumbing ──
        sb.AppendLine("    private readonly LocalizationCatalog _catalog;");
        sb.AppendLine();
        sb.Append("    private ").Append(className)
            .AppendLine("(Locale locale, LocalizationCatalog catalog)");
        sb.AppendLine("    {");
        sb.AppendLine("        Locale = locale;");
        sb.AppendLine("        _catalog = catalog;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>The locale this instance was loaded for.</summary>");
        sb.AppendLine("    public Locale Locale { get; }");
        sb.AppendLine();
        sb.AppendLine(
            "    /// <summary>Every locale a catalog was generated for, template first.</summary>"
        );
        sb.AppendLine(
            "    public static readonly IReadOnlyList<Locale> SupportedLocales = new List<Locale> {"
        );
        sb.Append("        Locale.Parse(\"").Append(template.Locale).AppendLine("\"),");
        foreach (var c in catalogs)
            if (!ReferenceEquals(c, template))
                sb.Append("        Locale.Parse(\"").Append(c.Locale).AppendLine("\"),");
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.Append(
            "    /// <summary>Plug into <c>LocalizationsScope.Delegates</c> to make <see cref=\"Of\" /> available.</summary>"
        ).AppendLine();
        sb.Append("    public static readonly LocalizationsDelegate<").Append(className)
            .AppendLine("> Delegate =");
        sb.AppendLine("        LocalizationsDelegates.Create(static _ => true, Load);");
        sb.AppendLine();
        sb.AppendLine(
            "    /// <summary>The instance in scope (registers a rebuild dependency on the provider).</summary>"
        );
        sb.Append("    public static ").Append(className).AppendLine(" Of(BuildContext context)");
        sb.AppendLine("    {");
        sb.Append("        return Localizations.Of<").Append(className).AppendLine(">(context)");
        sb.AppendLine("            ?? throw new InvalidOperationException(");
        sb.Append("                \"No ").Append(className)
            .Append(" in scope — add ").Append(className)
            .AppendLine(".Delegate to LocalizationsScope.Delegates.\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine(
            "    /// <summary>Load the best catalog for a locale (exact tag, else language, else template).</summary>"
        );
        sb.Append("    public static ").Append(className).AppendLine(" Load(Locale locale)");
        sb.AppendLine("    {");
        sb.Append("        return new ").Append(className)
            .AppendLine("(locale, CatalogFor(locale));");
        sb.AppendLine("    }");
        sb.AppendLine();

        // ── message accessors ──
        foreach (var member in members)
        {
            var templateText = template.Messages[member.Key];
            sb.AppendLine("    /// <summary>");
            sb.Append("    ///     ").AppendLine(XmlEscape(Truncate(templateText, 120)));
            sb.AppendLine("    /// </summary>");
            if (member.Args.Count == 0)
            {
                sb.Append("    public string ").Append(member.Name)
                    .Append(" => _catalog.Translate(")
                    .Append(Quote(member.Key)).Append(") ?? ").Append(Quote(member.Key))
                    .AppendLine(";");
            }
            else
            {
                sb.Append("    public string ").Append(member.Name).Append('(');
                for (var i = 0; i < member.Args.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(member.Args[i].CsType).Append(' ').Append(member.Args[i].ParamName);
                }

                sb.AppendLine(")");
                sb.AppendLine("    {");
                sb.Append("        return _catalog.Translate(").Append(Quote(member.Key));
                foreach (var arg in member.Args)
                    sb.Append(", (").Append(Quote(arg.Name)).Append(", ").Append(arg.ParamName)
                        .Append(')');
                sb.Append(") ?? ").Append(Quote(member.Key)).AppendLine(";");
                sb.AppendLine("    }");
            }

            sb.AppendLine();
        }

        // ── per-locale catalogs (locale text wins; template completes missing keys) ──
        sb.AppendLine("    private static LocalizationCatalog CatalogFor(Locale locale)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (locale.ToBcp47())");
        sb.AppendLine("        {");
        foreach (var c in catalogs)
            sb.Append("            case \"").Append(c.Locale).Append("\": return Catalog")
                .Append(LocaleId(c.Locale)).AppendLine("();");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        switch (locale.Language)");
        sb.AppendLine("        {");
        var seenLanguages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in catalogs)
        {
            var lang = c.Locale.Split('-', '_')[0];
            if (!seenLanguages.Add(lang)) continue;
            sb.Append("            case \"").Append(lang).Append("\": return Catalog")
                .Append(LocaleId(c.Locale)).AppendLine("();");
        }

        sb.AppendLine("        }");
        sb.AppendLine();
        sb.Append("        return Catalog").Append(LocaleId(template.Locale)).AppendLine("();");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var c in catalogs)
        {
            var id = LocaleId(c.Locale);
            sb.Append("    private static LocalizationCatalog? _catalog").Append(id)
                .AppendLine(";");
            sb.Append("    private static LocalizationCatalog Catalog").Append(id).AppendLine("()");
            sb.AppendLine("    {");
            sb.Append("        return _catalog").Append(id)
                .Append(" ??= new LocalizationCatalog(Locale.Parse(")
                .Append(Quote(c.Locale)).AppendLine(")) {");
            foreach (var member in members)
            {
                // Fall back to the template text so every accessor is total in every locale.
                var text = c.Messages.TryGetValue(member.Key, out var localized)
                    ? localized
                    : template.Messages[member.Key];
                sb.Append("            { ").Append(Quote(member.Key)).Append(", ")
                    .Append(Quote(text))
                    .AppendLine(" },");
            }

            sb.AppendLine("        };");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    // ── naming / escaping helpers ─────────────────────────────────────────────

    private static string MemberName(string key, HashSet<string> used)
    {
        var sb = new StringBuilder(key.Length);
        var upper = true;
        foreach (var ch in key)
        {
            if (ch is '.' or '-' or '_' or ' ')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(ch) : ch);
            upper = false;
        }

        if (sb.Length == 0) sb.Append("Message");
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');

        var name = sb.ToString();
        var candidate = name;
        var n = 2;
        while (!used.Add(candidate)) candidate = name + n++;
        return candidate;
    }

    private static string LocaleId(string tag)
    {
        var sb = new StringBuilder(tag.Length);
        var upper = true;
        foreach (var ch in tag)
        {
            if (ch is '-' or '_')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(ch) : ch);
            upper = false;
        }

        return sb.ToString();
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        sb.Append('"');
        foreach (var ch in s)
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }

        sb.Append('"');
        return sb.ToString();
    }

    private static string XmlEscape(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private static string Truncate(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    // ── model ─────────────────────────────────────────────────────────────────

    private sealed class MessageMember(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public List<IcuArg> Args { get; } = [];
    }

    internal readonly struct IcuArg(string name, string csType)
    {
        public string Name { get; } = name;
        public string CsType { get; } = csType;

        /// <summary>The C# parameter name (escaped if it collides with a keyword).</summary>
        public string ParamName => Name switch {
            "string" or "int" or "double" or "object" or "params" or "value" or "base" or "this"
                => "@" + Name,
            _ => Name,
        };
    }
}