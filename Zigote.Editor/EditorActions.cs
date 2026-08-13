using Zigote.Editor.Settings;

namespace Zigote.Editor;

/// <summary>
///     Application-level callbacks the editor shell (menu bar) invokes but does not own —
///     project lifecycle lives in the bootstrapper (Program), which supplies these.
/// </summary>
public sealed class EditorActions
{
    /// <summary>Open the project at the given .zigoteproj path.</summary>
    public required Action<string> OpenProject { get; init; }

    /// <summary>Close the current project and return to the welcome screen.</summary>
    public required Action CloseProject { get; init; }

    /// <summary>Request application exit.</summary>
    public required Action Quit { get; init; }

    /// <summary>Project history (the recent-projects list the File menu shows).</summary>
    public required ProjectHistory History { get; init; }

    /// <summary>Persisted editor settings (console font/behavior the shell reads live).</summary>
    public required EditorSettings Settings { get; init; }

    /// <summary>Open (or raise) the Settings window. Null until the bootstrapper wires it.</summary>
    public Action? OpenSettings { get; init; }
}
