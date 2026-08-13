package dev.zigote.rider

import com.intellij.icons.AllIcons
import com.intellij.openapi.Disposable
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.ComboBox
import com.intellij.openapi.ui.Messages
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.ui.components.JBCheckBox
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.components.JBTextField
import com.intellij.ui.content.Content
import com.intellij.ui.content.ContentFactory
import com.intellij.ui.treeStructure.Tree
import java.awt.BorderLayout
import java.awt.BasicStroke
import java.awt.Color
import java.awt.Component
import java.awt.Dimension
import java.awt.FlowLayout
import java.awt.Graphics
import java.awt.Graphics2D
import java.awt.Rectangle
import java.awt.RenderingHints
import java.awt.event.ComponentAdapter
import java.awt.event.ComponentEvent
import java.awt.image.BufferedImage
import java.io.ByteArrayInputStream
import java.util.Base64
import javax.imageio.ImageIO
import javax.swing.DefaultComboBoxModel
import javax.swing.DefaultListCellRenderer
import javax.swing.Icon
import javax.swing.JButton
import javax.swing.JComponent
import javax.swing.JList
import javax.swing.JPanel
import javax.swing.JSplitPane
import javax.swing.Scrollable
import javax.swing.Timer
import javax.swing.event.DocumentEvent
import javax.swing.event.DocumentListener
import javax.swing.tree.DefaultMutableTreeNode
import javax.swing.tree.DefaultTreeModel
import javax.swing.tree.TreeSelectionModel

/**
 * The Zigote tool window: **Preview**, **Widgets** and **Semantics**.
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
        )
        toolWindow.contentManager.addContent(
            contents.createContent(TreePanel(session, "semantics", ::semanticsLabel), "Semantics", false)
        )
    }

    private fun Content.disposedWith(): Content = apply {
        (component as? Disposable)?.let { setDisposer(it) }
    }
}

// ── preview ───────────────────────────────────────────────────────────────────

/**
 * The running app's frame, at a chosen device size, in the tool window.
 *
 * The app renders it — this is `shot` over the socket, decoded and drawn. Deliberately a picture and
 * not an embedded window: the app is a separate process with its own GPU surface, and pulling a frame
 * costs one re-render into an offscreen target, while hosting that surface inside a Swing hierarchy
 * would mean platform-specific window reparenting for a preview.
 *
 * Device sizes are not a frame drawn around the picture: the app is told to lay its live tree out at
 * that size, so breakpoints, MediaQuery and wrapping all behave as they would on the device.
 */
internal class PreviewPanel(
    private val project: Project?,
    private val session: ZigoteSession,
) : JPanel(BorderLayout()), Disposable {

    /** A runnable csproj, rendered by name. The combo is the answer to "which app does Run app run". */
    private class Proj(val file: com.intellij.openapi.vfs.VirtualFile) {
        override fun toString() = file.nameWithoutExtension
    }

    /** Whether the toolbar is collapsed to its essentials — see [autoCompact]. */
    private var compactOn = false

    /** Set the first time the toggle is pressed; from then on the width rule stops deciding. */
    private var compactChosen = false

    private val projectsModel = DefaultComboBoxModel<Proj>()
    private val projectCombo = ComboBox(projectsModel).apply { toolTipText = "Project that Run app starts" }

    private val targets = DefaultComboBoxModel<String>()
    private val combo = ComboBox(targets).apply {
        // Bounded, or one long type name ("AdwaitaGallery.Pages.ImageGridPage") sets the toolbar's
        // width and pushes the buttons out of a docked panel.
        prototypeDisplayValue = FULL_NAME
        maximumSize = Dimension(260, preferredSize.height)
        // Compact drops the namespace — "ImageGridPage" is the part being read anyway, and the whole
        // name is still the tooltip and still what selects. Rendering, not the model: everything that
        // talks to the app keeps sending the type name it was given.
        renderer = object : DefaultListCellRenderer() {
            override fun getListCellRendererComponent(
                list: JList<*>?,
                value: Any?,
                index: Int,
                selected: Boolean,
                focused: Boolean,
            ): Component {
                val full = value as? String
                val shown = if (compactOn) full?.substringAfterLast('.') else full
                return super.getListCellRendererComponent(list, shown, index, selected, focused)
            }
        }
    }
    private val devices = ComboBox(DefaultComboBoxModel(Devices.all.toTypedArray()))
        .apply { toolTipText = "Size the app lays its live tree out at" }
    private val landscape = JBCheckBox("Landscape")
    private val theme: ComboBox<String> = ComboBox(arrayOf("Dark", "Light")).apply { toolTipText = "App theme" }
    private val locales = DefaultComboBoxModel<String>()
    private val localeCombo = ComboBox(locales).apply { toolTipText = "App locale" }
    private val localeCaption = JBLabel("Locale:")
    private val zoom: ComboBox<String> = ComboBox(arrayOf(FIT, "100%", "200%"))
        .apply { toolTipText = "Zoom the picture" }
    private val live = JBCheckBox("Live")
    private val hotReload = JBCheckBox("Hot reload")
    private val status = JBLabel("")
    private val canvas = Canvas({ zoom.selectedItem as? String ?: FIT }, session)
    private var populating = false

    private val runButton = JButton("Run app").apply { addActionListener { launch() } }
    private val stopButton = JButton("Stop").apply { addActionListener { session.stop() } }
    private val attachButton = JButton("Attach…").apply { addActionListener { attach() } }
    private val refreshButton = JButton("Refresh").apply { addActionListener { sync() } }
    private val projectCaption = JBLabel("Project:")
    private val widgetCaption = JBLabel("Widget:")
    private val deviceCaption = JBLabel("Device:")
    private val themeCaption = JBLabel("Theme:")
    private val zoomCaption = JBLabel("Zoom:")

    /** Collapses the toolbar to the essentials, and back. Icon-only, because width is its whole point. */
    private val compactToggle = JButton().apply { addActionListener { chooseCompact(!compactOn) } }

    private val toolbar = JPanel(WrapLayout())

    /**
     * The toolbar, in order, and what each control is worth when there is no room for all of them.
     *
     * Compact is a filter over this one list rather than a second arrangement of the same controls:
     * the controls stay where they are and the ones a preview does not need every minute go
     * invisible, which [WrapLayout] then stops reserving a row for. A second arrangement would drift
     * from this one on the first control added to either.
     *
     * Essential is what a preview is *used* through — start and stop the app, watch it live, choose
     * what to show and how big. The rest is set once and left alone, which is exactly what the toggle
     * is for.
     */
    private val items = listOf(
        Item(compactToggle, essential = true),
        Item(runButton, essential = true, icon = AllIcons.Actions.Execute),
        Item(stopButton, essential = true, icon = AllIcons.Actions.Suspend),
        Item(refreshButton, essential = true, icon = AllIcons.Actions.Refresh),
        Item(attachButton),
        Item(live, essential = true),
        Item(hotReload),
        Item(projectCombo, projectCaption),
        Item(combo, widgetCaption, essential = true),
        Item(devices, deviceCaption, essential = true),
        Item(landscape),
        Item(theme, themeCaption),
        Item(localeCombo, localeCaption, available = { locales.size > 0 }),
        Item(zoom, zoomCaption),
        Item(status, essential = true),
    )

    /**
     * One control in the toolbar: its caption, whether compact keeps it, and — for the verbs — the
     * icon it shrinks to.
     *
     * A button's words are remembered here because compact takes them away, and become its tooltip,
     * so an icon-only **Run app** still says what it is. [available] is for a control that has
     * nothing to offer at all, like the locale combo of an app with no `LocalizationsScope`; compact
     * must not make it reappear.
     */
    private class Item(
        val control: JComponent,
        val caption: JBLabel? = null,
        val essential: Boolean = false,
        val icon: Icon? = null,
        val available: () -> Boolean = { true },
    ) {
        private val words = (control as? JButton)?.text

        init {
            if (icon != null) control.toolTipText = words
        }

        /** Icon-only while [on], the words back when there is room for them again. */
        fun shrink(on: Boolean) {
            val button = control as? JButton ?: return
            if (icon == null) return
            button.text = if (on) "" else words
            button.icon = if (on) icon else null
        }
    }

    // The polled fallback for Live against an app built before `stream` existed. Half a second:
    // fast enough to watch a reload land, slow enough to stay off the app's critical path.
    private val timer = Timer(500) { shot() }

    /** The open frame stream, closed to stop it; null while polling or not live. */
    @Volatile
    private var streamSocket: java.net.Socket? = null

    /** The capture density the open stream was started with — see [retuneStream]. */
    private var streamScale: Double = 1.0

    /** The port the panel last pushed its state to — a change means the app restarted or was swapped. */
    private var appliedPort: Int? = null

    /** Theme/locale are only re-sent to a restarted app when the user actually chose them once. */
    private var themeChosen = false
    private var localeChosen: String? = null

    /**
     * Synthetic input, in order. One queue and one sender because ordering is the contract — an `up`
     * overtaking its `down` is a phantom click — while pointer-move floods coalesce (only the newest
     * queued move survives). The sender triggers a `shot` after discrete events when no stream is
     * running, so a click is *seen* even with Live off.
     */
    private val inputQueue = java.util.concurrent.LinkedBlockingDeque<String>()

    // The immediate shot shows the click; the settle shot, a beat later, shows what the click grew
    // into — a dialog fading in, a page transition — which the immediate one catches mid-animation.
    private val settleShot = Timer(400) {
        if (streamSocket == null && session.port != null) shot()
    }.apply { isRepeats = false }

    init {
        Thread({
            while (true) {
                val cmd = inputQueue.takeFirst()
                if (cmd.isEmpty()) return@Thread // poison pill from dispose
                runCatching { session.query(cmd) }
                if (!cmd.startsWith("input move") && streamSocket == null)
                    session.exec.ui {
                        if (session.port != null) shot()
                        settleShot.restart()
                    }
            }
        }, "zigote-input").apply { isDaemon = true }.start()
    }

    // Resizing fires a burst of events; only the size it settles at is worth acting on. Under
    // "Panel (adapt)" that means a relayout in the app; under a fixed device size the layout does not
    // change but the size it is *drawn* at does, and with it the density worth capturing.
    private val resizeDebounce = Timer(250) {
        if (devices.selectedItem === Devices.PANEL) applyDevice() else if (streamSocket == null) shot()
        retuneStream()
    }.apply { isRepeats = false }

    init {
        combo.addActionListener {
            // The full type name, wherever the combo is only showing the tail of it.
            combo.toolTipText = combo.selectedItem as? String
            showSelected()
        }
        devices.addActionListener { applyDevice() }
        landscape.addActionListener { applyDevice() }
        theme.addActionListener { applyTheme() }
        localeCombo.addActionListener { applyLocale() }
        // Zoom changes what the picture is drawn at, and so what density is worth capturing: at 200%
        // a 1× frame is enlarged four-fold on a Retina panel. Live re-opens at the new density; the
        // still path picks it up on its next shot anyway.
        zoom.addActionListener { canvas.revalidate(); canvas.repaint(); retuneStream(); if (streamSocket == null) shot() }
        live.addActionListener { if (live.isSelected) startLive() else stopLive() }
        hotReload.addActionListener { session.hotReload = hotReload.isSelected }

        // Interactivity: the canvas hands over wire-ready `input …` commands; the queue keeps them
        // ordered and coalesces move floods. Dropped entirely while no app is connected.
        canvas.onInput = { cmd ->
            if (session.port != null) {
                if (cmd.startsWith("input move") && inputQueue.peekLast()?.startsWith("input move") == true)
                    inputQueue.pollLast()
                inputQueue.offerLast(cmd)
            }
        }

        project?.let { open ->
            val available = ZigoteSession.runnableProjects(open)
            available.forEach { projectsModel.addElement(Proj(it)) }
            // Start on the project owning the open file — the combo exists so this is a default, not a decision.
            val current = ZigoteSession.csprojFor(open)?.path
            available.indexOfFirst { it.path == current }.takeIf { it >= 0 }
                ?.let { projectCombo.selectedIndex = it }
        }
        hotReload.toolTipText =
            "Run under 'dotnet watch' so edits reload in place. Needs spare inotify watches on Linux."

        // WrapLayout, not FlowLayout: a docked tool window is narrower than this toolbar, and plain
        // FlowLayout hides every control past the first row rather than wrapping the panel taller.
        // Actions come first — behind the collapse toggle, which has to stay reachable to undo itself —
        // so that if anything is ever clipped, it is not how you start.
        add(toolbar.apply {
            for (item in items) {
                item.caption?.let { add(it) }
                add(item.control)
            }
        }, BorderLayout.NORTH)

        val scroll = JBScrollPane(canvas)
        scroll.verticalScrollBar.unitIncrement = 16
        scroll.horizontalScrollBar.unitIncrement = 16
        add(scroll, BorderLayout.CENTER)

        // "Panel (adapt)" means the app follows this viewport, so its size is an input to the app.
        scroll.viewport.addComponentListener(object : ComponentAdapter() {
            override fun componentResized(e: ComponentEvent) {
                canvas.viewport = scroll.viewport.extentSize
                resizeDebounce.restart()
                canvas.revalidate()
            }
        })

        // The panel's own width decides whether the toolbar is worth its rows — see [autoCompact].
        addComponentListener(object : ComponentAdapter() {
            override fun componentResized(e: ComponentEvent) = autoCompact()
        })

        session.onChanged { sync() }
        session.onHighlight { canvas.repaint() }
        applyToolbar()
        sync()
    }

    override fun dispose() {
        stopLive()
        resizeDebounce.stop()
        settleShot.stop()
        inputQueue.clear()
        inputQueue.offerFirst("") // stops the input sender thread
    }

    // Seams for PreviewPanelWiringTest — the model is what "the list is empty" is actually about.
    internal fun targetCount() = targets.size
    internal fun targetAt(index: Int): String? = targets.getElementAt(index)
    internal fun statusText(): String = status.text
    internal fun selected(): String? = combo.selectedItem as? String
    internal fun select(target: String) { combo.selectedItem = target }
    internal fun refreshFromTest() = refreshTargets()
    internal fun selectDevice(device: Device) { devices.selectedItem = device }
    internal fun frame(): BufferedImage? = canvas.image
    internal fun canvasForTest(): Canvas = canvas
    internal fun localeVisible(): Boolean = localeCombo.isVisible
    internal fun selectedLocale(): String? = localeCombo.selectedItem as? String
    internal fun selectLocale(tag: String) { localeCombo.selectedItem = tag }
    internal fun selectLandscape(on: Boolean) { landscape.isSelected = on; applyDevice() }
    internal fun compactForTest(): Boolean = compactOn
    internal fun chooseCompactForTest(on: Boolean) = chooseCompact(on)
    internal fun widthChangedForTest(width: Int) { setSize(width, 600); autoCompact() }
    internal fun shownCountForTest(): Int = items.count { it.control.isVisible }
    internal fun captionShownForTest(): Boolean = widgetCaption.isVisible
    internal fun runButtonForTest(): JButton = runButton

    /** The rows the toolbar takes from the picture at a given panel width — what compact is for. */
    internal fun toolbarHeightForTest(width: Int): Int {
        toolbar.setSize(width, 1)
        return toolbar.preferredSize.height
    }

    // ── compact ───────────────────────────────────────────────────────────────

    /** Show what this mode keeps, hide the rest, and let [WrapLayout] re-wrap around it. */
    private fun applyToolbar() {
        compactToggle.icon = if (compactOn) AllIcons.General.ChevronDown else AllIcons.General.ChevronUp
        compactToggle.toolTipText =
            if (compactOn) "Show every preview control" else "Compact toolbar — keep the essentials"
        // The prototype is what bounds the combo's width, so compact has to shorten that too, or the
        // short names are drawn in a box still sized for a fully qualified one.
        combo.prototypeDisplayValue = if (compactOn) SHORT_NAME else FULL_NAME

        for (item in items) {
            val shown = item.available() && (!compactOn || item.essential)
            item.control.isVisible = shown
            // A caption is width spent saying what the control next to it already looks like; in a
            // compact row that is a whole control's worth, and the tooltip says the same thing.
            item.caption?.isVisible = shown && !compactOn
            item.shrink(compactOn)
        }

        toolbar.revalidate()
        toolbar.repaint()
    }

    /** The user's own choice, which from then on outranks the width rule. */
    private fun chooseCompact(on: Boolean) {
        compactChosen = true
        setCompact(on)
    }

    private fun setCompact(on: Boolean) {
        if (on == compactOn) return
        compactOn = on
        applyToolbar()
    }

    /**
     * Compact follows the panel's width until the toggle is pressed once.
     *
     * A tool window docked to a side is narrower than this toolbar however it is arranged, and every
     * row it wraps into is a row taken from the picture. Measured: the full toolbar needs ~1600 points
     * to fit in one row, so at a 420-point panel it wraps into five (127 px of the picture) where
     * compact takes two (58 px). Below [COMPACT_BELOW] it is three rows or more and the controls stop
     * being worth what they cost; above it, hiding anything gains at most a single row.
     *
     * The flag is the point of the rule: a mode that silently overrides what the user just clicked is
     * worse than no automatic mode at all.
     */
    private fun autoCompact() {
        if (compactChosen || width <= 0) return
        setCompact(width < COMPACT_BELOW)
    }

    private fun sync() {
        val open = session.port
        if (open == null) {
            status.text = session.state
            canvas.show(null, 1.0)
            targets.removeAllElements()
            locales.removeAllElements()
            applyToolbar()
            appliedPort = null
            stopLive()
            return
        }

        val reconnected = open != appliedPort
        appliedPort = open

        status.text = "port $open"
        refreshTargets()
        refreshLocales()

        // A new port is a new process — a `dotnet watch` restart after a rude edit, or a relaunch.
        // The app came back with its defaults, so push back what the panel had chosen; without this,
        // every watch restart silently dropped the previewed widget and showed the app's Home.
        if (reconnected) {
            selected()?.let { query(session, "preview $it", status) { shot() } }
            if (themeChosen) applyTheme()
            if (live.isSelected) startLive()
        }
    }

    /** Watch the app at animation rate; falls back to the 2 Hz poll against an older framework. */
    private fun startLive() {
        val port = session.port ?: return
        stopLive()
        // Fixed for the stream's life, so remember it: a later zoom or resize wanting a different
        // density has to restart the stream, and [retuneStream] compares against this.
        val want = canvas.captureScale()
        streamScale = want
        session.exec.background {
            // `stream` blocks for the stream's whole life; IOException just means it ended (the app
            // died or stopLive closed the socket) — only a false return means "server too old".
            val supported = runCatching {
                ZigoteInspect.stream(port, want, { streamSocket = it }) { bytes, granted ->
                    val img = ImageIO.read(ByteArrayInputStream(bytes))
                    if (img != null) session.exec.ui {
                        if (live.isSelected) {
                            canvas.show(img, granted)
                            status.text = "${(img.width / granted).toInt()}×${(img.height / granted).toInt()} live"
                        }
                    }
                }
            }.getOrDefault(true)
            session.exec.ui {
                streamSocket = null
                if (!supported && live.isSelected) timer.start()
            }
        }
    }

    private fun stopLive() {
        timer.stop()
        streamSocket?.close()
        streamSocket = null
    }

    /**
     * A stream captures at the density fixed when it opened, so zooming in — or growing the panel
     * under "Fit" — leaves it sending a picture coarser than the one being drawn, which is the blur
     * this whole path exists to avoid. Reopening is cheap (one socket) but not free, so only a
     * difference worth seeing does it: the window either side of 1 covers a viewport nudged by a few
     * pixels, where a restart would be visible and the gain would not.
     */
    private fun retuneStream() {
        if (streamSocket == null || !live.isSelected) return
        val want = canvas.captureScale()
        if (want < streamScale * 1.15 && want > streamScale * 0.6) return
        startLive()
    }

    private fun launch() {
        if (project == null) return
        val csproj = (projectCombo.selectedItem as? Proj)?.file ?: return noProject()
        session.launch(csproj, null)
    }

    private fun attach() {
        val entered = Messages.showInputDialog(
            project,
            "Inspect port of the running app (the port you passed as ZIGOTE_INSPECT):",
            "Attach to Zigote App",
            null,
        )?.trim()?.toIntOrNull() ?: return
        session.attach(entered)
    }

    private fun refreshTargets() {
        query(session, "targets", status) { reply ->
            val selected = combo.selectedItem as? String
            // Filling the model selects its first element, which would fire the listener and shove the
            // app onto whichever widget sorts first. Refreshing the list must not change what is shown.
            populating = true
            try {
                targets.removeAllElements()
                reply.strings("targets").forEach { targets.addElement(it) }
                if (selected != null && targets.getIndexOf(selected) >= 0) combo.selectedItem = selected
            } finally {
                populating = false
            }

            status.text = "${targets.size} widgets"
            applyDevice()
        }
    }

    /** Swap the app to the chosen widget — no restart, the socket says so and the app obeys. */
    private fun showSelected() {
        if (populating) return
        val target = combo.selectedItem as? String ?: return
        if (session.port == null) return
        query(session, "preview $target", status) { shot() }
    }

    private fun applyDevice() {
        if (session.port == null) return
        val chosen = devices.selectedItem as? Device ?: return
        // Landscape rotates the fixed presets; the adaptive ones have no orientation to rotate.
        val device = if (landscape.isSelected) Devices.rotate(chosen) else chosen
        val viewport = canvas.viewport
        query(session, Devices.command(device, viewport.width, viewport.height), status) { reply ->
            status.text = "${reply.int("w")}×${reply.int("h")}"
            shot()
        }
    }

    private fun applyTheme() {
        if (session.port == null) return
        val name = (theme.selectedItem as? String)?.lowercase() ?: return
        themeChosen = true
        query(session, "theme $name", status) { shot() }
    }

    /**
     * Fill the locale combo from the app, or hide it when the app has none. Not through [query]: an
     * app without a `LocalizationsScope` — or one built before the command existed — is the normal
     * case, and it must read as "no combo", never as an error in the status line.
     */
    private fun refreshLocales() {
        session.exec.background {
            val reply = runCatching { session.query("locales") }.getOrNull()
            val supported = reply?.strings("locales") ?: emptyList()
            val current = reply?.text("current")
            session.exec.ui {
                populating = true
                try {
                    locales.removeAllElements()
                    supported.forEach { locales.addElement(it) }
                    if (current != null && locales.getIndexOf(current) >= 0) localeCombo.selectedItem = current
                } finally {
                    populating = false
                }
                // The combo is only worth its width when the app has locales to offer; [applyToolbar]
                // reads that off the model it was just filled from.
                applyToolbar()

                // A restarted app woke up in its default locale; put the user's choice back.
                val want = localeChosen
                if (want != null && want != current && supported.contains(want)) {
                    populating = true
                    try {
                        localeCombo.selectedItem = want
                    } finally {
                        populating = false
                    }
                    query(session, "locale $want", status) { shot() }
                }
            }
        }
    }

    /** Swap the app's locale live — the same reactive path a settings screen would use. */
    private fun applyLocale() {
        if (populating || session.port == null) return
        val tag = localeCombo.selectedItem as? String ?: return
        localeChosen = tag
        query(session, "locale $tag", status) { shot() }
    }

    private fun shot() {
        if (session.port == null) return
        val want = canvas.captureScale()
        query(session, "shot ${ZigoteInspect.fmt(want)}", status) { reply ->
            val data = reply.text("data") ?: return@query
            // The density the app granted, not the one asked for: a server too old for `shot <scale>`
            // ignores the argument and answers at 1×, and drawing that as if it were 2× halves the
            // picture. `scale` is absent there, so the fallback is the honest one.
            val granted = (reply["scale"] as? Double)?.takeIf { it > 0 } ?: 1.0
            canvas.show(ImageIO.read(ByteArrayInputStream(Base64.getDecoder().decode(data))), granted)
            status.text = "${reply.int("w")}×${reply.int("h")}"
        }
    }

    private fun noProject() {
        Messages.showWarningDialog(
            project!!,
            "No runnable project in this solution — nothing declares <OutputType>Exe</OutputType>.",
            "Zigote",
        )
    }

    companion object {
        private const val FIT = "Fit"

        // What bounds the widget combo: a fully qualified name expanded, its last segment compacted.
        private const val FULL_NAME = "Some.Namespace.SomePage"
        private const val SHORT_NAME = "SomeWidgetPage"

        // Panel points. Wide enough that a bottom-docked tool window keeps every control, narrow
        // enough that a side-docked one — the shape this exists for — collapses.
        private const val COMPACT_BELOW = 700
    }
}

/**
 * Draws the last frame, plus the outline of whatever is selected in a tree tab.
 *
 * Implements [Scrollable] so "Fit" tracks the viewport (no scrollbars, image shrunk to fit) while a
 * fixed zoom does not (scrollbars appear and work). Sizing from the component's own bounds instead is
 * the circular definition that left the panel unscrollable.
 */
internal class Canvas(
    private val zoom: () -> String,
    private val session: ZigoteSession,
) : JComponent(), Scrollable {

    /** The scroll pane's visible extent, which "Fit" scales against. */
    var viewport: Dimension = Dimension(600, 400)

    /**
     * Receives wire-ready `input …` commands for everything done to the picture — press, drag,
     * wheel, keys, typed text. The canvas only maps coordinates; whether and how they are sent is
     * the panel's business.
     */
    var onInput: ((String) -> Unit)? = null

    var image: BufferedImage? = null
        private set

    /**
     * Image pixels per layout point — the density [image] was captured at. Every other number here is
     * in layout points (the space the app reports bounds in and takes input in), so this is divided
     * out in exactly one place, [pointSize], and nothing downstream has to know the picture is denser
     * than the coordinates.
     */
    var imageScale: Double = 1.0
        private set

    /** A new frame and the density it was captured at; the two must never be set apart. */
    fun show(frame: BufferedImage?, scale: Double) {
        image = frame
        imageScale = if (scale.isFinite() && scale > 0) scale else 1.0
        revalidate()
        repaint()
    }

    /**
     * The density the next capture should use: device pixels per layout point, for the size the
     * picture will actually be drawn at.
     *
     * The panel is drawn on the IDE's screen, so on a Retina MacBook one layout point covers two
     * device pixels — a 1× capture is a half-resolution image the compositor then has to enlarge,
     * which is what makes preview text look soft next to the same app in its own window. Asking for
     * more than the drawn size, on the other hand, is bytes over the socket and offscreen render work
     * in the app that lands in the same pixels: under "Fit" at half size, 1× is already exact.
     */
    fun captureScale(): Double {
        val drawn = image?.let { factor(it) } ?: 1.0
        return (deviceScale() * drawn).coerceIn(MIN_CAPTURE, MAX_CAPTURE)
    }

    /**
     * Device pixels per logical pixel for the screen this panel is on — AWT's own number rather than
     * the IDE's, because it is the one that decides whether the blit is 1:1. 1.0 with no peer yet
     * (headless tests, a panel built before it is shown), which is also the safe under-estimate.
     */
    private fun deviceScale(): Double =
        graphicsConfiguration?.defaultTransform?.scaleX?.takeIf { it > 0 } ?: 1.0

    /** The picture's size in layout points — what everything below measures against. */
    private fun pointSize(img: BufferedImage): Pair<Double, Double> =
        img.width / imageScale to img.height / imageScale

    init {
        // Keyboard goes to the app under preview, which needs two things: focus on the canvas
        // (clicking the picture grants it) and Tab not being eaten by Swing's own focus traversal.
        isFocusable = true
        setFocusTraversalKeysEnabled(false)

        val mouse = object : java.awt.event.MouseAdapter() {
            override fun mousePressed(e: java.awt.event.MouseEvent) {
                requestFocusInWindow()
                pointer(e, "down", button(e))
            }

            override fun mouseReleased(e: java.awt.event.MouseEvent) = pointer(e, "up", button(e))
            override fun mouseDragged(e: java.awt.event.MouseEvent) = pointer(e, "move", "")
            override fun mouseMoved(e: java.awt.event.MouseEvent) = pointer(e, "move", "")

            override fun mouseWheelMoved(e: java.awt.event.MouseWheelEvent) {
                val (ax, ay) = toApp(e.x, e.y) ?: return
                // AWT: positive rotation = towards the user; the app follows SDL, where +Y scrolls up.
                val ticks = -e.preciseWheelRotation.toFloat()
                val (dx, dy) = if (e.isShiftDown) ticks to 0f else 0f to ticks
                onInput?.invoke("input scroll ${fmt(ax)} ${fmt(ay)} ${fmt(dx)} ${fmt(dy)}")
            }
        }
        addMouseListener(mouse)
        addMouseMotionListener(mouse)
        addMouseWheelListener(mouse)

        addKeyListener(object : java.awt.event.KeyAdapter() {
            override fun keyPressed(e: java.awt.event.KeyEvent) {
                Keys.command(e, true)?.let { onInput?.invoke(it) }
            }

            override fun keyReleased(e: java.awt.event.KeyEvent) {
                Keys.command(e, false)?.let { onInput?.invoke(it) }
            }

            override fun keyTyped(e: java.awt.event.KeyEvent) {
                // Printable characters travel as committed text — the same split SDL makes natively
                // (keydown for the physical key, textinput for what it typed).
                val c = e.keyChar
                if (!c.isISOControl() && c != java.awt.event.KeyEvent.CHAR_UNDEFINED && !e.isControlDown && !e.isMetaDown)
                    onInput?.invoke("input text $c")
            }
        })
    }

    private fun pointer(e: java.awt.event.MouseEvent, verb: String, button: String) {
        // Presses must land on the picture; moves and releases clamp to it instead, so a drag that
        // slips off the edge still ends with its `up` — a lost release is a stuck pointer capture.
        val at = if (verb == "down") toApp(e.x, e.y) else toApp(e.x, e.y) ?: toAppClamped(e.x, e.y)
        val (ax, ay) = at ?: return
        val suffix = if (button.isEmpty()) "" else " $button"
        onInput?.invoke("input $verb ${fmt(ax)} ${fmt(ay)}$suffix")
    }

    private fun button(e: java.awt.event.MouseEvent): String = when (e.button) {
        java.awt.event.MouseEvent.BUTTON3 -> "right"
        java.awt.event.MouseEvent.BUTTON2 -> "middle"
        else -> "left"
    }

    private fun fmt(v: Float): String = String.format(java.util.Locale.ROOT, "%.1f", v)

    /** Where the image is drawn: scale factor and top-left corner, or null with no image. */
    private fun geometry(): Triple<Double, Int, Int>? {
        val img = image ?: return null
        val factor = factor(img)
        val (pw, ph) = pointSize(img)
        val w = (pw * factor).toInt()
        val h = (ph * factor).toInt()
        return Triple(factor, maxOf(0, (width - w) / 2), maxOf(0, (height - h) / 2))
    }

    /** Panel pixel → app layout point, or null outside the picture. */
    internal fun toApp(px: Int, py: Int): Pair<Float, Float>? {
        val img = image ?: return null
        val (factor, x0, y0) = geometry() ?: return null
        val (pw, ph) = pointSize(img)
        val ax = (px - x0) / factor
        val ay = (py - y0) / factor
        if (ax < 0 || ay < 0 || ax >= pw || ay >= ph) return null
        return ax.toFloat() to ay.toFloat()
    }

    /** Like [toApp] but clamped to the picture's edge — for drags that wander off it. */
    internal fun toAppClamped(px: Int, py: Int): Pair<Float, Float>? {
        val img = image ?: return null
        val (factor, x0, y0) = geometry() ?: return null
        val (pw, ph) = pointSize(img)
        val ax = ((px - x0) / factor).coerceIn(0.0, pw - 1.0)
        val ay = ((py - y0) / factor).coerceIn(0.0, ph - 1.0)
        return ax.toFloat() to ay.toFloat()
    }

    /** Drawn logical pixels per layout point — the zoom, in the panel's own coordinates. */
    private fun factor(img: BufferedImage): Double {
        val (pw, ph) = pointSize(img)
        return when (zoom()) {
            "200%" -> 2.0
            "100%" -> 1.0
            // Fit: never enlarge. A 400×300 widget blown up to fill a wide tab looks like a bug report.
            else -> minOf(
                1.0,
                viewport.width.toDouble() / pw,
                viewport.height.toDouble() / ph,
            ).coerceAtLeast(0.05)
        }
    }

    override fun getPreferredSize(): Dimension {
        val img = image ?: return Dimension(320, 240)
        val factor = factor(img)
        val (pw, ph) = pointSize(img)
        return Dimension((pw * factor).toInt(), (ph * factor).toInt())
    }

    override fun getPreferredScrollableViewportSize(): Dimension = preferredSize
    override fun getScrollableUnitIncrement(r: Rectangle, orientation: Int, direction: Int) = 16
    override fun getScrollableBlockIncrement(r: Rectangle, orientation: Int, direction: Int) = 160
    override fun getScrollableTracksViewportWidth() = zoom() == "Fit"
    override fun getScrollableTracksViewportHeight() = zoom() == "Fit"

    override fun paintComponent(g: Graphics) {
        val img = image ?: return drawEmptyState(g)
        val (factor, x, y) = geometry() ?: return
        val (pw, ph) = pointSize(img)
        val w = (pw * factor).toInt()
        val h = (ph * factor).toInt()

        val g2 = g as Graphics2D
        // Device pixels per image pixel. At 1 — a capture taken at the density it is drawn at, which
        // is what [captureScale] asks for — the blit is 1:1 and no filter runs at all. Bicubic is for
        // the frames either side of a zoom or resize, where a stale density is still on screen;
        // bilinear over a real downscale is the one that looks like a JPEG.
        val ratio = factor * deviceScale() / imageScale
        g2.setRenderingHint(
            RenderingHints.KEY_INTERPOLATION,
            if (ratio < 0.99) RenderingHints.VALUE_INTERPOLATION_BICUBIC
            else RenderingHints.VALUE_INTERPOLATION_BILINEAR,
        )
        g2.drawImage(img, x, y, w, h, null)

        session.highlight?.let { b ->
            g2.color = HIGHLIGHT
            g2.stroke = BasicStroke(2f)
            g2.drawRect(
                x + (b[0] * factor).toInt(),
                y + (b[1] * factor).toInt(),
                (b[2] * factor).toInt().coerceAtLeast(1),
                (b[3] * factor).toInt().coerceAtLeast(1),
            )
        }
    }

    /** A blank grey rectangle tells you nothing; this says which button starts a preview. */
    private fun drawEmptyState(g: Graphics) {
        val g2 = g as Graphics2D
        g2.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON)
        g2.color = foreground
        val lines = listOf(
            "No preview yet.",
            "Press \u201cRun app\u201d to start this project,",
            "or \u201cAttach\u2026\u201d if it is already running with ZIGOTE_INSPECT set.",
        )
        var y = maxOf(24, height / 2 - lines.size * 9)
        for (line in lines) {
            val w = g2.fontMetrics.stringWidth(line)
            g2.drawString(line, maxOf(8, (width - w) / 2), y)
            y += g2.fontMetrics.height + 4
        }
    }

    private companion object {
        val HIGHLIGHT: Color = Color(0x4A, 0x9E, 0xFF)

        // The app clamps to 0.1..4 anyway; these keep a shrunk "Fit" from asking for a picture too
        // coarse to read at all, and a 200% zoom on a Retina panel from asking for 4× of a desktop
        // window — which is a 30 MB frame per capture.
        const val MIN_CAPTURE = 0.5
        const val MAX_CAPTURE = 3.0
    }
}

// ── trees ─────────────────────────────────────────────────────────────────────

/**
 * One tree, whichever the app calls it: [command] is the socket command, [label] renders a node.
 *
 * A list of node names is not worth opening; what makes a tree useful is answering "which one is that
 * on screen" and "what is it set to". So selecting a node outlines it in the Preview tab and loads its
 * properties, and the filter box keeps a 300-node tree navigable.
 */
internal class TreePanel(
    private val session: ZigoteSession,
    private val command: String,
    private val label: (Map<String, Any?>) -> String,
) : JPanel(BorderLayout()) {

    private val model = DefaultTreeModel(DefaultMutableTreeNode("(not loaded)"))
    private val tree = Tree(model).apply {
        isRootVisible = true
        selectionModel.selectionMode = TreeSelectionModel.SINGLE_TREE_SELECTION
    }
    private val filter = JBTextField(14)
    private val details = DefaultTreeModel(DefaultMutableTreeNode("(no selection)"))
    private val detailsTree = Tree(details).apply { isRootVisible = false }
    private val status = JBLabel("")
    private var loaded: Map<String, Any?>? = null

    init {
        add(JPanel(FlowLayout(FlowLayout.LEFT, 4, 2)).apply {
            add(JButton("Refresh").apply { addActionListener { refresh() } })
            add(JBLabel("Filter:"))
            add(filter)
            add(status)
        }, BorderLayout.NORTH)

        add(
            JSplitPane(JSplitPane.VERTICAL_SPLIT, JBScrollPane(tree), JBScrollPane(detailsTree))
                .apply { resizeWeight = 0.65 },
            BorderLayout.CENTER,
        )

        filter.document.addDocumentListener(object : DocumentListener {
            override fun insertUpdate(e: DocumentEvent) = rebuild()
            override fun removeUpdate(e: DocumentEvent) = rebuild()
            override fun changedUpdate(e: DocumentEvent) = rebuild()
        })

        tree.addTreeSelectionListener { selected() }

        session.onChanged { if (session.port != null) refresh() }
        if (session.port != null) refresh()
    }

    internal fun statusText(): String = status.text
    internal fun nodeCount(): Int = countRows(model.root as DefaultMutableTreeNode)
    internal fun rootLabel(): String = (model.root as DefaultMutableTreeNode).userObject.toString()

    private fun countRows(node: DefaultMutableTreeNode): Int =
        1 + (0 until node.childCount).sumOf { countRows(node.getChildAt(it) as DefaultMutableTreeNode) }

    private fun refresh() {
        query(session, command, status) { reply ->
            loaded = reply.node("tree")
            rebuild()
        }
    }

    private fun rebuild() {
        val root = loaded
        if (root == null) {
            model.setRoot(DefaultMutableTreeNode("(empty)"))
            return
        }

        val needle = filter.text.trim().lowercase()
        model.setRoot(build(root, needle) ?: DefaultMutableTreeNode("(nothing matches '$needle')"))
        // Two levels when browsing; a filtered tree is small, so open all of it.
        repeat(if (needle.isEmpty()) 2 else 12) {
            for (row in tree.rowCount - 1 downTo 0) tree.expandRow(row)
        }
        status.text = "${countRows(model.root as DefaultMutableTreeNode)} nodes"
    }

    /** Keeps a node when it matches, or when a descendant does — a match with no path to it is noise. */
    private fun build(node: Map<String, Any?>, needle: String): DefaultMutableTreeNode? {
        val kept = node.children().mapNotNull { build(it, needle) }
        val text = label(node)
        if (needle.isNotEmpty() && kept.isEmpty() && !text.lowercase().contains(needle)) return null

        val branch = DefaultMutableTreeNode(Node(text, node))
        kept.forEach(branch::add)
        return branch
    }

    private fun selected() {
        val node = (tree.lastSelectedPathComponent as? DefaultMutableTreeNode)?.userObject as? Node
        if (node == null) {
            session.highlight(null)
            return
        }

        val source = node.source
        session.highlight(
            floatArrayOf(
                source.int("x").toFloat(),
                source.int("y").toFloat(),
                source.int("w").toFloat(),
                source.int("h").toFloat(),
            )
        )
        showDetails(source)
    }

    /** Bounds and role come with the tree; the rest is one `props` round trip for widgets only. */
    private fun showDetails(source: Map<String, Any?>) {
        val root = DefaultMutableTreeNode("props")
        for (key in listOf("type", "role", "label", "value", "hint", "flags", "actions")) {
            source.text(key)?.let { root.add(DefaultMutableTreeNode("$key = $it")) }
        }
        root.add(DefaultMutableTreeNode("bounds = ${source.int("x")}, ${source.int("y")}  ${source.int("w")}×${source.int("h")}"))
        details.setRoot(root)
        expandDetails()

        if (command != "widgets") return
        val id = (source["id"] as? Double)?.toInt() ?: return
        query(session, "props $id", status) { reply ->
            @Suppress("UNCHECKED_CAST")
            val props = reply["props"] as? Map<String, Any?> ?: return@query
            props.forEach { (k, v) -> root.add(DefaultMutableTreeNode("$k = $v")) }
            details.reload()
            expandDetails()
        }
    }

    private fun expandDetails() {
        for (row in detailsTree.rowCount - 1 downTo 0) detailsTree.expandRow(row)
    }

    /** Carries the raw node so selection can highlight and drill into it, while rendering as a label. */
    private class Node(private val text: String, val source: Map<String, Any?>) {
        override fun toString() = text
    }
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
private fun query(
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

internal fun widgetLabel(node: Map<String, Any?>): String {
    val type = node.text("type") ?: "?"
    val size = "${node.int("w")}×${node.int("h")}"
    val desc = node.text("desc")
    return if (desc.isNullOrBlank()) "$type  $size" else "$type  $desc  $size"
}

internal fun semanticsLabel(node: Map<String, Any?>): String {
    val role = node.text("role") ?: "?"
    val size = "${node.int("w")}×${node.int("h")}"
    val label = node.text("label")
    return if (label.isNullOrBlank()) "$role  $size" else "$role  \"$label\"  $size"
}
