package dev.zigote.rider

import com.intellij.openapi.Disposable
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.openapi.wm.ToolWindowManager
import com.intellij.ui.components.JBLabel
import com.intellij.ui.content.Content
import com.intellij.ui.content.ContentFactory

/**
 * The Zigote tool window: **Preview** ([PreviewPanel]), **Widgets** and **Semantics** ([TreePanel]).
 *
 * All three are views of one running app, read over its inspect socket. None of them models anything —
 * the app decides what a widget tree is, what size it lays out at and what a frame looks like, and the
 * panels draw whatever came back. That is why the same three views are one socket away for any other
 * editor.
 */
class ZigoteToolWindowFactory : ToolWindowFactory, DumbAware {
    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        val contents = ContentFactory.getInstance()
        val session = ZigoteSession.of(project)
        toolWindow.contentManager.addContent(
            contents.createContent(PreviewPanel(project, session), "Preview", false).disposedWith()
        )
        toolWindow.contentManager.addContent(
            contents.createContent(TreePanel(session, "widgets", ::widgetLabel), "Widgets", false)
                .disposedWith()
        )
        toolWindow.contentManager.addContent(
            contents.createContent(TreePanel(session, "semantics", ::semanticsLabel), "Semantics", false)
                .disposedWith()
        )
    }

    /**
     * Every panel, not just the first: each one subscribes to the session, and a subscription held by
     * a closed tool window is both a leak and a callback into dead Swing components.
     */
    private fun Content.disposedWith(): Content = apply {
        (component as? Disposable)?.let { setDisposer(it) }
    }
}

internal const val ZIGOTE_TOOL_WINDOW = "Zigote"

/**
 * Bring the panels up, for the paths that start a preview from the editor — the gutter icon and
 * **Preview Zigote Widget**. In preview mode the app's own window is hidden, so a preview nobody
 * opened the tool window for would be a process with nothing on screen.
 */
internal fun activateZigoteWindow(project: Project) {
    ToolWindowManager.getInstance(project).getToolWindow(ZIGOTE_TOOL_WINDOW)?.activate(null)
}

// ── shared ────────────────────────────────────────────────────────────────────

private val LOG = logger<ZigoteToolWindowFactory>()

/**
 * Run [command] against the live app off the EDT and hand the reply back on it.
 *
 * The app answers from its own UI thread, so a slow frame is a slow reply — doing this inline would
 * freeze the IDE for exactly as long as the app being inspected is busy. Every failure ends up in the
 * status label *and* in the log: a panel that fails silently is what made this hard to fix once already.
 */
internal fun query(
    session: ZigoteSession,
    command: String,
    status: JBLabel,
    onReply: (Map<String, Any?>) -> Unit,
) {
    if (session.port == null) {
        status.text = session.state
        return
    }

    session.exec.background {
        val result = runCatching { session.query(command) }
        session.exec.ui {
            result.onSuccess { reply ->
                val error = reply.error()
                if (error != null) {
                    status.text = error
                    LOG.warn("zigote: '$command' -> $error")
                } else {
                    runCatching { onReply(reply) }.onFailure { fail(status, command, it) }
                }
            }.onFailure { fail(status, command, it) }
        }
    }
}

private fun fail(status: JBLabel, command: String, e: Throwable) {
    status.text = "${e.javaClass.simpleName}: ${e.message ?: "failed"}"
    LOG.warn("zigote: '$command' failed", e)
}
