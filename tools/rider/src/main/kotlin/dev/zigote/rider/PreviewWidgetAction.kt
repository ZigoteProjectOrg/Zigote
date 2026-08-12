package dev.zigote.rider

import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.ui.Messages
import com.intellij.openapi.vfs.VirtualFile

/**
 * "Preview Zigote Widget" — runs the widget the caret sits in, on its own, reloading on save.
 *
 * All this does is name the type under the caret and hand it to [ZigoteSession]. The previewer itself
 * is `Zigote.UI.Host.WidgetPreview` in the framework, which is why nothing here knows anything about
 * widgets: the same environment variable drives the preview from a terminal, a VS Code task or the
 * `zigote preview` command, and this plugin is only one of the callers.
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
            ?: return warn(project, "Put the caret inside a widget class first — no type declaration above it.")
        val csproj = projectFileFor(file)
            ?: return warn(project, "No .csproj above ${file.name}, so there is nothing to run.")

        // Through the session rather than starting a process here, so the tool window's panels find the
        // inspect port of the app this action launched.
        ZigoteSession.of(project).launch(csproj, type)
    }

    private fun warn(project: com.intellij.openapi.project.Project, message: String) =
        Messages.showWarningDialog(project, message, "Zigote Preview")

    private fun projectFileFor(file: VirtualFile): VirtualFile? {
        var dir = file.parent
        while (dir != null) {
            dir.children.firstOrNull { it.extension.equals("csproj", true) }?.let { return it }
            dir = dir.parent
        }
        return null
    }

    companion object {
        private val NAMESPACE = Regex("""\bnamespace\s+([A-Za-z_][\w.]*)""")
        private val TYPE = Regex("""\b(?:class|record|struct)\s+([A-Za-z_]\w*)""")

        /**
         * The fully-qualified name of the type the caret is inside, read from the text.
         *
         * Text, not PSI, for the same reason as [ZigoteColors]: C# symbols live in the ReSharper
         * backend. "The last type declared at or before the caret" is what someone pressing the
         * shortcut means in every ordinary file, and a wrong guess costs one error message in the
         * preview window rather than anything worse.
         */
        fun typeAtCaret(text: CharSequence, offset: Int): String? {
            val head = text.subSequence(0, offset.coerceIn(0, text.length))
            val type = TYPE.findAll(head).lastOrNull()?.groupValues?.get(1) ?: return null
            val namespace = NAMESPACE.findAll(head).lastOrNull()?.groupValues?.get(1)
            return if (namespace == null) type else "$namespace.$type"
        }
    }
}
