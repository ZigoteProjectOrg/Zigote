package dev.zigote.rider

import java.awt.BasicStroke
import java.awt.Color
import java.awt.Dimension
import java.awt.Graphics
import java.awt.Graphics2D
import java.awt.Rectangle
import java.awt.RenderingHints
import java.awt.image.BufferedImage
import javax.swing.JComponent
import javax.swing.Scrollable

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
            "Press “Run app” to start this project,",
            "or “Attach…” if it is already running with ZIGOTE_INSPECT set.",
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
