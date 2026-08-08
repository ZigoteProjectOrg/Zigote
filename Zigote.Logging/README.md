# Zigote.Logging

**Serilog, wired the way a Zigote app wants it.** Three sinks for three readers, and the four failure sources that are
silent by default.

```csharp
// First thing in Main — before anything can fail and want logging.
AppLog.Bootstrap();                       // console (Warning+) and the DevTools ring (Debug+)
AppLog.AddFile(storage.Data("logs/app.log"));   // once you know where you may write
AppLog.CaptureFailures();

try { new MyApp().Run(); }
finally { AppLog.Shutdown(); }            // or a crash loses its own report
```

Everything else in the app goes through `Log.ForContext<T>()`. Nothing writes to `Console` directly:
a message that only exists on someone else's terminal is a message nobody will ever read back to you.

## The sinks

| Sink            | Reader                                                                     | Default level |
|-----------------|----------------------------------------------------------------------------|---------------|
| Console         | whoever ran `dotnet run`                                                   | Warning       |
| Rolling file    | the bug report a user sends                                                | Debug         |
| `DebugLog` ring | the in-app log panel (Shift+D) — the only one reachable without a terminal | Debug         |

The file rolls daily, keeps five, and is capped at 8 MB with roll-on-size: a chatty loop on a slow night must not be
able to fill a user's disk, which a daily roll alone does not prevent.
`AddFile` creates the directory, and a file it cannot open degrades to console-only rather than failing the run.

## CaptureFailures

Four things that otherwise vanish:

- a throwing **reactive effect** — otherwise unwinds through whichever thread wrote the signal and skips its siblings
  (`Reactive.OnError`)
- a throwing **bloc handler** (`BlocErrors.OnError`)
- a **fire-and-forget task** — otherwise waits for the finalizer, long after the context that explains it is gone.
  `task.Forget()` logs the fault at the moment it happens; cancellation is not a fault and stays silent.
- an **unhandled exception** on the way out, flushed before the process goes down

`AppLog.DebugLogSink` is public so a test can point a throwaway `LoggerConfiguration` at it.
