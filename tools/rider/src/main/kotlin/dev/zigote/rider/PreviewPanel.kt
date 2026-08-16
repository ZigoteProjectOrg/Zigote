package dev.zigote.rider

import com.intellij.icons.AllIcons
import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.Disposable
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.ComboBox
import com.intellij.openapi.ui.Messages
import com.intellij.ui.components.JBCheckBox
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBScrollPane
import java.awt.BorderLayout
import java.awt.Component
import java.awt.Dimension
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
import javax.swing.Timer

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
 *
 * What there *is* to show comes from [ZigoteSession.targets] rather than being fetched here — the
 * editor gutter marks previewable declarations from the same list, and two fetchers would disagree
 * for as long as one of them was stale.
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

    private val targets = DefaultComboBoxModel<PreviewTarget>()
    private val combo = ComboBox(targets).apply {
        // Bounded, or one long type name ("AdwaitaGallery.Pages.ImageGridPage") sets the toolbar's
        // width and pushes the buttons out of a docked panel.
        prototypeDisplayValue = PreviewTarget(FULL_NAME)
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
                val item = value as? PreviewTarget
                // The group is a heading with nowhere to be one in a combo box, so it rides in front
                // of the name — and is the first thing dropped when there is no width for it.
                val shown = if (compactOn) item?.display?.substringAfterLast('.')
                else item?.group?.let { "$it / ${item.display}" } ?: item?.display
                return super.getListCellRendererComponent(list, shown, index, selected, focused)
            }
        }
    }

    /** Held rather than read off the combo: [applyAnnotation] adds and removes an entry. */
    private val deviceModel = DefaultComboBoxModel(Devices.all.toTypedArray())
    private val devices = ComboBox(deviceModel)
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

    /** The previewed widget's own properties; hides itself when the widget has none. */
    private val props = PropertiesPanel { showSelected() }
    private var populating = false

    private val runButton = JButton("Run app").apply { addActionListener { launch() } }
    private val stopButton = JButton("Stop").apply { addActionListener { session.stop() } }
    private val attachButton = JButton("Attach…").apply { addActionListener { attach() } }
    private val refreshButton = JButton("Refresh").apply { addActionListener { refresh() } }
    private val projectCaption = JBLabel("Project:")
    private val widgetCaption = JBLabel("Widget:")
    private val deviceCaption = JBLabel("Device:")
    private val themeCaption = JBLabel("Theme:")
    private val zoomCaption = JBLabel("Zoom:")

    /** Collapses the toolbar to the essentials, and back. Icon-only, because width is its whole point. */
    private val compactToggle = JButton().apply { addActionListener { chooseCompact(!compactOn) } }

    private val toolbar = JPanel(WrapLayout())

    /** Dropped on [dispose]: a listener held by a dead panel is a leak that also calls into it. */
    private val subscriptions = mutableListOf<Unsubscribe>()

    /**
     * Where the toolbar's choices survive an IDE restart. Picking the phone you are building for, the
     * theme and the zoom is a decision about the *work*, not about this session of it, and making it
     * again every morning is the kind of small tax a tool is judged by.
     *
     * Null with no project — the panel tests build one that way, and a test that wrote to the real
     * settings store would leak into the next one.
     */
    private val settings = project?.let { PropertiesComponent.getInstance(it) }

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

    /** The [ZigoteSession.requestSeq] this panel has already moved its combo to. */
    private var appliedRequest = 0

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
            // The full type name, wherever the combo is only showing the tail of it — or a label.
            combo.toolTipText = current()?.target
            chose()
        }
        devices.addActionListener { remember(); applyDevice() }
        landscape.addActionListener { remember(); applyDevice() }
        theme.addActionListener { remember(); applyTheme() }
        localeCombo.addActionListener { applyLocale() }
        // Zoom changes what the picture is drawn at, and so what density is worth capturing: at 200%
        // a 1× frame is enlarged four-fold on a Retina panel. Live re-opens at the new density; the
        // still path picks it up on its next shot anyway.
        zoom.addActionListener {
            remember(); canvas.revalidate(); canvas.repaint(); retuneStream()
            if (streamSocket == null) shot()
        }
        live.addActionListener { if (live.isSelected) startLive() else stopLive() }
        hotReload.addActionListener { session.hotReload = hotReload.isSelected; remember() }

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

        // Under the picture, not in the toolbar: a property editor is as tall as the widget has
        // properties, and the toolbar is a row of things that are all one line high.
        add(props, BorderLayout.SOUTH)

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

        subscriptions += session.onChanged { sync() }
        subscriptions += session.onTargets { fill() }
        subscriptions += session.onHighlight { canvas.repaint() }
        restore()
        applyToolbar()
        sync()
        fill()
    }

    override fun dispose() {
        subscriptions.forEach { it() }
        subscriptions.clear()
        stopLive()
        resizeDebounce.stop()
        settleShot.stop()
        props.dispose()
        inputQueue.clear()
        inputQueue.offerFirst("") // stops the input sender thread
    }

    // Seams for the panel tests — the model is what "the list is empty" is actually about.
    internal fun targetCount() = targets.size
    internal fun targetAt(index: Int): String? = targets.getElementAt(index)?.target
    internal fun labelAt(index: Int): String? = targets.getElementAt(index)?.display
    internal fun statusText(): String = status.text
    internal fun selected(): String? = current()?.target
    internal fun select(target: String) {
        rows().firstOrNull { it.target == target }?.let { combo.selectedItem = it }
    }

    internal fun propsForTest(): PropertiesPanel = props
    internal fun deviceForTest(): Device? = devices.selectedItem as? Device
    internal fun themeForTest(): String? = theme.selectedItem as? String
    internal fun refreshFromTest() = refresh()
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
        combo.prototypeDisplayValue = PreviewTarget(if (compactOn) SHORT_NAME else FULL_NAME)

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
        remember()
    }

    // ── remembered between sessions ───────────────────────────────────────────

    /**
     * Put back what was chosen last time. Through the controls, so every listener runs exactly as if
     * it had just been picked — and they all no-op while no app is connected, which is the state a
     * panel is built in.
     */
    private fun restore() {
        val saved = settings ?: return
        saved.getValue(DEVICE)
            ?.let { label -> Devices.all.firstOrNull { it.label == label } }
            ?.let { devices.selectedItem = it }
        landscape.isSelected = saved.getBoolean(LANDSCAPE, false)
        saved.getValue(THEME)?.takeIf { it == "Dark" || it == "Light" }?.let { theme.selectedItem = it }
        saved.getValue(ZOOM)?.let { zoom.selectedItem = it }
        hotReload.isSelected = saved.getBoolean(HOT_RELOAD, false)
        session.hotReload = hotReload.isSelected
        // Only if it was ever pressed: otherwise the width rule is still the one deciding.
        if (saved.isValueSet(COMPACT)) {
            compactChosen = true
            setCompact(saved.getBoolean(COMPACT, false))
        }
    }

    /**
     * A widget's `[Preview]` is not the developer's preference. It moves the same controls, so
     * without this the first annotated widget of the morning would quietly become the theme and size
     * every project opens in.
     */
    private var annotating = false

    private fun applyingAnnotation(work: () -> Unit) {
        annotating = true
        try {
            work()
        } finally {
            annotating = false
        }
    }

    private fun remember() {
        if (annotating) return
        val saved = settings ?: return
        // Not the annotated entry: it belongs to a widget, not to a preference, and it will not be in
        // the list next time.
        (devices.selectedItem as? Device)?.takeIf { !it.fromAnnotation }
            ?.let { saved.setValue(DEVICE, it.label) }
        saved.setValue(LANDSCAPE, landscape.isSelected)
        (theme.selectedItem as? String)?.let { saved.setValue(THEME, it) }
        (zoom.selectedItem as? String)?.let { saved.setValue(ZOOM, it) }
        saved.setValue(HOT_RELOAD, hotReload.isSelected)
        if (compactChosen) saved.setValue(COMPACT, compactOn)
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

    // ── the app ───────────────────────────────────────────────────────────────

    private fun sync() {
        val open = session.port
        if (open == null) {
            status.text = session.state
            canvas.show(null, 1.0)
            locales.removeAllElements()
            applyToolbar()
            appliedPort = null
            stopLive()
            return
        }

        val reconnected = open != appliedPort
        appliedPort = open

        status.text = "port $open"
        refreshLocales()

        // A new port is a new process — a `dotnet watch` restart after a rude edit, or a relaunch.
        // The app came back with its defaults, so push back what the panel had chosen; without this,
        // every watch restart silently dropped the previewed widget and showed the app's Home.
        if (reconnected) {
            pushPreview()
            if (themeChosen) applyTheme()
            if (live.isSelected) startLive()
        }

        followRequest()
    }

    /** Everything the panel reads afresh, for when it was opened after the app was already up. */
    private fun refresh() {
        session.refreshTargets()
        refreshLocales()
        shot()
    }

    private fun rows(): List<PreviewTarget> = (0 until targets.size).map { targets.getElementAt(it) }

    private fun current(): PreviewTarget? = combo.selectedItem as? PreviewTarget

    /** The session's list, into the combo. Never changes what is *shown* — see [followRequest]. */
    private fun fill() {
        val found = session.targets
        val keep = current()?.target
        // Filling the model selects its first element, which would fire the listener and shove the
        // app onto whichever widget sorts first. Refreshing the list must not change what is shown.
        populating = true
        try {
            targets.removeAllElements()
            found.forEach { targets.addElement(it) }
            found.firstOrNull { it.target == keep }?.let { combo.selectedItem = it }
        } finally {
            populating = false
        }

        if (found.isEmpty()) {
            props.show(null)
            return
        }

        status.text = "${found.size} widgets"
        // The reselected element is a new object — after a reload its defaults may have changed with
        // the source. What was typed into the editor survives; see [PropertiesPanel.show].
        props.show(current())
        followRequest()
        applyDevice()
    }

    /**
     * A widget was asked for from outside the panel — the editor gutter, the preview action. The
     * session already told the app; this moves the combo to match, because a picture of one widget
     * over a dropdown naming another is the panel lying about what it is showing.
     */
    private fun followRequest() {
        val seq = session.requestSeq
        if (seq == appliedRequest) return
        val row = rows().firstOrNull { it.target == session.requested } ?: return
        appliedRequest = seq

        populating = true
        try {
            combo.selectedItem = row
        } finally {
            populating = false
        }
        combo.toolTipText = row.target
        props.show(row)
        applyAnnotation(row)

        // The session asked for the bare target. If this panel is holding edited properties for it —
        // the same widget, asked for again — they are what it says it is showing, so send them.
        val spec = Previews.spec(row, props.values())
        if (spec == row.target) shot() else query(session, "preview $spec", status) { shot() }
    }

    /** A widget was chosen here: its properties, the size and theme it asked for, then the widget. */
    private fun chose() {
        if (populating) return
        val target = current() ?: return
        props.show(target)
        applyAnnotation(target)
        showSelected()
    }

    /**
     * `[Preview(Width = …, Height = …, Theme = "dark")]` is the author saying what this widget is
     * meant to be looked at as — a phone page previewed at desktop width is the mistake the annotation
     * exists to prevent.
     *
     * Applied *through* the combos rather than behind them, so the toolbar keeps saying what the app
     * is actually laid out at, and so the next widget can be looked at the same way with one click.
     */
    private fun applyAnnotation(target: PreviewTarget) = applyingAnnotation {
        val stale = (0 until deviceModel.size).map { deviceModel.getElementAt(it) }
            .filter { it.fromAnnotation }
        if (target.width > 0 && target.height > 0) {
            val asked = Device(
                "${target.width}×${target.height} (annotated)",
                target.width,
                target.height,
                fromAnnotation = true,
            )
            deviceModel.insertElementAt(asked, 0)
            deviceModel.selectedItem = asked
        }
        // After, not before: removing the selected entry first would move the selection somewhere
        // arbitrary and lay the app out at it on the way past.
        stale.forEach { deviceModel.removeElement(it) }

        val wanted = target.theme?.let { if (it.equals("light", ignoreCase = true)) "Light" else "Dark" }
        if (wanted != null && theme.selectedItem != wanted) theme.selectedItem = wanted
    }

    /** Swap the app to the chosen widget — no restart, the socket says so and the app obeys. */
    private fun showSelected() {
        if (populating) return
        pushPreview()
    }

    private fun pushPreview() {
        val target = current() ?: return
        if (session.port == null) return
        query(session, "preview ${Previews.spec(target, props.values())}", status) { shot() }
    }

    // ── frames ────────────────────────────────────────────────────────────────

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

    // ── the rest of the toolbar ───────────────────────────────────────────────

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

        // Remembered per project. Live is deliberately not among them: it costs the app frames, and
        // starting a stream nobody asked for on the morning's first launch is a surprise.
        private const val DEVICE = "zigote.preview.device"
        private const val LANDSCAPE = "zigote.preview.landscape"
        private const val THEME = "zigote.preview.theme"
        private const val ZOOM = "zigote.preview.zoom"
        private const val HOT_RELOAD = "zigote.preview.hotReload"
        private const val COMPACT = "zigote.preview.compact"
    }
}
