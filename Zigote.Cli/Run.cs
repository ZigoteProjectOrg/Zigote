using System.Diagnostics;

namespace Zigote.Cli;

/// <summary>
///     <c>zigote run</c> — the one door. Desktop, a phone, or a single widget, chosen by what is
///     plugged in rather than by which of three commands you remembered.
///     <para>
///         There is no new capability here: the Android path is <see cref="Device.Launch" /> and the
///         widget path is <see cref="Preview" />, both unchanged. What is new is that the *choice*
///         is made by the tool. The rule is the one every framework that got this right uses:
///         exactly one candidate → use it silently; more than one → ask; none → desktop. Never
///         guess between two, because deploying to the wrong phone is a silent failure.
///     </para>
/// </summary>
public static class Run
{
    public static int Execute(RunVerb options)
    {
        string? adb = Device.TryFindAdb();
        var ready = adb is null ? [] : Device.ReadyDevices(adb);
        ready.AddRange(Ios.Simulators());
        var device = Resolve(spec: options.DeviceSpec, ready: ready);

        if (device is null) return Desktop(options);

        if (options.Widget is not null)
        {
            throw new CliError(
                "--widget previews on desktop only. Drop --device, or pass --device desktop."
            );
        }

        if (device.Value.Kind == Device.Kind.IosSimulator)
        {
            return Ios.Launch(
                simulator: device.Value,
                directory: options.Directory,
                release: options.Release
            );
        }

        return Device.Launch(
            device: device.Value,
            options: new Device.LaunchSpec(
                Directory: options.Directory,
                Release: options.Release,
                NoReload: options.NoReload,
                Debug: options.Debug,
                DebugPort: options.DebugPort
            ),
            adb: adb!
        );
    }

    /// <summary>The app on this machine — with <c>dotnet watch</c>, so a saved edit reloads.</summary>
    private static int Desktop(RunVerb options)
    {
        string project = Projects.App(options.Directory);
        if (options.Widget is not null)
        {
            return Preview.Launch(
                project: project,
                target: options.Widget,
                noWatch: options.NoReload || options.Release
            );
        }

        // Release gives up reload for JIT'd frames — the same trade `device run --release` makes.
        bool reload = !options.NoReload && !options.Release;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        if (reload)
        {
            start.ArgumentList.Add("watch");
            start.ArgumentList.Add("--non-interactive");
        }

        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(options.Release ? "Release" : "Debug");
        if (options.Inspect)
        {
            // 0 asks the OS for a free port, which the app announces on stdout — the same protocol
            // the Rider plugin and the MCP server already speak. Nothing new is being built here:
            // the server is in the app, and it was simply unreachable from the CLI.
            start.Environment["ZIGOTE_INSPECT"] = "0";
        }

        Console.WriteLine($"  desktop  →  {Path.GetFileNameWithoutExtension(project)}");
        if (options.Inspect)
        {
            Console.WriteLine(
                "  inspect on: the app prints its port as `[inspect] listening on …` — attach the "
            );
            Console.WriteLine(
                "  Rider plugin or the MCP server to it, or Shift+D for the in-app overlay."
            );
        }

        Console.WriteLine(
            reload
                ? "  hot reload on: edit a Build() and save — the tree re-runs in place."
                : "  hot reload off: edits need another `zigote run`."
        );
        Console.WriteLine();

        return Shell.Wait(start);
    }

    /// <summary>
    ///     Which device to run on: null means this machine. <paramref name="spec" /> matches a
    ///     serial or a model, exactly first and then by substring, so `--device pixel` works
    ///     without anyone reading a serial off a sticker.
    /// </summary>
    internal static Device.Attached? Resolve(string? spec, List<Device.Attached> ready)
    {
        if (string.Equals(
                a: spec,
                b: "desktop",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ))
            return null;

        if (spec is not null)
        {
            var matches = ready
                .Where(d => d.Serial.Equals(
                        value: spec,
                        comparisonType: StringComparison.OrdinalIgnoreCase
                    )
                )
                .ToList();
            if (matches.Count == 0)
            {
                matches = [
                    .. ready
                        .Where(d => d.Serial.Contains(
                                        value: spec,
                                        comparisonType: StringComparison.OrdinalIgnoreCase
                                    )
                                    || d.Model.Contains(
                                        value: spec,
                                        comparisonType: StringComparison.OrdinalIgnoreCase
                                    )
                        ),
                ];
            }

            return matches.Count switch {
                1 => matches[0],
                0 => throw new CliError(
                    $"no attached device matches '{spec}'. `zigote run` with nothing plugged in " +
                    "means desktop; `--device desktop` says so explicitly."
                ),
                _ => throw new CliError(
                    $"'{spec}' matches more than one device: " +
                    string.Join(separator: ", ", values: matches.Select(d => d.Serial)) +
                    ". Name one exactly."
                ),
            };
        }

        return ready.Count switch {
            1 => ready[0],
            0 => null, // nothing plugged in — desktop is the only thing left to mean
            _ => Ask(ready),
        };
    }

    /// <summary>
    ///     More than one candidate, so the tool asks instead of guessing. Scripts and IDEs get an
    ///     error naming the flag rather than a prompt nobody is there to answer.
    /// </summary>
    private static Device.Attached? Ask(List<Device.Attached> ready)
    {
        if (Console.IsInputRedirected)
        {
            throw new CliError(
                "more than one device attached: " +
                string.Join(separator: ", ", values: ready.Select(d => d.Serial)) +
                ". Pick one with --device (or --device desktop)."
            );
        }

        Console.WriteLine("More than one place to run this:");
        for (int i = 0; i < ready.Count; i++) Console.WriteLine($"  [{i + 1}] {ready[i]}");
        Console.WriteLine($"  [{ready.Count + 1}] desktop");
        Console.Write($"Which one? [1-{ready.Count + 1}] ");

        string? answer = Console.ReadLine()?.Trim();
        if (!int.TryParse(s: answer, result: out int choice) || choice < 1 ||
            choice > ready.Count + 1)
            throw new CliError($"'{answer}' is not one of the choices.");
        return choice == ready.Count + 1 ? null : ready[choice - 1];
    }
}
