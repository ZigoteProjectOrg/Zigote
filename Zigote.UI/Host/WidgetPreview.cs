using System.Globalization;
using System.Reflection;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.UI.Host;

/// <summary>
///     Runs one widget instead of the app's real <see cref="ZigoteApp.Home" />, so a single widget can
///     be looked at on its own while it is edited.
///     <para>
///         Deliberately editor-agnostic: the whole contract is two environment variables read by
///         <see cref="ZigoteApp.Run" />, so anything that can set an env var and start
///         <c>dotnet watch run</c> — the <c>zigote preview</c> command, the Rider plugin under
///         <c>tools/rider</c>, a VS Code task, a shell — drives it identically. There is no IDE-side
///         protocol and nothing to keep in sync.
///     </para>
///     <list type="bullet">
///         <item>
///             <c>ZIGOTE_PREVIEW=Some.Namespace.MyPage</c> — show that widget. The target is a widget
///             type, or a static method returning a <see cref="Widget" />
///             (<c>Some.Namespace.Previews.Buttons</c>); either may take parameters as long as every
///             one of them has a default, which is what makes them the preview's editable properties.
///         </item>
///         <item>
///             <c>ZIGOTE_PREVIEW=Some.Namespace.MyPage?title=Hello&amp;dense=true</c> — the same, with
///             those properties set. Values are URL-encoded; an unknown or unparseable one falls back
///             to the declared default rather than failing the preview.
///         </item>
///         <item>
///             <c>ZIGOTE_PREVIEW_LIST=1</c> — print every target in the entry assembly, one per line,
///             and exit without opening a window.
///         </item>
///     </list>
///     <para>
///         Live editing comes from the hot-reload bridge that is already there (see
///         <see cref="HotReload" />): run the app under <c>dotnet watch</c> and editing the previewed
///         widget's <c>Build()</c> re-runs it in place.
///     </para>
/// </summary>
public static class WidgetPreview
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly IReadOnlyDictionary<string, string> EmptyValues =
        new Dictionary<string, string>();

    /// <summary>The widget to show, or null when the process is a normal app run.</summary>
    public static string? Target =>
        Environment.GetEnvironmentVariable("ZIGOTE_PREVIEW") is { Length: > 0 } t ? t : null;

    /// <summary>
    ///     Prints the preview targets in the entry assembly and returns true when the caller should
    ///     exit instead of opening a window.
    /// </summary>
    public static bool HandleListRequest()
    {
        if (Environment.GetEnvironmentVariable("ZIGOTE_PREVIEW_LIST") is not { Length: > 0 })
            return false;

        foreach (var target in Descriptors()) Console.Out.WriteLine(Line(target));
        return true;
    }

    /// <summary>
    ///     One target per line, name first: the first token stays exactly what <c>ZIGOTE_PREVIEW</c>
    ///     takes, so <c>zigote preview --list | awk '{print $1}'</c> still works, and everything after
    ///     it is what the annotation and the constructor said.
    /// </summary>
    private static string Line(PreviewTarget target)
    {
        var notes = new List<string>();
        if (target.Label is { Length: > 0 }) notes.Add($"\"{target.Label}\"");
        if (target.Group is { Length: > 0 }) notes.Add($"[{target.Group}]");
        if (target is { Width: > 0, Height: > 0 })
        {
            notes.Add(
                target.Width.ToString("0.##", CultureInfo.InvariantCulture) + "×" +
                target.Height.ToString("0.##", CultureInfo.InvariantCulture)
            );
        }

        if (target.Theme is { Length: > 0 }) notes.Add(target.Theme);
        if (target.Parameters.Count > 0)
        {
            notes.Add(
                "(" + string.Join(
                    separator: ", ",
                    values: target.Parameters.Select(p => $"{p.Name}={p.Value}")
                ) + ")"
            );
        }

        return notes.Count == 0 ? target.Target : $"{target.Target}  {string.Join("  ", notes)}";
    }

    /// <summary>
    ///     Every previewable target in the entry assembly (or in <c>ZIGOTE_PREVIEW_ASSEMBLY</c>), by
    ///     name. The names <see cref="Resolve" /> accepts, for a caller that wants nothing else —
    ///     <see cref="Descriptors" /> is the same list with the metadata attached.
    /// </summary>
    public static IEnumerable<string> Candidates() => Descriptors().Select(d => d.Target);

    /// <summary>
    ///     Every previewable target with what a previewer needs to show it: its
    ///     <see cref="PreviewAttribute" />, if any, and the properties it can be given.
    ///     <para>
    ///         Only the app's own assembly is listed — listing the framework's widgets too would bury the
    ///         handful of screens someone actually wants to look at. Internal types count: pages in a
    ///         single-assembly app are internal far more often than not (<c>Zigote.UI.HelloWorld</c>
    ///         included), and a previewer that ignored them would list nothing for the common case.
    ///     </para>
    ///     <para>
    ///         Annotated targets sort first. In an app with two hundred widget types, the ten someone
    ///         wrote <c>[Preview]</c> on are the answer to "what is there to look at" and everything
    ///         else is the haystack.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<PreviewTarget> Descriptors()
    {
        var assembly = ListedAssembly();
        if (assembly is null) return [];

        // '<' catches the compiler's own types — closures, iterator state machines, <PrivateImplementation>.
        var types = SafeTypes(assembly).Where(t => !t.Name.Contains('<')).ToList();

        var widgets = types
            .Where(t => !t.IsAbstract && typeof(Widget).IsAssignableFrom(t))
            .Select(t => (Type: t, Ctor: Ctor(t)))
            .Where(found => found.Ctor is not null)
            .Select(found => Describe(
                target: found.Type.FullName!,
                preview: found.Type.GetCustomAttribute<PreviewAttribute>(),
                parameters: found.Ctor!.GetParameters()
            ));

        // Grouped by name, not one entry per overload: the list has to describe the overload
        // [Resolve] would actually call, or the property editor draws knobs for a method nothing runs.
        var factories = types
            .SelectMany(t => t.GetMethods(Any | BindingFlags.Static))
            .Where(m => !m.Name.Contains('<') && typeof(Widget).IsAssignableFrom(m.ReturnType))
            .GroupBy(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .Select(overloads => (Target: overloads.Key, Method: Pick(overloads)))
            .Where(found => found.Method is not null)
            .Select(found => Describe(
                target: found.Target,
                preview: found.Method!.GetCustomAttribute<PreviewAttribute>(),
                parameters: found.Method.GetParameters()
            ));

        return widgets.Concat(factories)
            .DistinctBy(d => d.Target)
            .OrderByDescending(d => d.Annotated)
            .ThenBy(keySelector: d => d.Group ?? "", comparer: StringComparer.Ordinal)
            .ThenBy(keySelector: d => d.Target, comparer: StringComparer.Ordinal)
            .ToList();
    }

    private static PreviewTarget Describe(
        string target,
        PreviewAttribute? preview,
        ParameterInfo[] parameters
    )
    {
        return new PreviewTarget(
            Target: target,
            Label: preview?.Name,
            Group: preview?.Group,
            Width: preview?.Width ?? 0,
            Height: preview?.Height ?? 0,
            Theme: preview?.Theme,
            Annotated: preview is not null,
            Parameters: parameters.Select(Describe).OfType<PreviewParam>().ToList()
        );
    }

    /// <summary>The parameter as an editable property, or null when its type has no editor.</summary>
    private static PreviewParam? Describe(ParameterInfo parameter)
    {
        if (parameter.Name is not { } name || Kind(parameter.ParameterType) is not { } kind)
            return null;

        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        return new PreviewParam(
            Name: name,
            Kind: kind,
            Value: Text(parameter.DefaultValue),
            Options: type.IsEnum ? Enum.GetNames(type) : []
        );
    }

    private static ConstructorInfo? Ctor(Type type) => Pick(
        options: type.GetConstructors(Any | BindingFlags.Instance),
        annotated: type.GetCustomAttribute<PreviewAttribute>() is not null
    );

    /// <summary>
    ///     Which overload a preview calls.
    ///     <para>
    ///         The one with the most knobs when the author asked for a preview — that is what
    ///         <see cref="PreviewAttribute" /> opts into. Otherwise the argument-less one where there is
    ///         one, because that is the only member this ever called before properties existed, and a
    ///         type declaring both <c>()</c> and <c>(int n = 0)</c> may well mean different things by
    ///         them. A widget with only defaulted parameters gains a preview either way; none loses the
    ///         one it had.
    ///     </para>
    /// </summary>
    private static T? Pick<T>(IEnumerable<T> options, bool annotated = false) where T : MethodBase
    {
        var usable = options.Where(m => Previewable(m.GetParameters())).ToList();
        if (!annotated && !usable.Any(m => m.GetCustomAttribute<PreviewAttribute>() is not null))
        {
            if (usable.FirstOrDefault(m => m.GetParameters().Length == 0) is { } plain) return plain;
        }

        return usable.MaxBy(m => m.GetParameters().Length);
    }

    /// <summary>
    ///     Callable with no arguments at all — which is what makes a target previewable. A parameter
    ///     whose type has no editor is fine as long as it has a default; it simply is not a knob.
    /// </summary>
    private static bool Previewable(ParameterInfo[] parameters) =>
        parameters.All(p => p.HasDefaultValue && !p.IsOut && !p.ParameterType.IsByRef);

    /// <summary>
    ///     The widget named by <paramref name="spec" />, or a widget describing why it could not be
    ///     produced. Never throws: under <c>dotnet watch</c> a thrown resolve kills the loop, whereas a
    ///     message on screen survives until the next edit fixes it.
    ///     <para>
    ///         <paramref name="spec" /> is a target name, optionally followed by property values —
    ///         <c>My.App.Card?title=Espresso&amp;sale=true</c>. See <see cref="Split" />.
    ///     </para>
    /// </summary>
    public static Widget Resolve(string spec)
    {
        (string target, var values) = Split(spec);
        try
        {
            if (FindType(target) is { } type)
            {
                if (!typeof(Widget).IsAssignableFrom(type))
                    return Message($"'{target}' is not a Widget.");
                if (type.IsAbstract || Ctor(type) is not { } ctor)
                {
                    return Message(
                        $"'{target}' has no constructor a preview can call — every parameter needs a default."
                    );
                }

                return (Widget)ctor.Invoke(Bind(parameters: ctor.GetParameters(), values: values));
            }

            // Not a type — the last segment may be a static factory method on the type before it.
            int split = target.LastIndexOf('.');
            if (split > 0 && FindType(target[..split]) is { } owner)
            {
                var method = Factory(owner: owner, name: target[(split + 1)..]);
                if (method is not null)
                {
                    return (Widget)method.Invoke(
                        obj: null,
                        parameters: Bind(parameters: method.GetParameters(), values: values)
                    )!;
                }
            }

            return Message($"No widget type or static factory named '{target}'.");
        }
        catch (Exception e)
        {
            // TargetInvocationException from a factory that threw is the interesting case: show the
            // inner message, which is the one the author's code produced.
            return Message(
                $"{target} failed to construct:\n{(e as TargetInvocationException)?.InnerException ?? e}"
            );
        }
    }

    /// <summary>
    ///     A preview spec split into its target and its property values:
    ///     <c>My.App.Card?title=Espresso&amp;sale=true</c> → <c>My.App.Card</c> and two values.
    ///     <para>
    ///         A query string rather than JSON because every consumer of this contract already has one —
    ///         it survives an environment variable, a shell command and a line on a socket unchanged, and
    ///         needs no parser on either side. Values are URL-encoded, so <c>+</c> and <c>%20</c> are both
    ///         a space and a literal <c>&amp;</c> travels as <c>%26</c>.
    ///     </para>
    /// </summary>
    public static (string Target, IReadOnlyDictionary<string, string> Values) Split(string spec)
    {
        int query = spec.IndexOf('?');
        if (query < 0) return (spec, EmptyValues);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in spec[(query + 1)..]
                     .Split(separator: '&', options: StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            values[Uri.UnescapeDataString(pair[..equals])] =
                Uri.UnescapeDataString(pair[(equals + 1)..].Replace(oldChar: '+', newChar: ' '));
        }

        return (spec[..query], values);
    }

    /// <summary>
    ///     The arguments to call a target with: whatever was asked for, and the declared default
    ///     everywhere else. A value that will not convert falls back to the default rather than failing
    ///     the preview — the value is being typed, and half of "412" is "4".
    /// </summary>
    private static object?[] Bind(
        ParameterInfo[] parameters,
        IReadOnlyDictionary<string, string> values
    )
    {
        return parameters.Select(p =>
            p.Name is { } name && values.TryGetValue(key: name, value: out string? raw) &&
            TryConvert(type: p.ParameterType, raw: raw, value: out object? converted)
                ? converted
                : p.DefaultValue
        ).ToArray();
    }

    private static MethodInfo? Factory(Type owner, string name) => Pick(
        owner.GetMethods(Any | BindingFlags.Static)
            .Where(m => m.Name == name && typeof(Widget).IsAssignableFrom(m.ReturnType))
    );

    /// <summary>The editor a previewer should offer for a parameter, or null for a type with none.</summary>
    private static string? Kind(Type declared)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "bool";
        if (type.IsEnum) return "enum";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return "number";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) ||
            type == typeof(byte))
            return "int";
        return null;
    }

    private static bool TryConvert(Type type, string raw, out object? value)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        value = null;
        if (target == typeof(string))
        {
            value = raw;
            return true;
        }

        if (target.IsEnum)
        {
            bool parsed = Enum.TryParse(
                enumType: target,
                value: raw,
                ignoreCase: true,
                result: out object? name
            );
            value = name;
            return parsed;
        }

        try
        {
            // Invariant: the panel sending "3.5" must not depend on the app's locale to mean 3½.
            value = Convert.ChangeType(
                value: raw,
                conversionType: target,
                provider: CultureInfo.InvariantCulture
            );
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>A default value as the string that would convert back to it.</summary>
    private static string Text(object? value) => value switch
    {
        null => "",
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(format: null, formatProvider: CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>
    ///     The widget to show after <paramref name="target" /> threw out of a frame — from its
    ///     <c>Build()</c>, its layout or its paint, all of which run long after
    ///     <see cref="Resolve" /> handed the instance over.
    ///     <para>
    ///         Most of what this reports is a widget that needs an ancestor its app supplies (a host
    ///         <c>InheritedWidget</c>, a provider, a controller) and that a preview showing it alone does
    ///         not. That is not fixable here — but it has to read as a message, not as a dead process.
    ///     </para>
    /// </summary>
    public static Widget Failure(string target, Exception error)
    {
        Console.Error.WriteLine($"zigote preview: {target} threw — {error.Message}");
        return Message($"{target} threw:\n{error.Message}");
    }

    private static Widget Message(string text) => new Center(child: new Text(text));

    private static Assembly? ListedAssembly()
    {
        string? name = Environment.GetEnvironmentVariable("ZIGOTE_PREVIEW_ASSEMBLY");
        if (name is not { Length: > 0 }) return Assembly.GetEntryAssembly();
        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name)
               ?? LoadNeighbours().FirstOrDefault(a => a.GetName().Name == name);
    }

    /// <summary>
    ///     A type by full name, searching what is loaded first and only then paying to load the rest of
    ///     the output directory — the target usually lives in the entry assembly, which is already in.
    /// </summary>
    private static Type? FindType(string fullName)
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(name: fullName, throwOnError: false))
            .FirstOrDefault(t => t is not null);
        if (loaded is not null) return loaded;

        return LoadNeighbours().Select(a => a.GetType(name: fullName, throwOnError: false))
            .FirstOrDefault(t => t is not null);
    }

    /// <summary>
    ///     Assemblies sitting next to the entry assembly that are not loaded yet. A widget can live in a
    ///     referenced project whose types nothing has touched, so it has no assembly in the domain.
    /// </summary>
    private static IEnumerable<Assembly> LoadNeighbours()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string path in Directory.EnumerateFiles(
                     path: AppContext.BaseDirectory,
                     searchPattern: "*.dll"
                 ))
        {
            if (loaded.Contains(Path.GetFileNameWithoutExtension(path))) continue;
            Assembly? assembly = null;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch
            {
                // Native library, a resource-only satellite, or a mismatched architecture — skip it.
            }

            if (assembly is not null) yield return assembly;
        }
    }

    // A half-loadable assembly still yields the types that did load, which is what a preview needs.
    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }
}

/// <summary>
///     One preview target as a previewer sees it: what to call it, how to show it, and what can be
///     changed about it without editing the file.
/// </summary>
/// <param name="Target">The name <see cref="WidgetPreview.Resolve" /> takes.</param>
/// <param name="Label">The <see cref="PreviewAttribute.Name" />, or null to use the type name.</param>
/// <param name="Group">The <see cref="PreviewAttribute.Group" />, or null.</param>
/// <param name="Width">Layout width in points to show it at; 0 to leave the size alone.</param>
/// <param name="Height">Layout height in points to show it at; 0 to leave the size alone.</param>
/// <param name="Theme"><c>dark</c>/<c>light</c> if it asked for one.</param>
/// <param name="Annotated">Whether it carries a <see cref="PreviewAttribute" /> at all.</param>
/// <param name="Parameters">Its editable properties, in declaration order.</param>
public sealed record PreviewTarget(
    string Target,
    string? Label,
    string? Group,
    float Width,
    float Height,
    string? Theme,
    bool Annotated,
    IReadOnlyList<PreviewParam> Parameters
);

/// <summary>
///     One editable property of a preview: a defaulted constructor or factory parameter.
///     <para>
///         <paramref name="Kind" /> is what to draw — <c>string</c>, <c>bool</c>, <c>int</c>,
///         <c>number</c> or <c>enum</c> — rather than the .NET type name, so a previewer picks a control
///         without a table of type names in it.
///     </para>
/// </summary>
public sealed record PreviewParam(
    string Name,
    string Kind,
    string Value,
    IReadOnlyList<string> Options
);
