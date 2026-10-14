using System.Diagnostics;

namespace Zigote.Cli;

/// <summary>
///     <c>zigote preview</c> — run one widget of an app on its own, live.
///     <para>
///         This is a launcher and nothing else. The preview itself lives in
///         <c>Zigote.UI.Host.WidgetPreview</c> and is driven by two environment variables, so an
///         editor
///         that would rather start the process itself needs no cooperation from this command — that is
///         the point of the split, and why the Rider plugin under <c>tools/rider</c> is a thin caller
///         rather than the implementation.
///     </para>
/// </summary>
public static class Preview
{
    public static int Run(PreviewVerb options, string project)
    {
        if (options.ListTargets) return List(project);

        return Launch(
            project: project,
            target: options.Target
                    ?? throw new CliError(
                        "preview needs a widget: zigote run --widget <Namespace.Type>. " +
                        "Run `zigote preview --list` to see what this project offers."
                    ),
            noWatch: options.NoWatch
        );
    }

    /// <summary>Start one widget. Shared with <c>zigote run --widget</c>, which is the same thing.</summary>
    internal static int Launch(string project, string target, bool noWatch)
    {
        // `dotnet watch` is what makes this a previewer rather than a runner: Zigote's hot-reload
        // bridge re-runs the previewed widget's Build() on every save, in place.
        var start = Dotnet(
            project: project,
            verb: noWatch ? ["run"] : ["watch", "run", "--non-interactive"]
        );
        start.Environment["ZIGOTE_PREVIEW"] = target;

        Console.WriteLine($"  previewing {target}{(noWatch ? "" : "  (edit and save to reload)")}");
        return Wait(start);
    }

    private static int List(string project)
    {
        // -v q --nologo keeps the build silent so stdout is the target list and nothing else, which is
        // what a caller populating a menu wants to read.
        var start = Dotnet(project: project, verb: ["run", "-v", "q", "--nologo"]);
        start.Environment["ZIGOTE_PREVIEW_LIST"] = "1";
        return Wait(start);
    }

    private static ProcessStartInfo Dotnet(string project, string[] verb) =>
        Shell.Command(file: "dotnet", arguments: [.. verb, "--project", project]);

    private static int Wait(ProcessStartInfo start) => Shell.Wait(start);
}
