package dev.zigote.rider

import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.command.WriteCommandAction
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
import com.intellij.ui.ColorChooserService
import com.intellij.util.ui.ColorIcon
import javax.swing.Icon

/**
 * Puts a colour swatch in the gutter next to every Zigote colour literal, and opens the platform
 * colour picker when it is clicked — writing the chosen colour back in the shape the literal was in.
 *
 * Works on the editor's markup model directly instead of through a `LineMarkerProvider`, because line
 * markers need PSI and Rider's C# PSI lives in the ReSharper backend. See [ZigoteColors].
 */
class ZigoteColorGutter : EditorFactoryListener {

    private val installed = HashMap<Editor, Markers>()

    override fun editorCreated(event: EditorFactoryEvent) {
        val editor = event.editor
        val file = FileDocumentManager.getInstance().getFile(editor.document) ?: return
        if (!file.name.endsWith(".cs", ignoreCase = true)) return
        installed[editor] = Markers(editor).also { it.install() }
    }

    override fun editorReleased(event: EditorFactoryEvent) {
        installed.remove(event.editor)?.uninstall()
    }

    private class Markers(private val editor: Editor) : DocumentListener {
        private val highlighters = ArrayList<RangeHighlighter>()

        fun install() {
            editor.document.addDocumentListener(this)
            refresh()
        }

        fun uninstall() {
            editor.document.removeDocumentListener(this)
            clear()
        }

        // ponytail: rescans the whole document per keystroke. A regex over a source file is
        // microseconds; if a very large generated file ever makes this visible, debounce through a
        // MergingUpdateQueue rather than trying to patch ranges incrementally.
        override fun documentChanged(event: DocumentEvent) = refresh()

        private fun clear() {
            highlighters.forEach { editor.markupModel.removeHighlighter(it) }
            highlighters.clear()
        }

        private fun refresh() {
            clear()
            for (literal in ZigoteColors.scan(editor.document.charsSequence)) {
                val highlighter = editor.markupModel.addRangeHighlighter(
                    literal.start,
                    literal.end,
                    HighlighterLayer.LAST,
                    null,
                    HighlighterTargetArea.EXACT_RANGE,
                )
                highlighter.gutterIconRenderer = Swatch(editor, literal)
                highlighters += highlighter
            }
        }
    }

    /**
     * The gutter swatch. Equality is by value so the platform can reuse the renderer across refreshes
     * instead of repainting the gutter on every keystroke.
     */
    private data class Swatch(
        private val editor: Editor,
        private val literal: ZigoteColors.Literal,
    ) : GutterIconRenderer() {

        override fun getIcon(): Icon = ColorIcon(12, java.awt.Color(literal.argb, true))

        override fun getTooltipText(): String = "#%08X — click to edit".format(literal.argb)

        override fun getClickAction(): AnAction = object : AnAction() {
            override fun actionPerformed(e: AnActionEvent) {
                val picked = ColorChooserService.instance.showDialog(
                    editor.project,
                    editor.component,
                    "Zigote Color",
                    java.awt.Color(literal.argb, true),
                    literal.form.supportsAlpha,
                ) ?: return

                val argb = picked.rgb.let { if (literal.form.supportsAlpha) it else it or (0xFF shl 24) }
                val text = ZigoteColors.format(argb, literal.form)
                val project = editor.project ?: return
                WriteCommandAction.runWriteCommandAction(project, "Change Zigote Color", null, {
                    editor.document.replaceString(literal.start, literal.end, text)
                })
            }
        }
    }
}
