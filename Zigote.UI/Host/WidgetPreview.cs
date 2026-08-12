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
///             type with a parameterless constructor, or a static parameterless method returning a
///             <see cref="Widget" /> (<c>Some.Namespace.Previews.Buttons</c>).
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

        foreach (var name in Candidates()) Console.Out.WriteLine(name);
        return true;
    }

    /// <summary>
    ///     Every previewable target in the entry assembly (or in <c>ZIGOTE_PREVIEW_ASSEMBLY</c>), sorted.
    ///     Only the app's own assembly is listed — listing the framework's widgets too would bury the
    ///     handful of screens someone actually wants to look at.
    ///     <para>
    ///         Internal types count. Pages in a single-assembly app are internal far more often than not
    ///         (<c>Zigote.UI.HelloWorld</c> included), and a previewer that ignored them would list
    ///         nothing for the common case.
    ///     </para>
    /// </summary>
    public static IEnumerable<string> Candidates()
    {
        var assembly = ListedAssembly();
        if (assembly is null) return [];

        // '<' catches the compiler's own types — closures, iterator state machines, <PrivateImplementation>.
        var types = SafeTypes(assembly).Where(t => !t.Name.Contains('<')).ToList();

        var widgets = types
            .Where(t => !t.IsAbstract && typeof(Widget).IsAssignableFrom(t))
            .Where(t => Ctor(t) is not null)
            .Select(t => t.FullName!);

        var factories = types
            .SelectMany(t => t.GetMethods(Any | BindingFlags.Static))
            .Where(m => !m.Name.Contains('<'))
            .Where(m => m.GetParameters().Length == 0 && typeof(Widget).IsAssignableFrom(m.ReturnType))
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}");

        return widgets.Concat(factories).Distinct().Order(StringComparer.Ordinal);
    }

    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;

    private static ConstructorInfo? Ctor(Type type)
    {
        return type.GetConstructor(Any | BindingFlags.Instance, Type.EmptyTypes);
    }

    /// <summary>
    ///     The widget named by <paramref name="target" />, or a widget describing why it could not be
    ///     produced. Never throws: under <c>dotnet watch</c> a thrown resolve kills the loop, whereas a
    ///     message on screen survives until the next edit fixes it.
    /// </summary>
    public static Widget Resolve(string target)
    {
        try
        {
            if (FindType(target) is { } type)
            {
                if (!typeof(Widget).IsAssignableFrom(type))
                    return Message($"'{target}' is not a Widget.");
                if (type.IsAbstract || Ctor(type) is not { } ctor)
                    return Message($"'{target}' has no parameterless constructor.");
                return (Widget)ctor.Invoke(null);
            }

            // Not a type — the last segment may be a static factory method on the type before it.
            var split = target.LastIndexOf('.');
            if (split > 0 && FindType(target[..split]) is { } owner)
            {
                var method = owner.GetMethod(
                    target[(split + 1)..],
                    Any | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null
                );
                if (method is not null && typeof(Widget).IsAssignableFrom(method.ReturnType))
                    return (Widget)method.Invoke(null, null)!;
            }

            return Message($"No widget type or static factory named '{target}'.");
        }
        catch (Exception e)
        {
            // TargetInvocationException from a factory that threw is the interesting case: show the
            // inner message, which is the one the author's code produced.
            return Message($"{target} failed to construct:\n{(e as TargetInvocationException)?.InnerException ?? e}");
        }
    }

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

    private static Widget Message(string text)
    {
        return new Center(child: new Text(text));
    }

    private static Assembly? ListedAssembly()
    {
        var name = Environment.GetEnvironmentVariable("ZIGOTE_PREVIEW_ASSEMBLY");
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
            .Select(a => a.GetType(fullName, false))
            .FirstOrDefault(t => t is not null);
        if (loaded is not null) return loaded;

        return LoadNeighbours().Select(a => a.GetType(fullName, false)).FirstOrDefault(t => t is not null);
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

        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
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
