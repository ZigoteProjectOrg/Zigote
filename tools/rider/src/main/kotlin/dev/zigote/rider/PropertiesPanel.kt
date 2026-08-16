package dev.zigote.rider

import com.intellij.icons.AllIcons
import com.intellij.openapi.Disposable
import com.intellij.openapi.ui.ComboBox
import com.intellij.ui.components.JBCheckBox
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.components.JBTextField
import com.intellij.util.ui.FormBuilder
import java.awt.BorderLayout
import java.awt.Dimension
import java.awt.FlowLayout
import javax.swing.JButton
import javax.swing.JComponent
import javax.swing.JPanel
import javax.swing.ScrollPaneConstants
import javax.swing.Timer
import javax.swing.event.DocumentEvent
import javax.swing.event.DocumentListener

/**
 * The previewed widget's own properties, as controls under the picture.
 *
 * A preview target is a constructor or a factory, and the parameters it declares defaults for are the
 * knobs its author already wrote down — an empty state, a long title, a disabled button. Without this
 * they are reachable only by editing the file and waiting for a reload, which is why the usual answer
 * is a `Previews` class with six near-identical methods in it. Here it is the same widget with a text
 * field next to it.
 *
 * Nothing is modelled: the app says what the properties are and what kind of control each one wants
 * ([PreviewParam.kind]), and [Previews.spec] turns whatever is typed back into the string the app's
 * `preview` command takes. Adding a property type is a change in the app, not here.
 */
internal class PropertiesPanel(private val onChange: () -> Unit) : JPanel(BorderLayout()), Disposable {

    private var target: PreviewTarget? = null
    private val edited = LinkedHashMap<String, String>()

    /** The live controls, by property name. Rebuilt with the form. */
    private val controls = LinkedHashMap<String, JComponent>()

    private val form = JPanel(BorderLayout())
    private val scroll = JBScrollPane(form).apply {
        border = null
        horizontalScrollBarPolicy = ScrollPaneConstants.HORIZONTAL_SCROLLBAR_NEVER
    }

    /** Typing "Espresso" is eight edits and the app only needs the last one. */
    private val debounce = Timer(APPLY_IDLE_MS) { onChange() }.apply { isRepeats = false }

    private var expanded = true
    private val toggle = JButton().apply { addActionListener { expand(!expanded) } }
    private val caption = JBLabel("Properties")
    private val reset = JButton("Reset").apply {
        toolTipText = "Back to the defaults the widget declares"
        addActionListener {
            edited.clear()
            // Forget the target first: [show] skips an unchanged one, and the controls are still
            // holding the text that was just thrown away.
            val showing = target
            target = null
            show(showing)
            onChange()
        }
    }

    init {
        add(
            JPanel(FlowLayout(FlowLayout.LEFT, 4, 2)).apply {
                add(toggle)
                add(caption)
                add(reset)
            },
            BorderLayout.NORTH,
        )
        add(scroll, BorderLayout.CENTER)
        expand(true)
        isVisible = false
    }

    /** The values to show the target with — only what is set; [Previews.spec] drops the rest. */
    fun values(): Map<String, String> = edited

    internal fun expandedForTest(): Boolean = expanded

    /**
     * Type into the control, then push it now rather than after the debounce — the whole path a
     * keystroke takes, minus the wait. Driving the control instead of the map is the point: it is
     * what catches a rebuild that leaves stale text on screen.
     */
    internal fun setForTest(name: String, value: String) {
        when (val control = controls[name]) {
            is JBTextField -> control.text = value
            is JBCheckBox -> {
                control.isSelected = value.equals("true", ignoreCase = true)
                set(name, value, now = false)
            }

            is ComboBox<*> -> control.selectedItem = value
            else -> set(name, value, now = false)
        }
        debounce.stop()
        onChange()
    }

    internal fun resetForTest() = reset.doClick()

    /** What the control for [name] is showing — the check that a rebuild actually happened. */
    internal fun shownForTest(name: String): String? = when (val control = controls[name]) {
        is JBTextField -> control.text
        is JBCheckBox -> control.isSelected.toString()
        is ComboBox<*> -> control.selectedItem as? String
        else -> null
    }

    /** The pending edit is worthless once nothing is listening for it. */
    override fun dispose() {
        debounce.stop()
    }

    /**
     * Rebuild for [next], keeping what was typed when it is the same target coming back — which is
     * what a `dotnet watch` reload is. Losing an edited title on every save would make the editor
     * unusable for the case it exists for.
     */
    fun show(next: PreviewTarget?) {
        // Identical target, identical controls: rebuilding would take the caret out of the field
        // being typed into, and the panel is told to show its target on every list refresh.
        if (next == target) return

        if (next == null || next.params.isEmpty()) {
            target = next
            edited.clear()
            controls.clear()
            isVisible = false
            revalidate()
            return
        }

        if (next.target != target?.target) edited.clear()
        edited.keys.retainAll(next.params.map { it.name }.toSet())
        target = next

        controls.clear()
        val builder = FormBuilder.createFormBuilder()
        for (param in next.params) {
            builder.addLabeledComponent(JBLabel("${param.name}:"), control(param), 0, false)
        }

        form.removeAll()
        form.add(builder.panel, BorderLayout.NORTH)
        caption.text = "Properties (${next.params.size})"
        isVisible = true
        sizeToContent()
    }

    /** One control, chosen by what the app called the parameter's type. */
    private fun control(param: PreviewParam): JComponent {
        val value = edited[param.name] ?: param.value
        return build(param, value).also { controls[param.name] = it }
    }

    private fun build(param: PreviewParam, value: String): JComponent {
        return when (param.kind) {
            "bool" -> JBCheckBox("", value.equals("true", ignoreCase = true)).apply {
                addActionListener { set(param.name, isSelected.toString(), now = true) }
            }

            "enum" -> ComboBox(param.options.toTypedArray()).apply {
                selectedItem = value
                addActionListener { set(param.name, selectedItem as? String ?: return@addActionListener, now = true) }
            }

            else -> JBTextField(value, TEXT_COLUMNS).apply {
                toolTipText = "default: ${param.value.ifEmpty { "(empty)" }}"
                document.addDocumentListener(object : DocumentListener {
                    override fun insertUpdate(e: DocumentEvent) = set(param.name, text, now = false)
                    override fun removeUpdate(e: DocumentEvent) = set(param.name, text, now = false)
                    override fun changedUpdate(e: DocumentEvent) = set(param.name, text, now = false)
                })
            }
        }
    }

    /** A checkbox has one state per click and no half-typed values; a text field has both. */
    private fun set(name: String, value: String, now: Boolean) {
        edited[name] = value
        if (now) onChange() else debounce.restart()
    }

    private fun expand(open: Boolean) {
        expanded = open
        toggle.icon = if (open) AllIcons.General.ChevronDown else AllIcons.General.ChevronRight
        toggle.toolTipText = if (open) "Hide the properties" else "Show the properties"
        scroll.isVisible = open
        reset.isVisible = open
        sizeToContent()
    }

    /**
     * As tall as the form needs, up to a third of the panel — past that it is eating the picture it
     * exists to change, and the scroll pane it is already in handles the rest.
     */
    private fun sizeToContent() {
        val wanted = if (expanded) form.preferredSize.height + 4 else 0
        val cap = maxOf(MIN_FORM_HEIGHT, parent?.height?.div(3) ?: MAX_FORM_HEIGHT)
        scroll.preferredSize = Dimension(0, minOf(wanted, cap))
        revalidate()
        repaint()
    }

    private companion object {
        // Long enough to type a word through, short enough that stopping shows the result.
        const val APPLY_IDLE_MS = 350
        const val TEXT_COLUMNS = 14
        const val MIN_FORM_HEIGHT = 60
        const val MAX_FORM_HEIGHT = 180
    }
}
