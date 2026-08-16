package dev.zigote.rider

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.ui.Messages

/**
 * "Preview Zigote Widget" — shows the widget the caret sits in, on its own.
 *
 * All this does is name the declaration under the caret and hand it to [ZigoteSession], which swaps a
 * running app in place and only launches one when there is nothing running. The previewer itself is
 * `Zigote.UI.Host.WidgetPreview` in the framework, which is why nothing here knows anything about
 * widgets: the same environment variable drives the preview from a terminal, a VS Code task or the
 * `zigote preview` command, and this plugin is only one of the callers.
 *
 * The same request is one click away in the gutter — see [ZigotePreviewGutter].
 */
class PreviewWidgetAction : AnAction() {

    override fun getActionUpdateThread(): ActionUpdateThread = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible =
            e.project != null && e.getData(CommonDataKeys.VIRTUAL_FILE)?.extension.equals("cs", true)
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        val editor = e.getData(CommonDataKeys.EDITOR) ?: return
        val file = e.getData(CommonDataKeys.VIRTUAL_FILE) ?: return

        val type = typeAtCaret(editor.document.charsSequence, editor.caretModel.offset)
            ?: return warn(project, "Put the caret inside a widget class first — no declaration above it.")
        val csproj = ZigoteSession.csprojFor(file)
            ?: return warn(project, "No .csproj above ${file.name}, so there is nothing to run.")

        // Through the session rather than starting a process here: it swaps a running app in place —
        // a frame, against the better part of a minute for a rebuild — and only launches when there
        // is nothing to swap. The tool window follows whichever happened.
        activateZigoteWindow(project)
        ZigoteSession.of(project).show(type, csproj)
    }

    private fun warn(project: com.intellij.openapi.project.Project, message: String) =
        Messages.showWarningDialog(project, message, "Zigote Preview")

    companion object {
        /**
         * The fully-qualified name of the declaration the caret is inside, read from the text.
         *
         * "The last type or static widget factory declared at or before the caret" is what someone
         * pressing the shortcut means in every ordinary file, and a wrong guess costs one error
         * message in the preview window rather than anything worse. See [PreviewScan].
         */
        fun typeAtCaret(text: CharSequence, offset: Int): String? = PreviewScan.at(text, offset)
    }
}
