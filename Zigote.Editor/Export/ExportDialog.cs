using System.Diagnostics;
using System.Runtime.InteropServices;
using Zigote.Core;
using Zigote.Editor.Scene;
using Zigote.UI.Host;
using Zigote.UI.Theme;
using Zigote.UI.Widgets;
using Zigote.UI.Widgets.Controls;
using Zigote.UI.Widgets.Layout;

namespace Zigote.Editor.Export;

/// <summary>
///     "File → Export Game…": pick platforms + build flavors (one pass can produce JIT and AOT
///     side by side), then a blocking progress view tracks every platform × mode job. While an
///     export runs the dialog cannot be dismissed and a second export cannot be started.
/// </summary>
public static class ExportDialog
{
    private static readonly (string Rid, string Label)[] Platforms = [
        ("osx-arm64", "macOS (Apple Silicon)"),
        ("osx-x64", "macOS (Intel)"),
        ("win-x64", "Windows x64"),
        ("linux-x64", "Linux x64"),
        ("linux-arm64", "Linux ARM64"),
    ];

    private static bool _exporting;

    public static void Show(App app, ThemeData theme, EditorState state)
    {
        if (_exporting)
        {
            app.ShowSnackbar("An export is already running — see the Console panel.");
            return;
        }

        if (state.ProjectPath is null)
        {
            app.ShowSnackbar("Save the project before exporting.");
            return;
        }

        if (state.IsScriptBuilding)
        {
            app.ShowSnackbar("Scripts are still building — try again when the build finishes.");
            return;
        }

        var hostRid = RuntimeInformation.RuntimeIdentifier;
        var platformSelected = Platforms.Select(p => p.Rid == hostRid).ToArray();
        if (!platformSelected.Any(s => s)) platformSelected[0] = true;
        var jitSelected = true;
        var aotSelected = false;

        var outField = new TextField(decoration: new InputDecoration("Output folder")) {
            Text = Path.Combine(Path.GetDirectoryName(state.ProjectPath)!, "export"),
        };
        var validation = new Label("", theme.FontSizeCaption, theme.Error);

        Dialog? dialog = null;
        // Dialog passes a bounded max height and expects content columns to be MainAxisSize.Min —
        // the default (Max) fills the whole screen and starves later children to zero height.
        var rows = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };
        rows.Children.Add(new Label("Export Game", theme.FontSizeTitle, theme.OnSurface));
        rows.Children.Add(new SizedBox(height: 6f));
        rows.Children.Add(
            new Label(
                "Bundles the game (scene, assets, scripts, engine) into distributable builds.",
                theme.FontSizeCaption,
                theme.Hint
            )
        );
        rows.Children.Add(new SizedBox(height: 14f));

        rows.Children.Add(SectionHeader("Platforms", theme));
        for (var i = 0; i < Platforms.Length; i++)
        {
            var idx = i;
            var label = Platforms[idx].Label +
                        (Platforms[idx].Rid == hostRid ? "  — this machine" : "");
            rows.Children.Add(
                CheckRow(
                    new Checkbox(platformSelected[idx], v => platformSelected[idx] = v),
                    label,
                    null,
                    theme
                )
            );
        }

        rows.Children.Add(new SizedBox(height: 12f));
        rows.Children.Add(SectionHeader("Build flavors", theme));
        rows.Children.Add(
            CheckRow(
                new Checkbox(jitSelected, v => jitSelected = v),
                "Self-contained (JIT)",
                "single-file on Windows/Linux; every platform buildable from here",
                theme
            )
        );
        rows.Children.Add(
            CheckRow(
                new Checkbox(aotSelected, v => aotSelected = v),
                "Native AOT",
                $"fastest startup, smallest runtime; only for this machine's OS ({RidOsLabel(hostRid)})",
                theme
            )
        );
        rows.Children.Add(new SizedBox(height: 12f));

        rows.Children.Add(SectionHeader("Output", theme));
        rows.Children.Add(outField);
        rows.Children.Add(new SizedBox(height: 6f));
        rows.Children.Add(validation);
        rows.Children.Add(new SizedBox(height: 10f));

        rows.Children.Add(
            new Row {
                MainAxisAlignment = MainAxisAlignment.End,
                Children = {
                    new Button("Cancel", () => dialog?.Dismiss()) { Style = ButtonStyle.Outlined },
                    new SizedBox(10f),
                    new Button(
                        "Export",
                        () =>
                        {
                            var rids = Platforms.Where((_, i) => platformSelected[i])
                                .Select(p => p.Rid).ToList();
                            var modes = new List<ExportMode>();
                            if (jitSelected) modes.Add(ExportMode.SelfContained);
                            if (aotSelected) modes.Add(ExportMode.NativeAot);

                            if (rids.Count == 0 || modes.Count == 0)
                            {
                                validation.Text = rids.Count == 0
                                    ? "Select at least one platform."
                                    : "Select at least one build flavor.";
                                return;
                            }

                            var input = new ExportInput(
                                state.ProjectPath!,
                                state.Project!,
                                state.ScriptRegistry,
                                state.ScriptDomain.AssemblyPath is { } asm
                                    ? Path.GetFileNameWithoutExtension(asm)
                                    : null
                            );
                            var options = new ExportOptions(
                                Path.GetFullPath(outField.Text.Trim()),
                                rids,
                                modes
                            );

                            dialog?.Dismiss();
                            RunWithProgress(
                                app,
                                theme,
                                input,
                                options
                            );
                        }
                    ) { BackgroundColor = theme.Primary },
                },
            }
        );

        var body = new SizedBox(540f, child: new Padding(EdgeInsets.All(20f), rows));
        dialog = new Dialog(body, app) { Dismissible = true };
        dialog.Show();
    }

    // ── Progress view (blocking) ──────────────────────────────────────────────

    private static void RunWithProgress(App app, ThemeData theme, ExportInput input,
        ExportOptions options)
    {
        _exporting = true;

        // One status row per platform × mode job, updated from the export thread.
        var jobRows = new Dictionary<ExportJob, Label>();
        var rows = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };
        rows.Children.Add(new Label("Exporting…", theme.FontSizeTitle, theme.OnSurface));
        rows.Children.Add(new SizedBox(height: 12f));
        foreach (var rid in options.Rids)
        foreach (var mode in options.Modes)
        {
            var job = new ExportJob(rid, mode);
            var status = new Label("queued", theme.FontSizeCaption, theme.Hint);
            jobRows[job] = status;
            rows.Children.Add(
                new Row {
                    Children = {
                        new Label(
                            $"{PlatformLabel(rid)} · {ModeLabel(mode)}",
                            theme.FontSizeBody,
                            theme.OnSurface
                        ),
                        new Spacer(),
                        status,
                    },
                }
            );
            rows.Children.Add(new SizedBox(height: 6f));
        }

        rows.Children.Add(new SizedBox(height: 8f));
        var logLine = new Label("Starting…", theme.FontSizeCaption, theme.Hint) { MaxLines = 1 };
        rows.Children.Add(logLine);
        rows.Children.Add(new SizedBox(height: 12f));

        var closeButton = new Button("Close", null) {
            Style = ButtonStyle.Outlined,
            Enabled = false,
        };
        var revealButton = new Button("Show in file manager", null) { Enabled = false };
        rows.Children.Add(
            new Row {
                MainAxisAlignment = MainAxisAlignment.End,
                Children = {
                    revealButton,
                    new SizedBox(10f),
                    closeButton,
                },
            }
        );

        var body = new SizedBox(540f, child: new Padding(EdgeInsets.All(20f), rows));
        var progress =
            new Dialog(body, app) { Dismissible = false }; // blocked until the pass finishes
        progress.Show();
        closeButton.OnPressed = () => progress.Dismiss();
        revealButton.OnPressed = () => Reveal(options.OutputDir);

        var log = new LineProgress(line =>
            {
                Console.WriteLine(line); // EditorLog tees this into the Console panel
                logLine.Text = Truncate(line, 90);
            }
        );
        var jobs = new JobProgress(update =>
            {
                if (!jobRows.TryGetValue(update.Job, out var status)) return;
                (status.Text, status.Color) = update.State switch {
                    ExportJobState.Running => ("building…", theme.Hint),
                    ExportJobState.Succeeded => ("done", theme.Success),
                    ExportJobState.Skipped => ($"skipped — {update.Detail}", theme.Hint),
                    _ => ("failed", theme.Error),
                };
            }
        );

        _ = Task.Run(async () =>
            {
                bool ok;
                try
                {
                    ok = await GameExporter.ExportAsync(
                        input,
                        options,
                        log,
                        jobs
                    );
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Export] {ex}");
                    logLine.Text = Truncate(ex.Message, 90);
                    ok = false;
                }
                finally
                {
                    _exporting = false;
                }

                logLine.Text =
                    ok ? $"Done → {options.OutputDir}" : "Export failed — see the Console panel.";
                logLine.Color = ok ? theme.Success : theme.Error;
                progress.Dismissible = true; // unblock
                closeButton.Enabled = true;
                revealButton.Enabled = ok;
            }
        );
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Widget SectionHeader(string text, ThemeData theme)
    {
        return new Padding(
            EdgeInsets.Only(bottom: 6f),
            new Label(text.ToUpperInvariant(), theme.FontSizeCaption, theme.Hint)
        );
    }

    private static Widget CheckRow(Checkbox box, string label, string? caption, ThemeData theme)
    {
        // Min, or this column absorbs the Row's bounded height (the dialog's full remaining space).
        var text = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Min,
            Children = { new Label(label, theme.FontSizeBody, theme.OnSurface) },
        };
        if (caption is not null)
            text.Children.Add(new Label(caption, theme.FontSizeCaption, theme.Hint));
        return new Padding(
            EdgeInsets.Only(bottom: 6f),
            new Row {
                CrossAxisAlignment = CrossAxisAlignment.Start,
                Children = {
                    box,
                    new SizedBox(8f),
                    text,
                },
            }
        );
    }

    private static string PlatformLabel(string rid)
    {
        return Platforms.FirstOrDefault(p => p.Rid == rid).Label ?? rid;
    }

    private static string ModeLabel(ExportMode mode)
    {
        return mode == ExportMode.NativeAot ? "Native AOT" : "JIT";
    }

    private static string RidOsLabel(string rid)
    {
        return rid.StartsWith("osx") ? "macOS" : rid.StartsWith("win") ? "Windows" : "Linux";
    }

    private static void Reveal(string dir)
    {
        try
        {
            var (exe, args) = OperatingSystem.IsMacOS() ? ("open", $"\"{dir}\"")
                : OperatingSystem.IsWindows() ? ("explorer", $"\"{dir}\"")
                : ("xdg-open", $"\"{dir}\"");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Export] reveal failed: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max)
    {
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private sealed class LineProgress(Action<string> onLine) : IProgress<string>
    {
        public void Report(string value)
        {
            onLine(value);
        }
    }

    private sealed class JobProgress(Action<ExportJobUpdate> onUpdate) : IProgress<ExportJobUpdate>
    {
        public void Report(ExportJobUpdate value)
        {
            onUpdate(value);
        }
    }
}