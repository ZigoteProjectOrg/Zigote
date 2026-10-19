using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Playground;

/// <summary>
///     What one compile produced: a widget factory (or null), the loaded assembly, and the
///     diagnostics — as display lines and as editor markers.
/// </summary>
internal sealed record CompileResult(Func<Widget>? Create, Assembly? Assembly, string[] Errors, IReadOnlyList<CodeMarker> Markers)
{
    public CompileResult(Func<Widget>? create, Assembly? assembly, string[] errors) : this(create, assembly, errors, []) { }
}

/// <summary>
///     Compiles an example in-process with Roslyn and loads it into its own collectible context, so
///     an edited example replaces the old one without restarting the app. An example is a C# file
///     with a <c>Demo</c> class — any <see cref="Widget" /> with a parameterless constructor. The same
///     compilation answers the editor's completion requests.
/// </summary>
internal static class Compiler
{
    // The same namespaces the playground itself imports, so examples read like app code.
    public const string Usings =
        """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Zigote.Core;
        global using Zigote.Core.Animation;
        global using Zigote.Core.Engine;
        global using Zigote.Core.Events;
        global using Zigote.Core.Paint;
        global using Zigote.Core.Platform;
        global using Zigote.Core.State;
        global using Zigote.Core.Threading;
        global using Zigote.UI.Adwaita;
        global using Zigote.UI.Localizations;
        global using Zigote.UI.Theme;
        global using Zigote.UI.Widgets;
        global using Zigote.UI.Widgets.Controls;
        global using Zigote.UI.Widgets.Layout;
        global using Zigote.UI.Widgets.Navigation;
        global using Zigote.UI.Widgets.Transitions;
        """;

    // An example's Console: a type in the example's own global namespace wins over System.Console,
    // so Console.WriteLine in example code lands in that example's console pane.
    private const string ConsoleShim =
        """
        internal static class Console
        {
            private static readonly System.Reflection.Assembly Self = typeof(Console).Assembly;
            public static void WriteLine() => Playground.ExampleConsole.Write(Self, "\n");
            public static void WriteLine(string? value) => Playground.ExampleConsole.Write(Self, value + "\n");
            public static void WriteLine(object? value) => Playground.ExampleConsole.Write(Self, value + "\n");
            public static void WriteLine(string format, params object?[] args) => Playground.ExampleConsole.Write(Self, string.Format(format, args) + "\n");
            public static void Write(string? value) => Playground.ExampleConsole.Write(Self, value ?? "");
            public static void Write(object? value) => Playground.ExampleConsole.Write(Self, value?.ToString() ?? "");
        }
        """;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly SyntaxTree UsingsTree = CSharpSyntaxTree.ParseText(Usings, ParseOptions, path: "GlobalUsings.cs", encoding: System.Text.Encoding.UTF8);
    private static readonly SyntaxTree ShimTree = CSharpSyntaxTree.ParseText(ConsoleShim, ParseOptions, path: "Console.cs", encoding: System.Text.Encoding.UTF8);

    // Every assembly the host can load, BCL and Zigote alike: the trusted-platform list is exactly
    // what the runtime resolved for this app, so an example can reach whatever the playground can.
    // Zigote assemblies get their XML doc files attached, which is what completion's docs read.
    private static readonly Lazy<MetadataReference[]> References = new(() => [
        .. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(p => File.Exists(Path.ChangeExtension(p, ".xml"))
            ? MetadataReference.CreateFromFile(p, documentation: new RoslynXmlDocs(Path.ChangeExtension(p, ".xml")))
            : MetadataReference.CreateFromFile(p)),
    ]);

    private static readonly CSharpCompilationOptions Options = new(
        OutputKind.DynamicallyLinkedLibrary,
        nullableContextOptions: NullableContextOptions.Enable,
        optimizationLevel: OptimizationLevel.Debug
    );

    private static int _counter;

    private static CSharpCompilation Create(SyntaxTree tree) => CSharpCompilation.Create(
        assemblyName: $"Example{Interlocked.Increment(ref _counter)}",
        syntaxTrees: [UsingsTree, ShimTree, tree],
        references: References.Value,
        options: Options
    );

    private static SyntaxTree Parse(string source) =>
        CSharpSyntaxTree.ParseText(source, ParseOptions, path: "Example.cs", encoding: System.Text.Encoding.UTF8);

    public static CompileResult Compile(string source)
    {
        var compilation = Create(Parse(source));

        using var il = new MemoryStream();
        // Embedded PDB: a runtime exception's stack trace then names Example.cs and the line.
        var emit = compilation.Emit(
            il,
            options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
                debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.Embedded
            )
        );

        // Diagnostics in the example's own file, as editor markers. Warnings never block a run.
        var markers = emit.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning && d.Location.SourceTree?.FilePath == "Example.cs")
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return new CodeMarker(
                    span.StartLinePosition.Line, span.StartLinePosition.Character,
                    span.EndLinePosition.Line, span.EndLinePosition.Character,
                    d.Severity == DiagnosticSeverity.Error ? CodeMarkerSeverity.Error : CodeMarkerSeverity.Warning,
                    d.GetMessage());
            })
            .ToArray();

        if (!emit.Success)
        {
            return new CompileResult(null, null, [
                .. emit.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d =>
                    {
                        var at = d.Location.GetLineSpan().StartLinePosition;
                        return $"{at.Line + 1}:{at.Character + 1}  {d.GetMessage()}";
                    }),
            ], markers);
        }

        il.Position = 0;
        // ponytail: one context per compile, never unloaded — a few KB of IL per run; call
        // context.Unload() once the old preview is detached if long sessions ever show it.
        var assembly = new AssemblyLoadContext(name: compilation.AssemblyName, isCollectible: true)
            .LoadFromStream(il);
        var demo = assembly.GetTypes().FirstOrDefault(t => t.Name == "Demo");
        if (demo is null || !typeof(Widget).IsAssignableFrom(demo) ||
            demo.GetConstructor(Type.EmptyTypes) is null)
            return new CompileResult(null, assembly, [
                "Define `class Demo : ComposedWidget` (any Widget with a parameterless constructor).",
            ], markers);

        return new CompileResult(() => (Widget)Activator.CreateInstance(demo)!, assembly, [], markers);
    }

    // ── Completion ────────────────────────────────────────────────────────────

    private static readonly string[] Keywords = [
        "abstract", "as", "async", "await", "base", "bool", "break", "case", "catch", "char", "class", "const",
        "continue", "decimal", "default", "do", "double", "else", "enum", "false", "finally", "float", "for",
        "foreach", "get", "if", "in", "init", "int", "interface", "internal", "is", "long", "new", "null",
        "object", "out", "override", "params", "private", "protected", "public", "readonly", "record", "ref",
        "required", "return", "sealed", "set", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "using", "var", "virtual", "void", "while", "with", "yield",
    ];

    private static readonly SymbolDisplayFormat Signature = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName);

    /// <summary>
    ///     Completion candidates at <paramref name="offset" />: the members of what is left of a
    ///     <c>.</c> (static members of a type, instance members and extensions of a value), otherwise
    ///     everything in scope plus the C# keywords. Runs on a worker; the editor filters the result.
    /// </summary>
    public static Task<IReadOnlyList<CodeCompletion>> CompleteAsync(string source, int offset, CancellationToken ct) =>
        Task.Run<IReadOnlyList<CodeCompletion>>(() => Complete(source, offset, ct), ct);

    public static IReadOnlyList<CodeCompletion> Complete(string source, int offset, CancellationToken ct)
    {
        var tree = Parse(source);
        var model = Create(tree).GetSemanticModel(tree);
        ct.ThrowIfCancellationRequested();

        // The start of the word being typed: that is where the context is decided.
        int start = offset;
        while (start > 0 && (char.IsLetterOrDigit(source[start - 1]) || source[start - 1] == '_')) start--;
        var root = tree.GetRoot(ct);

        IEnumerable<ISymbol> symbols;
        bool members = false;
        if (start > 0 && source[start - 1] == '.')
        {
            members = true;
            var dot = root.FindToken(start - 1);
            ExpressionSyntax? left = dot.Parent switch {
                MemberAccessExpressionSyntax ma => ma.Expression,
                QualifiedNameSyntax qn => qn.Left,
                _ => null,
            };
            if (left is null) return [];
            var bound = model.GetSymbolInfo(left, ct).Symbol;
            if (bound is INamespaceOrTypeSymbol container)
                symbols = bound is INamespaceSymbol ? model.LookupNamespacesAndTypes(start, container) : model.LookupStaticMembers(start, container);
            else if (model.GetTypeInfo(left, ct).Type is { } type)
                symbols = model.LookupSymbols(start, type, includeReducedExtensionMethods: true).Where(s => !s.IsStatic || s is IMethodSymbol { MethodKind: MethodKind.ReducedExtension });
            else return [];
        }
        else
            symbols = model.LookupSymbols(start);

        ct.ThrowIfCancellationRequested();
        var items = symbols
            .Where(s => s.CanBeReferencedByName && !s.IsImplicitlyDeclared && s is not IMethodSymbol { MethodKind: not (MethodKind.Ordinary or MethodKind.ReducedExtension) })
            .Where(s => s.Name != "Console" || s.ContainingAssembly?.Name?.StartsWith("Example") != true) // our shim
            .GroupBy(s => s.Name)
            .Select(g => Item(g.First(), g.Count()))
            .OrderBy(i => Rank(i.Kind, members))
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!members) items.AddRange(Keywords.Select(k => new CodeCompletion(k, CodeCompletionKind.Keyword)));
        return items;
    }

    private static CodeCompletion Item(ISymbol s, int overloads)
    {
        var kind = s switch {
            ILocalSymbol => CodeCompletionKind.Local,
            IParameterSymbol => CodeCompletionKind.Parameter,
            IMethodSymbol => CodeCompletionKind.Method,
            IPropertySymbol => CodeCompletionKind.Property,
            IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => CodeCompletionKind.EnumMember,
            IFieldSymbol => CodeCompletionKind.Field,
            IEventSymbol => CodeCompletionKind.Event,
            INamespaceSymbol => CodeCompletionKind.Namespace,
            INamedTypeSymbol { TypeKind: TypeKind.Enum } => CodeCompletionKind.Enum,
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => CodeCompletionKind.Struct,
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => CodeCompletionKind.Interface,
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } => CodeCompletionKind.Delegate,
            INamedTypeSymbol => CodeCompletionKind.Class,
            _ => CodeCompletionKind.Other,
        };
        string detail = s switch {
            IMethodSymbol m => m.ToDisplayString(Signature) + (overloads > 1 ? $"  (+{overloads - 1})" : ""),
            IPropertySymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IFieldSymbol f => f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ILocalSymbol l => l.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IParameterSymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            INamedTypeSymbol t => t.ContainingNamespace?.ToDisplayString() ?? "",
            _ => "",
        };
        return new CodeCompletion(s.Name, kind, detail, Summary(s));
    }

    // Locals and members first, then types, namespaces last — what is usually meant.
    private static int Rank(CodeCompletionKind k, bool members) => k switch {
        CodeCompletionKind.Local or CodeCompletionKind.Parameter => 0,
        CodeCompletionKind.Property or CodeCompletionKind.Field or CodeCompletionKind.Method or CodeCompletionKind.Event or CodeCompletionKind.EnumMember => members ? 0 : 1,
        CodeCompletionKind.Namespace => 3,
        _ => 2,
    };

    // Keyed by doc ID, not symbol: every compile makes new symbols, but the IDs are stable.
    private static readonly ConcurrentDictionary<string, string?> DocXml = new();

    private static string? Xml(ISymbol s) =>
        s.GetDocumentationCommentId() is not { } id ? null : DocXml.GetOrAdd(id, _ => s.GetDocumentationCommentXml());

    private static string? Summary(ISymbol s) => DocElement(Xml(s), @"<summary>(.*?)</summary>");

    private static string? ParamDoc(IMethodSymbol m, string name) =>
        DocElement(Xml(m), $@"<param name=""{Regex.Escape(name)}"">(.*?)</param>");

    // One element of a doc comment as plain text: crefs shortened to their name, tags dropped.
    private static string? DocElement(string? xml, string pattern)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        var m = Regex.Match(xml, pattern, RegexOptions.Singleline);
        if (!m.Success) return null;
        string text = Regex.Replace(m.Groups[1].Value, @"<see\s+cref=""[A-Z]:([^""]+)""\s*/>", c => c.Groups[1].Value.Split('.')[^1]);
        text = Regex.Replace(text, @"<(?:see|paramref|typeparamref)\s+(?:langword|name)=""([^""]+)""\s*/>", "$1");
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = Regex.Replace(System.Net.WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    // ── Parameter hints ───────────────────────────────────────────────────────

    private static readonly SymbolDisplayFormat ParameterFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                              SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeParamsRefOut);

    public static Task<CodeSignatureHelp?> SignatureHelpAsync(string source, int offset, CancellationToken ct) =>
        Task.Run(() => SignatureHelp(source, offset, ct), ct);

    /// <summary>
    ///     The overloads of the call whose parentheses hold <paramref name="offset" /> — a method call,
    ///     <c>new T(…)</c>, target-typed <c>new(…)</c> or <c>base(…)</c> — with the argument being typed
    ///     marked in each. Null outside any argument list.
    /// </summary>
    public static CodeSignatureHelp? SignatureHelp(string source, int offset, CancellationToken ct)
    {
        var tree = Parse(source);
        var root = tree.GetRoot(ct);
        // The innermost argument list the caret is inside (after its '(' and not past its ')').
        var list = root.FindToken(Math.Max(0, offset - 1)).Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>()
            .FirstOrDefault(l => l.OpenParenToken.SpanStart < offset && (l.CloseParenToken.IsMissing || offset <= l.CloseParenToken.SpanStart));
        if (list?.Parent is not { } call) return null;

        var model = Create(tree).GetSemanticModel(tree);
        IMethodSymbol[] methods = call switch {
            InvocationExpressionSyntax inv => [.. model.GetMemberGroup(inv.Expression, ct).OfType<IMethodSymbol>()],
            BaseObjectCreationExpressionSyntax creation when model.GetTypeInfo(creation, ct).Type is INamedTypeSymbol type =>
                [.. type.InstanceConstructors.Where(c => model.IsAccessible(creation.SpanStart, c))],
            _ => model.GetSymbolInfo(call, ct) is var info && info.Symbol is IMethodSymbol one
                ? [one]
                : [.. info.CandidateSymbols.OfType<IMethodSymbol>()],
        };
        if (methods.Length == 0) return null;
        ct.ThrowIfCancellationRequested();

        int position = list.Arguments.GetSeparators().Count(sep => sep.SpanStart < offset);
        string? named = list.Arguments.FirstOrDefault(a => a.FullSpan.Start <= offset && offset <= a.FullSpan.End)?.NameColon?.Name.Identifier.ValueText;
        var bound = model.GetSymbolInfo(call, ct).Symbol;

        var ordered = methods.OrderBy(m => m.Parameters.Length).ToArray();
        var signatures = ordered.Select(m => BuildSignature(m, ActiveParameter(m, position, named))).ToArray();
        // The overload the compiler picked; else the first that has room for this argument.
        int active = Array.FindIndex(ordered, m => SymbolEqualityComparer.Default.Equals(m, bound));
        if (active < 0) active = Math.Max(0, Array.FindIndex(ordered, m => m.Parameters.Length > position || m.Parameters.LastOrDefault()?.IsParams == true));
        return new CodeSignatureHelp(signatures, active);
    }

    private static int ActiveParameter(IMethodSymbol m, int position, string? named)
    {
        if (named is not null) return m.Parameters.IndexOf(m.Parameters.FirstOrDefault(p => p.Name == named)!);
        if (position < m.Parameters.Length) return position;
        return m.Parameters.LastOrDefault()?.IsParams == true ? m.Parameters.Length - 1 : -1;
    }

    private static CodeSignature BuildSignature(IMethodSymbol m, int active)
    {
        var sb = new System.Text.StringBuilder(m.MethodKind == MethodKind.Constructor
            ? m.ContainingType.Name
            : $"{m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {m.Name}");
        if (m.TypeParameters.Length > 0) sb.Append('<').Append(string.Join(", ", m.TypeParameters.Select(t => t.Name))).Append('>');
        sb.Append('(');
        var spans = new List<(int, int)>();
        foreach (var p in m.Parameters)
        {
            if (spans.Count > 0) sb.Append(", ");
            string text = p.ToDisplayString(ParameterFormat);
            spans.Add((sb.Length, text.Length));
            sb.Append(text);
        }

        sb.Append(')');
        return new CodeSignature(sb.ToString(), spans, Summary(m)) {
            ActiveParameter = active,
            ParameterDocs = [.. m.Parameters.Select(p => ParamDoc(m, p.Name))],
        };
    }
}

/// <summary>An assembly's XML doc file, read on first use — what completion's descriptions come from.</summary>
internal sealed class RoslynXmlDocs(string path) : DocumentationProvider
{
    private readonly Lazy<Dictionary<string, string>> _members = new(() =>
        System.Xml.Linq.XDocument.Load(path).Descendants("member")
            .Where(m => m.Attribute("name") is not null)
            .GroupBy(m => m.Attribute("name")!.Value)
            .ToDictionary(g => g.Key, g => g.First().ToString()));

    protected override string? GetDocumentationForSymbol(string documentationMemberID, System.Globalization.CultureInfo preferredCulture, CancellationToken cancellationToken = default) =>
        _members.Value.GetValueOrDefault(documentationMemberID);

    public override bool Equals(object? obj) => obj is RoslynXmlDocs other && other._path == _path;
    public override int GetHashCode() => _path.GetHashCode();
    private readonly string _path = path;
}

/// <summary>
///     Where an example's <c>Console</c> writes go. Each running example registers a sink for its
///     compiled assembly; writes arrive from any thread (examples run tasks), so sinks must be
///     thread-safe — the example view writes a signal.
/// </summary>
public static class ExampleConsole
{
    private static readonly ConcurrentDictionary<Assembly, Action<string>> Sinks = new();

    internal static void Register(Assembly assembly, Action<string> sink) => Sinks[assembly] = sink;

    internal static void Unregister(Assembly assembly) => Sinks.TryRemove(assembly, out _);

    public static void Write(Assembly assembly, string text)
    {
        if (Sinks.TryGetValue(assembly, out var sink)) sink(text);
    }
}
