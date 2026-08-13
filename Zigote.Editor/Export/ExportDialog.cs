using System.Diagnostics;
using System.Runtime.InteropServices;
using Zigote.Core;
using Zigote.Core.Engine;
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
        // Mobile: the native engine cross-compiles from any host, but the app packaging is
        // Apple-toolchain / Android-SDK bound — see GameExporter.MobileHostAvailable.
        ("ios-arm64", "iOS (device)"),
        ("iossimulator-arm64", "iOS (simulator)"),
        ("android-arm64", "Android (arm64)"),
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

        string hostRid = RuntimeInformation.RuntimeIdentifier;
        bool[] platformSelected = Platforms.Select(p => p.Rid == hostRid).ToArray();
        if (!platformSelected.Any(s => s)) platformSelected[0] = true;
        bool jitSelected = true;
        bool aotSelected = false;

        var outField = new AdwEntry {
            Placeholder = "Output folder",
            Text = Path.Combine(path1: Path.GetDirectoryName(state.ProjectPath)!, path2: "export"),
        };
        var validation = new Label(text: "", fontSize: theme.FontSizeCaption, color: theme.Error);

        Dialog? dialog = null;
        // Dialog passes a bounded max height and expects content columns to be MainAxisSize.Min —
        // the default (Max) fills the whole screen and starves later children to zero height.
        var rows = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            MainAxisSize = MainAxisSize.Min,
        };
        rows.Children.Add(
            new Label(text: "Export Game", fontSize: theme.FontSizeTitle, color: theme.OnSurface)
        );
        rows.Children.Add(new SizedBox(height: 6f));
        rows.Children.Add(
            new Label(
                text:
                "Bundles the game (scene, assets, scripts, engine) into distributable builds.",
                fontSize: theme.FontSizeCaption,
                color: theme.Hint
            )
        );
        rows.Children.Add(new SizedBox(height: 14f));

        rows.Children.Add(SectionHeader(text: "Platforms", theme: theme));
        for (int i = 0; i < Platforms.Length; i++)
        {
            int idx = i;
            string label = Platforms[idx].Label +
                           (Platforms[idx].Rid == hostRid ? "  — this machine" : "");
            rows.Children.Add(
                CheckRow(
                    box: new AdwCheckButton(
                        value: platformSelected[idx],
                        onChanged: v => platformSelected[idx] = v
                    ),
                    label: label,
                    caption: null,
                    theme: theme
                )
            );
        }

        rows.Children.Add(new SizedBox(height: 12f));
        rows.Children.Add(SectionHeader(text: "Build flavors", theme: theme));
        rows.Children.Add(
            CheckRow(
                box: new AdwCheckButton(value: jitSelected, onChanged: v => jitSelected = v),
                label: "Self-contained (JIT)",
                caption: "single-file on Windows/Linux; every platform buildable from here",
                theme: theme
            )
        );
        rows.Children.Add(
            CheckRow(
                box: new AdwCheckButton(value: aotSelected, onChanged: v => aotSelected = v),
                label: "Native AOT",
                caption:
                $"fastest startup, smallest runtime; only for this machine's OS ({RidOsLabel(hostRid)})",
                theme: theme
            )
        );
        rows.Children.Add(new SizedBox(height: 12f));

        rows.Children.Add(SectionHeader(text: "Output", theme: theme));

        // Native folder picker beside the field; the export folder may not exist yet, so the
        // field stays editable and the picker just replaces its text.
        async void BrowseOutput()
        {
            try
            {
                string current = outField.Text.Trim();
                string? startDir = Directory.Exists(current)
                    ? current
                    : Path.GetDirectoryName(state.ProjectPath);
                string? picked = await FileDialog.PickFolderAsync(
                    title: "Choose Export Folder",
                    startDirectory: startDir
                );
                if (picked is null) return;
                outField.Text = picked;
                app.RequestPaint();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Export] Folder picker failed: {ex.Message}");
            }
        }

        rows.Children.Add(
            FileDialog.CanShowDialogs
                ? new Row {
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children = {
                        new Expanded(outField),
                        new SizedBox(8f),
                        new AdwButton(label: "Browse…", onPressed: BrowseOutput),
                    },
                }
                : outField
        );
        rows.Children.Add(new SizedBox(height: 6f));
        rows.Children.Add(validation);
        rows.Children.Add(new SizedBox(height: 10f));

        rows.Children.Add(
            new Row {
                MainAxisAlignment = MainAxisAlignment.End,
                Children = {
                    new AdwButton(label: "Cancel", onPressed: () => dialog?.Dismiss()),
                    new SizedBox(10f),
                    new AdwButton(
                        label: "Export",
                        onPressed: () =>
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
                                ProjectPath: state.ProjectPath!,
                                Project: state.Project!,
                                Scripts: state.ScriptRegistry,
                                ScriptAssemblyName: state.ScriptDomain.AssemblyPath is { } asm
                                    ? Path.GetFileNameWithoutExtension(asm)
                                    : null
                            );
                            var options = new ExportOptions(
                                OutputDir: Path.GetFullPath(outField.Text.Trim()),
                                Rids: rids,
                                Modes: modes
                            );

                            dialog?.Dismiss();
                            RunWithProgress(
                                app: app,
                                theme: theme,
                                input: input,
                                options: options
                            );
                        }
                    ) { Style = AdwButtonStyle.Suggested },
                },
            }
        );

        var body = new SizedBox(
            width: 540f,
            child: new Padding(padding: EdgeInsets.All(20f), child: rows)
        );
        dialog = new Dialog(content: body, app: app) { Dismissible = true };
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
        rows.Children.Add(
            new Label(text: "Exporting…", fontSize: theme.FontSizeTitle, color: theme.OnSurface)
        );
        rows.Children.Add(new SizedBox(height: 12f));
        foreach (string rid in options.Rids)
        foreach (var mode in options.Modes)
        {
            var job = new ExportJob(Rid: rid, Mode: mode);
            var status = new Label(
                text: "queued",
                fontSize: theme.FontSizeCaption,
                color: theme.Hint
            );
            jobRows[job] = status;
            rows.Children.Add(
                new Row {
                    Children = {
                        new Label(
                            text: $"{PlatformLabel(rid)} · {ModeLabel(mode)}",
                            fontSize: theme.FontSizeBody,
                            color: theme.OnSurface
                        ),
                        new Spacer(),
                        status,
                    },
                }
            );
            rows.Children.Add(new SizedBox(height: 6f));
        }

        rows.Children.Add(new SizedBox(height: 8f));
        var logLine = new Label(
            text: "Starting…",
            fontSize: theme.FontSizeCaption,
            color: theme.Hint
        ) { MaxLines = 1 };
        rows.Children.Add(logLine);
        rows.Children.Add(new SizedBox(height: 12f));

        var closeButton = new AdwButton("Close") { Enabled = false };
        var revealButton = new AdwButton("Show in file manager") {
            Style = AdwButtonStyle.Suggested,
            Enabled = false,
        };
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

        var body = new SizedBox(
            width: 540f,
            child: new Padding(padding: EdgeInsets.All(20f), child: rows)
        );
        var progress =
            new Dialog(content: body, app: app) {
                Dismissible = false,
            }; // blocked until the pass finishes
        progress.Show();
        closeButton.OnPressed = () => progress.Dismiss();
        revealButton.OnPressed = () => Reveal(options.OutputDir);

        var log = new LineProgress(line =>
            {
                Console.WriteLine(line); // EditorLog tees this into the Console panel
                logLine.Text = Truncate(s: line, max: 90);
            }
        );
        var jobs = new JobProgress(update =>
            {
                if (!jobRows.TryGetValue(key: update.Job, value: out var status)) return;
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
                        input: input,
                        options: options,
                        log: log,
                        jobs: jobs
                    );
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Export] {ex}");
                    logLine.Text = Truncate(s: ex.Message, max: 90);
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
            padding: EdgeInsets.Only(bottom: 6f),
            child: new Label(
                text: text.ToUpperInvariant(),
                fontSize: theme.FontSizeCaption,
                color: theme.Hint
            )
        );
    }

    private static Widget CheckRow(AdwCheckButton box, string label, string? caption,
        ThemeData theme)
    {
        // Min, or this column absorbs the Row's bounded height (the dialog's full remaining space).
        var text = new Column {
            CrossAxisAlignment = CrossAxisAlignment.Start,
            MainAxisSize = MainAxisSize.Min,
            Children = {
                new Label(text: label, fontSize: theme.FontSizeBody, color: theme.OnSurface),
            },
        };
        if (caption is not null)
        {
            text.Children.Add(
                new Label(text: caption, fontSize: theme.FontSizeCaption, color: theme.Hint)
            );
        }

        return new Padding(
            padding: EdgeInsets.Only(bottom: 6f),
            child: new Row {
                CrossAxisAlignment = CrossAxisAlignment.Start,
                Children = {
                    box,
                    new SizedBox(8f),
                    text,
                },
            }
        );
    }

    private static string PlatformLabel(string rid) =>
        Platforms.FirstOrDefault(p => p.Rid == rid).Label ?? rid;

    private static string ModeLabel(ExportMode mode) =>
        mode == ExportMode.NativeAot ? "Native AOT" : "JIT";

    private static string RidOsLabel(string rid)
    {
        if (rid.StartsWith("osx")) return "macOS";
        if (rid.StartsWith("win")) return "Windows";
        if (rid.StartsWith("ios")) return "iOS";
        if (rid.StartsWith("android")) return "Android";
        return "Linux";
    }

    private static void Reveal(string dir)
    {
        try
        {
            (string exe, string args) = OperatingSystem.IsMacOS() ? ("open", $"\"{dir}\"")
                : OperatingSystem.IsWindows() ? ("explorer", $"\"{dir}\"")
                : ("xdg-open", $"\"{dir}\"");
            Process.Start(
                new ProcessStartInfo(fileName: exe, arguments: args) { UseShellExecute = false }
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Export] reveal failed: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private sealed class LineProgress(Action<string> onLine) : IProgress<string>
    {
        public void Report(string value) => onLine(value);
    }

    private sealed class JobProgress(Action<ExportJobUpdate> onUpdate) : IProgress<ExportJobUpdate>
    {
        public void Report(ExportJobUpdate value) => onUpdate(value);
    }
}
