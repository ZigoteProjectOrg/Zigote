package dev.zigote.rider

import com.intellij.execution.RunContentExecutor
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.process.OSProcessHandler
import com.intellij.execution.process.ProcessEvent
import com.intellij.execution.process.ProcessListener
import com.intellij.openapi.Disposable
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Key
import com.intellij.openapi.project.guessProjectDir
import com.intellij.openapi.vfs.VirtualFile
import java.net.ServerSocket
import java.util.concurrent.atomic.AtomicInteger
import javax.swing.Timer

/**
 * The one app the panels are looking at: a port, and how it got there.
 *
 * Launching is the only thing this plugin does that the framework cannot do for itself. Once the app
 * is up, every panel talks to `Zigote.UI.Host.InspectServer` through [ZigoteInspect] — so this class
 * has no model of widgets and no protocol of its own.
 *
 * The port is chosen **here** and handed to the app. An earlier version let the app choose and read the
 * number back out of its console output; that made the panels depend on a log line surviving
 * `dotnet watch`'s forwarding and on a listener being attached before it was written, and when it
 * failed it failed silently, as an empty list. Choosing the port leaves nothing to parse.
 */
@Service(Service.Level.PROJECT)
class ZigoteSession(private val project: Project?) : Disposable {

    /** Swapped for an inline runner in tests; see [Exec]. */
    internal var exec: Exec = Exec.Platform

    /** The inspect port of a reachable app, or null. */
    @Volatile
    var port: Int? = null
        private set

    /** What the panels say while [port] is null: idle, starting, or why it stopped. */
    @Volatile
    var state: String = IDLE
        private set

    private val listeners = mutableListOf<() -> Unit>()
    private val highlightListeners = mutableListOf<() -> Unit>()

    /**
     * Which launch/attach the panels currently belong to. Every callback that can change [port] or
     * [state] — a port poller, a process listener — captures the generation it was born under and
     * checks it before writing. This is what stops a stale app from clobbering a fresh one: press
     * Run app twice and the first process's exit, arriving seconds later, used to null the second
     * process's port and blank every panel.
     */
    private val generation = AtomicInteger()

    /** The launched app, so the next launch (or Stop) can put it down first. */
    private var handler: OSProcessHandler? = null

    /** Use `dotnet watch` so edits reload in place. Off by default — see [launch]. */
    var hotReload: Boolean = false

    /**
     * The reason "hot reload doesn't work" in an IDE that hot reload demonstrably works under:
     * `dotnet watch` reloads on file *save*, and Rider only autosaves on window deactivation — which
     * never happens while you edit code and glance at a tool window inside the same Rider window.
     * So while a watch session is live, edits are saved for you after a moment's pause, which is
     * what hands them to `dotnet watch`. Installed lazily on the first watch launch, inert otherwise.
     */
    private var autoSaveInstalled = false
    private val autoSave = Timer(AUTOSAVE_IDLE_MS) {
        // exec.ui, not the timer's own EDT slot: saving documents needs the write-intent lock, and
        // invokeLater-dispatched runnables hold it (same reason RunContentExecutor goes through it).
        if (hotReload && handler?.isProcessTerminated == false)
            exec.ui { FileDocumentManager.getInstance().saveAllDocuments() }
    }.apply { isRepeats = false }

    private fun installAutoSave() {
        if (autoSaveInstalled) return
        autoSaveInstalled = true
        EditorFactory.getInstance().eventMulticaster.addDocumentListener(object : DocumentListener {
            override fun documentChanged(event: DocumentEvent) {
                if (hotReload && handler?.isProcessTerminated == false) autoSave.restart()
            }
        }, this)
    }

    /** The project is closing: stop the timer and take the previewed app down with the IDE. */
    override fun dispose() {
        autoSave.stop()
        handler?.destroyProcess()
    }

    /** Bounds of the widget selected in a tree tab, drawn over the frame. x, y, w, h; null for none. */
    @Volatile
    var highlight: FloatArray? = null
        private set

    /** Separate from [onChanged] because selecting a tree node must not re-fetch the widget list. */
    fun onHighlight(listener: () -> Unit) {
        highlightListeners += listener
    }

    fun highlight(bounds: FloatArray?) {
        highlight = bounds
        exec.ui { highlightListeners.toList().forEach { it() } }
    }

    /** Called on the EDT whenever the port or the state changes. */
    fun onChanged(listener: () -> Unit) {
        listeners += listener
    }

    private fun changed() {
        exec.ui { listeners.toList().forEach { it() } }
    }

    /** Start the app, previewing [type] when given, and wait for its socket. */
    fun launch(csproj: VirtualFile, type: String?) {
        val project = project ?: error("launch needs a project")
        val gen = generation.incrementAndGet()

        // One previewed app at a time. Without this, Run app pressed twice leaves the first app
        // running invisibly (its window is hidden) and fighting the second for the panels.
        handler?.destroyProcess()

        val chosen = freePort()

        // Plain `dotnet run` by default. `dotnet watch` is the nicer previewer — edits reload in place —
        // but it opens a file watcher per directory, and next to a running Rider that reliably exhausts
        // the inotify instance limit on Linux: the app starts, prints its port, and watch kills it a
        // second later. A previewer that usually fails to start is worse than one without hot reload,
        // so watch is opt-in and its failure is reported rather than silent.
        if (hotReload) installAutoSave()
        val verb = if (hotReload) listOf("watch", "run", "--non-interactive") else listOf("run")
        val command = GeneralCommandLine(listOf("dotnet") + verb + listOf("--project", csproj.path))
            .withWorkDirectory(csproj.parent.path)
            .withEnvironment("ZIGOTE_INSPECT", chosen.toString())
            // The panel is the window. The app's own is a duplicate — and a distracting one, since it
            // pops to the front on launch and shows a different layout as soon as a device is chosen.
            .withEnvironment("ZIGOTE_PREVIEW_HEADLESS", "1")
        if (type != null) command.withEnvironment("ZIGOTE_PREVIEW", type)

        // A Zigote app opens a GPU window, so it needs the desktop session — and a command line
        // inherits the environment the IDE captured from a login shell, which does not reliably carry
        // these. Without XDG_RUNTIME_DIR the engine fails with "No available video device" and the
        // process is gone before it prints anything. Copied from the IDE's own environment, which is
        // displaying a window and therefore certainly has them.
        for (name in SESSION_VARS) System.getenv(name)?.let { command.withEnvironment(name, it) }

        LOG.info("zigote: launching $command (inspect port $chosen)")

        val process = OSProcessHandler(command)
        process.addProcessListener(object : ProcessListener {
            // Two sources for the port, because one was not enough. `chosen` is what we asked for and
            // is polled below; this line is what the app actually bound, which differs when the port
            // was taken between our picking it and the app binding it. And it keeps arriving: under
            // `dotnet watch` a rebuild replaces the app process, which binds a *new* port and
            // announces it through the same watch stdout — so an announcement that differs from the
            // current port is a reconnect, not a duplicate.
            override fun onTextAvailable(event: ProcessEvent, outputType: Key<*>) {
                val announced = PORT_LINE.find(event.text)?.groupValues?.get(1)?.toIntOrNull() ?: return
                if (gen == generation.get() && announced != port) {
                    LOG.info("zigote: app announced port $announced")
                    waitForPort(announced, gen) { !process.isProcessTerminated }
                }
            }

            override fun processTerminated(event: ProcessEvent) {
                // A stale app's death must not blank the session that replaced it.
                if (gen != generation.get()) return
                // "Never started" and "you closed the window" look identical from an empty panel.
                state = if (port == null)
                    "app exited (code ${event.exitCode}) before it was ready — see the run console"
                else IDLE
                port = null
                changed()
            }
        })

        handler = process
        port = null
        // The port is in the status from the start, not only once connected: if the wait ever fails,
        // that number is what "Attach…" needs, and hunting for it was the first thing to go wrong here.
        state = "starting on port $chosen… (the first build can take a while)"
        changed()

        // Through the platform queue, not called directly: RunContentExecutor saves all documents
        // first, and Rider 2026.2 enforces the write-intent lock for that. A raw Swing listener on
        // the EDT does not hold it, so a direct call throws before attaching the console or starting
        // the poller — which looked like "starting on port…" forever. invokeLater-dispatched
        // runnables hold the lock.
        exec.ui {
            RunContentExecutor(project, process)
                // The port in the tab title: the one number needed for "Attach…" if anything goes
                // wrong, somewhere that does not scroll away.
                .withTitle(if (type != null) "Zigote $type — port $chosen" else "Zigote — port $chosen")
                .withActivateToolWindow(true)
                .run()
        }

        waitForPort(chosen, gen) { !process.isProcessTerminated }
    }

    /**
     * Point the panels at an app that is already running — one started from a terminal, or from a run
     * configuration. The escape hatch for everything launching cannot do. Deliberately leaves any
     * launched app alone: attaching re-points the panels, it does not take ownership.
     */
    fun attach(candidate: Int) {
        val gen = generation.incrementAndGet()
        port = null
        state = "connecting to port $candidate…"
        changed()
        waitForPort(candidate, gen) { true }
    }

    /** Put the launched app down and go idle. */
    fun stop() {
        generation.incrementAndGet()
        handler?.destroyProcess()
        handler = null
        port = null
        state = IDLE
        changed()
    }

    /** Knock until the app answers. Generous: a cold checkout builds the Zig engine first. */
    private fun waitForPort(candidate: Int, gen: Int, keepWaiting: () -> Boolean) {
        exec.background {
            val deadline = System.currentTimeMillis() + READY_TIMEOUT_MS
            while (System.currentTimeMillis() < deadline) {
                if (gen != generation.get()) return@background // a newer launch/attach owns the panels
                if (ZigoteInspect.reachable(candidate)) {
                    if (gen != generation.get()) return@background // …even one that started mid-probe
                    port = candidate
                    state = "port $candidate"
                    LOG.info("zigote: connected on port $candidate")
                    changed()
                    return@background
                }
                if (!keepWaiting()) return@background
                Thread.sleep(POLL_MS)
            }
            if (gen != generation.get()) return@background
            state = "nothing answered on port $candidate"
            LOG.warn("zigote: nothing answered on port $candidate")
            changed()
        }
    }

    /**
     * Send one command and return the parsed reply. Blocking; callers belong on a pooled thread — the
     * app answers from its own UI thread, which can be a frame away.
     */
    fun query(command: String): Map<String, Any?> {
        val open = port ?: throw IllegalStateException("No Zigote app is running.")
        return ZigoteInspect.query(open, command)
    }

    companion object {
        private val LOG = logger<ZigoteSession>()
        private const val IDLE = "no app running — press Run app"

        private val PORT_LINE = Regex("""zigote inspect: 127\.0\.0\.1:(\d+)""")

        private const val MAX_SCAN_DEPTH = 4
        private const val MAX_PROJECTS = 200
        private val SKIPPED_DIRS = setOf("bin", "obj", "node_modules", "artifacts", "packages")

        // Five minutes: a cold checkout builds the Zig engine before the app ever runs.
        private const val READY_TIMEOUT_MS = 5 * 60_000L
        private const val POLL_MS = 500L

        // Long enough to not save mid-word, short enough that a save lands before the eye moves from
        // the editor to the preview. `dotnet watch` adds its own debounce on top.
        private const val AUTOSAVE_IDLE_MS = 700

        // X11, Wayland and the session bus. Whichever exists is the one the app needs.
        private val SESSION_VARS = listOf(
            "DISPLAY",
            "WAYLAND_DISPLAY",
            "XDG_RUNTIME_DIR",
            "XDG_SESSION_TYPE",
            "XAUTHORITY",
            "DBUS_SESSION_BUS_ADDRESS",
        )

        fun of(project: Project): ZigoteSession = project.service()

        /** A port free right now; the app binds it a moment later. */
        private fun freePort(): Int = ServerSocket(0).use { it.localPort }

        /**
         * Every runnable project in the solution, for the panel's Project combo. Runnable means an
         * executable OutputType — offering class libraries to "Run app" only manufactures a build
         * error. When the filter leaves nothing (unusual project files), everything is offered
         * rather than nothing.
         */
        fun runnableProjects(project: Project): List<VirtualFile> {
            val found = ArrayList<VirtualFile>()
            project.guessProjectDir()?.let { collectProjects(it, found, 0) }
            val runnable = found.filter(::isExecutable)
            return (runnable.ifEmpty { found }).sortedBy { it.nameWithoutExtension }
        }

        private fun isExecutable(csproj: VirtualFile): Boolean = runCatching {
            val text = String(csproj.contentsToByteArray(), Charsets.UTF_8)
            text.contains("<OutputType>Exe", true) || text.contains("<OutputType>WinExe", true)
        }.getOrDefault(false)

        /** The project owning the file being edited, as the combo's starting selection. */
        fun csprojFor(project: Project): VirtualFile? {
            var dir = FileEditorManager.getInstance(project).selectedFiles.firstOrNull()?.parent
            while (dir != null) {
                dir.children.firstOrNull { it.extension.equals("csproj", true) }?.let { return it }
                dir = dir.parent
            }
            return null
        }

        /** Bounded walk: build output and package caches hold thousands of files and no app to run. */
        private fun collectProjects(dir: VirtualFile, into: MutableList<VirtualFile>, depth: Int) {
            if (depth > MAX_SCAN_DEPTH || into.size >= MAX_PROJECTS) return
            for (child in dir.children) {
                if (child.isDirectory) {
                    val name = child.name
                    if (name.startsWith(".") || name in SKIPPED_DIRS) continue
                    collectProjects(child, into, depth + 1)
                } else if (child.extension.equals("csproj", true)) {
                    into += child
                }
            }
        }
    }
}
