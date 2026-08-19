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

        string target = options.Target
                        ?? throw new CliError(
                            "preview needs a widget: zigote preview <Namespace.Type>. " +
                            "Run `zigote preview --list` to see what this project offers."
                        );

        // `dotnet watch` is what makes this a previewer rather than a runner: Zigote's hot-reload
        // bridge re-runs the previewed widget's Build() on every save, in place.
        var start = Dotnet(
            project: project,
            verb: options.NoWatch ? ["run"] : ["watch", "run", "--non-interactive"]
        );
        start.Environment["ZIGOTE_PREVIEW"] = target;

        Console.WriteLine(
            $"  previewing {target}{(options.NoWatch ? "" : "  (edit and save to reload)")}"
        );
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

    private static ProcessStartInfo Dotnet(string project, string[] verb)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (string a in verb) start.ArgumentList.Add(a);
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        return start;
    }

    private static int Wait(ProcessStartInfo start)
    {
        using var process = Process.Start(start)
                            ?? throw new CliError(
                                "could not start dotnet — is the .NET SDK on PATH?"
                            );
        process.WaitForExit();
        return process.ExitCode;
    }
}
