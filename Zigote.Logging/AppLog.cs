using Serilog;
using Serilog.Core;
using Serilog.Events;
using Zigote.Bloc;
using Zigote.Core.Diagnostics;

namespace Zigote.Logging;

/// <summary>
///     An app's logging, configured once at the top of <c>Main</c> and reached everywhere else
///     through <c>Log.ForContext&lt;T&gt;()</c>.
///     <para>
///         Three sinks, for three readers. The console is for whoever ran <c>dotnet run</c>:
///         warnings and worse, one line each. The rolling file is for the bug report a user actually
///         sends. The <see cref="DebugLog" /> ring is for the in-app log panel, which is the only
///         one of the three a user can reach without a terminal.
///     </para>
///     <para>
///         Nothing in an app writes to <see cref="Console" /> directly: a message that only exists
///         on someone else's terminal is a message nobody will ever read back to you.
///     </para>
/// </summary>
public static class AppLog
{
    private const string ConsoleTemplate =
        "[{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    ///     Console and the in-app ring — everything the app can log before it knows where it is
    ///     allowed to write. Safe to call more than once; the last configuration wins.
    /// </summary>
    public static void Bootstrap(LogEventLevel consoleLevel = LogEventLevel.Warning)
    {
        Log.Logger = Build(consoleLevel).CreateLogger();
    }

    /// <summary>
    ///     Add the rolling file sink, once the sandbox has told us where the app may write. A logger
    ///     that cannot open its file still logs to the console rather than failing the run.
    /// </summary>
    /// <param name="path">The file to roll. Its directory is created if missing.</param>
    /// <param name="consoleLevel">Unchanged from <see cref="Bootstrap" />; passed again because the whole logger is rebuilt.</param>
    /// <param name="fileLevel">How much reaches the file. Debug is what makes a bug report worth reading.</param>
    public static void AddFile(
        string path,
        LogEventLevel consoleLevel = LogEventLevel.Warning,
        LogEventLevel fileLevel = LogEventLevel.Debug)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            Log.Logger = Build(consoleLevel)
                .WriteTo.File(
                    path,
                    fileLevel,
                    FileTemplate,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 5,
                    // Capped on purpose: a chatty loop on a slow night must not be able to fill a
                    // user's disk, which a daily roll alone does not prevent.
                    fileSizeLimitBytes: 8L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true
                )
                .CreateLogger();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Log file {Path} unavailable; logging to the console only", path);
        }
    }

    /// <summary>
    ///     Route everything that fails outside a call stack anyone is watching into the log.
    ///     <para>
    ///         Four sources, all of which are silent by default: a throwing reactive effect (which
    ///         otherwise unwinds through whichever thread wrote the signal and skips its siblings),
    ///         a bloc handler, a fire-and-forget task whose exception waits for the finalizer, and
    ///         an unhandled exception on the way out.
    ///     </para>
    /// </summary>
    public static void CaptureFailures()
    {
        Core.State.Reactive.OnError = ex =>
            Log.ForContext("SourceContext", "Reactive").Error(
                ex,
                "Unhandled failure in a reactive effect"
            );

        BlocErrors.OnError = (ex, context) =>
            Log.ForContext("SourceContext", "Bloc").Error(ex, "{Context}", context);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(
                e.ExceptionObject as Exception,
                "Unhandled exception, terminating: {Terminating}",
                e.IsTerminating
            );
            Log.CloseAndFlush(); // the process is going down; the file must have the line
        };
    }

    /// <summary>Flush the file sink. The last thing <c>Main</c> does, or a crash loses its own report.</summary>
    public static void Shutdown()
    {
        Log.CloseAndFlush();
    }

    private static LoggerConfiguration Build(LogEventLevel consoleLevel)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(consoleLevel, ConsoleTemplate)
            .WriteTo.Sink(new DebugLogSink());
    }

    /// <summary>
    ///     Serilog → the engine's <see cref="DebugLog" /> ring, which the DevTools log panel tails.
    ///     Level maps one-to-one; the category column shows the short <c>SourceContext</c> (the class
    ///     behind <c>Log.ForContext&lt;T&gt;()</c>), so the panel's filter reads "BrowseBloc", not a
    ///     full namespace.
    /// </summary>
    public sealed class DebugLogSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            var message = logEvent.RenderMessage();
            if (logEvent.Exception is { } e)
                message = $"{message} — {e.GetType().Name}: {e.Message}";

            DebugLog.Add(MapLevel(logEvent.Level), message, Category(logEvent));
        }

        private static DebugLogLevel MapLevel(LogEventLevel level)
        {
            return level switch {
                LogEventLevel.Verbose => DebugLogLevel.Trace,
                LogEventLevel.Debug => DebugLogLevel.Debug,
                LogEventLevel.Information => DebugLogLevel.Info,
                LogEventLevel.Warning => DebugLogLevel.Warning,
                LogEventLevel.Error => DebugLogLevel.Error,
                _ => DebugLogLevel.Fatal,
            };
        }

        private static string Category(LogEvent logEvent)
        {
            if (logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var value)
                && value is ScalarValue { Value: string context })
                return context[(context.LastIndexOf('.') + 1)..];

            return "app";
        }
    }
}

/// <summary>Fire-and-forget, with a witness.</summary>
public static class TaskLogging
{
    /// <summary>
    ///     A chip press has nothing to await, so an app discards tasks deliberately — but a discarded
    ///     task's exception otherwise waits for the finalizer (or never fires at all once something
    ///     calls SetObserved), long after the context that explains it is gone. This logs the fault
    ///     the moment it happens. Cancellation is not a fault and stays silent.
    /// </summary>
    public static void Forget(this Task task)
    {
        task.ContinueWith(
            static t => Log.Error(t.Exception, "Fire-and-forget task faulted"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }
}
