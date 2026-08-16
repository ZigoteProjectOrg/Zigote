package dev.zigote.rider

import com.intellij.icons.AllIcons
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.editor.event.EditorFactoryEvent
import com.intellij.openapi.editor.event.EditorFactoryListener
import com.intellij.openapi.editor.markup.GutterIconRenderer
import com.intellij.openapi.editor.markup.HighlighterLayer
import com.intellij.openapi.editor.markup.HighlighterTargetArea
import com.intellij.openapi.editor.markup.RangeHighlighter
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import javax.swing.Icon

/**
 * A "preview this" icon in the gutter next to every widget the running app can show — one click from
 * the code to the picture, without leaving the file or naming the type twice.
 *
 * The list of what is previewable comes from the app itself ([ZigoteSession.targets]), not from
 * guessing which classes are widgets: [PreviewScan] proposes the declarations a file contains and the
 * app decides which of them it can actually construct. With nothing running there is nobody to ask, so
 * only an explicit `[Preview]` gets an icon — an annotation is the author saying it, which needs no
 * confirmation.
 *
 * Clicking swaps a running app in place (a frame) instead of restarting it (the better part of a
 * minute); with nothing running it launches the project the file belongs to. Same path as
 * <kbd>Alt</kbd>+<kbd>Shift</kbd>+<kbd>P</kbd> — see [PreviewWidgetAction] — because they are the same
 * request made two ways.
 */
class ZigotePreviewGutter : EditorFactoryListener {

    private val installed = HashMap<Editor, Markers>()

    override fun editorCreated(event: EditorFactoryEvent) {
        val editor = event.editor
        val project = editor.project ?: return
        val file = FileDocumentManager.getInstance().getFile(editor.document) ?: return
        if (!file.name.endsWith(".cs", ignoreCase = true)) return
        installed[editor] = Markers(editor, project, file).also { it.install() }
    }

    override fun editorReleased(event: EditorFactoryEvent) {
        installed.remove(event.editor)?.uninstall()
    }

    private class Markers(
        private val editor: Editor,
        private val project: Project,
        private val file: com.intellij.openapi.vfs.VirtualFile,
    ) : DocumentListener {
        private val highlighters = ArrayList<RangeHighlighter>()
        private val session = ZigoteSession.of(project)
        private var unsubscribe: Unsubscribe? = null

        fun install() {
            editor.document.addDocumentListener(this)
            // An app starting, stopping or reloading changes which declarations are previewable, and
            // the icons have to follow it — the first launch is exactly when they should appear.
            unsubscribe = session.onTargets { refresh() }
            refresh()
        }

        fun uninstall() {
            editor.document.removeDocumentListener(this)
            unsubscribe?.invoke()
            unsubscribe = null
            clear()
        }

        // ponytail: rescans the whole document per keystroke, like ZigoteColorGutter. A regex over a
        // source file is microseconds; debounce through a MergingUpdateQueue if a generated file ever
        // makes it visible.
        override fun documentChanged(event: DocumentEvent) = refresh()

        private fun clear() {
            highlighters.forEach { editor.markupModel.removeHighlighter(it) }
            highlighters.clear()
        }

        private fun refresh() {
            clear()
            val known = session.targets.map { it.target }.toSet()
            for (declaration in PreviewScan.scan(editor.document.charsSequence)) {
                // The app is the authority whenever there is one: it knows about nested types, open
                // generics and constructors this cannot see. Only with no app running does the
                // annotation stand on its own.
                val previewable =
                    if (known.isEmpty()) declaration.annotated else declaration.name in known
                if (!previewable) continue

                val highlighter = editor.markupModel.addRangeHighlighter(
                    declaration.start,
                    declaration.end,
                    HighlighterLayer.LAST,
                    null,
                    HighlighterTargetArea.EXACT_RANGE,
                )
                highlighter.gutterIconRenderer = Mark(project, file, declaration.name)
                highlighters += highlighter
            }
        }
    }

    /**
     * The gutter icon. Equality is by value so the platform reuses the renderer across refreshes
     * instead of repainting the gutter on every keystroke.
     */
    private data class Mark(
        private val project: Project,
        private val file: com.intellij.openapi.vfs.VirtualFile,
        private val target: String,
    ) : GutterIconRenderer() {

        override fun getIcon(): Icon = AllIcons.Actions.Execute

        override fun getTooltipText(): String =
            "Preview ${target.substringAfterLast('.')} in the Zigote tool window"

        override fun getClickAction(): AnAction = object : AnAction() {
            override fun actionPerformed(e: AnActionEvent) {
                activateZigoteWindow(project)
                ZigoteSession.of(project).show(target, ZigoteSession.csprojFor(file))
            }
        }
    }
}
