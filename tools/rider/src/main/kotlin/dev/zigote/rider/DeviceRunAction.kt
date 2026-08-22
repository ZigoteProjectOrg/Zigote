package dev.zigote.rider

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.process.OSProcessHandler
import com.intellij.execution.process.ProcessEvent
import com.intellij.execution.process.ProcessListener
import com.intellij.execution.ui.RunContentExecutor
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import java.io.File
import javax.swing.Timer

/**
 * "Run on Device" — deploy the app to the attached phone or emulator and keep editing it.
 *
 * The command is `zigote device run`, not a private copy of what it does: choosing the RID from the
 * device's ABI, passing the hot-reload switch the head needs, pointing every adb call at one serial.
 * That belongs in one place, and a terminal has to be able to do the same thing this button does.
 *
 * What the plugin adds is the half a CLI cannot: **saving**. `dotnet watch` reloads on file save, and
 * Rider saves on window deactivation — which never happens while you edit code and watch the phone
 * next to your keyboard. So for as long as the session runs, edits are saved for you after a pause.
 * Without it the feature silently does nothing, which is exactly how it looks when it is broken.
 * ([ZigoteSession] carries the same trick for the preview panel, tied to that panel's own lifecycle.)
 */
class DeviceRunAction : AnAction() {

    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val root = project.basePath ?: return

        if (findHead(File(root)) == null) {
            return warn(
                project,
                "No Android head in this project. Run `zigote add android` from inside the app first."
            )
        }

        // Off the EDT: asking adb what is attached starts its daemon on a cold machine, and a frozen
        // IDE is what that looks like from the outside.
        Exec.Platform.background {
            val devices = list(root)
            Exec.Platform.ui {
                if (devices.isEmpty()) {
                    warn(
                        project,
                        "No device ready. Plug one in with USB debugging on, or start an emulator.\n" +
                            "(If `zigote` is not on PATH, install it: dotnet tool install -g Zigote.Cli.)"
                    )
                    return@ui
                }

                // Only ask when there is something to ask about — one device is not a question.
                val serial = if (devices.size == 1) devices[0] else Messages.showEditableChooseDialog(
                    "Which device?", "Zigote — Run on Device", null,
                    devices.toTypedArray(), devices[0], null
                ) ?: return@ui

                run(project, root, serial)
            }
        }
    }

    private fun run(project: Project, root: String, serial: String) {
        val command = GeneralCommandLine(
            listOf("zigote", "device", "run", "--dir", root, "--serial", serial)
        ).withWorkDirectory(root)

        val handler = OSProcessHandler(command)
        AutoSave(project, handler)
        RunContentExecutor(project, handler)
            .withTitle("Zigote on $serial")
            .withActivateToolWindow(true)
            .withStop({ handler.destroyProcess() }, { !handler.isProcessTerminated })
            .run()
    }

    /** Serials from `zigote device list`, which prints "  <serial>  <model>" per ready device. */
    private fun list(root: String): List<String> = try {
        val process = GeneralCommandLine(listOf("zigote", "device", "list", "--dir", root))
            .withWorkDirectory(root)
            .createProcess()
        process.inputStream.bufferedReader().readLines()
            .map { it.trim() }
            .filter { it.isNotEmpty() && !it.startsWith("No devices") }
            // A device the CLI reported as not ready carries its state in brackets; deploying to it
            // fails halfway through an install rather than up front.
            .filter { !it.endsWith(")") }
            .map { it.split(Regex("\\s+"))[0] }
    } catch (_: Exception) {
        emptyList()
    }

    /** The `*.Android` head, so "nothing to deploy" is said before a device is even looked for. */
    private fun findHead(root: File): File? = root.walkTopDown()
        .onEnter { it.name != "bin" && it.name != "obj" && !it.name.startsWith(".") }
        .maxDepth(3)
        .firstOrNull { it.isFile && it.name.endsWith(".Android.csproj") }

    private fun warn(project: Project, message: String) =
        Messages.showWarningDialog(project, message, "Zigote — Run on Device")
}

/**
 * Saves modified documents while a hot-reload session runs, a moment after typing stops.
 *
 * Tied to the process: it stops mattering the instant the session ends, and it must not outlive it —
 * an IDE that saves on its own after the user thought they had stopped is a surprise, not a feature.
 */
private class AutoSave(project: Project, handler: OSProcessHandler) {

    // Through Exec.ui, not the timer's own EDT slot: saving documents needs the write-intent lock,
    // and invokeLater-dispatched runnables hold it. Same hop ZigoteSession takes, for the same reason.
    private val timer = Timer(IDLE_MS) {
        if (!handler.isProcessTerminated)
            Exec.Platform.ui { FileDocumentManager.getInstance().saveAllDocuments() }
    }.apply { isRepeats = false }

    init {
        val listener = object : DocumentListener {
            override fun documentChanged(event: DocumentEvent) {
                if (!handler.isProcessTerminated) timer.restart()
            }
        }
        // Disposed with the project rather than never: the multicaster outlives any one session.
        EditorFactory.getInstance().eventMulticaster.addDocumentListener(listener, project)
        handler.addProcessListener(object : ProcessListener {
            override fun processTerminated(event: ProcessEvent) = timer.stop()
        })
    }

    private companion object {
        // Long enough not to save mid-word, short enough that a save feels like part of the edit.
        const val IDLE_MS = 700
    }
}
