package dev.zigote.rider

/**
 * The sizes a preview can be laid out at.
 *
 * Logical (density-independent) points, because that is what the framework lays out in — a Pixel 8 is
 * 412×915 points, not its 1080×2400 pixels. Getting that wrong would show a phone-shaped desktop
 * layout, which is exactly the mistake a device preview exists to catch.
 */
internal data class Device(
    val label: String,
    val width: Int,
    val height: Int,
    /** Added to the list by a `[Preview(Width, Height)]`, and removed again with it. */
    val fromAnnotation: Boolean = false,
) {
    override fun toString() = label
}

internal object Devices {

    /** Follow the tool window: the app re-lays out as the panel is resized. The developing default. */
    val PANEL = Device("Panel (adapt)", 0, 0)

    /** Whatever the app's own window is — the size it would run at normally. */
    val WINDOW = Device("App window", -1, -1)

    val all: List<Device> = listOf(
        PANEL,
        WINDOW,
        Device("iPhone SE", 375, 667),
        Device("iPhone 15", 393, 852),
        Device("iPhone 15 Pro Max", 430, 932),
        Device("Pixel 8", 412, 915),
        Device("Pixel 8 Pro", 448, 998),
        Device("Galaxy S24", 360, 780),
        Device("iPad mini", 744, 1133),
        Device("iPad Pro 11\"", 834, 1194),
        Device("Android tablet", 800, 1280),
        Device("Desktop 1280×800", 1280, 800),
        Device("Desktop 1440×900", 1440, 900),
        Device("Desktop 1920×1080", 1920, 1080),
    )

    /** The `size` argument for a device, given the panel's current viewport for [PANEL]. */
    fun command(device: Device, panelWidth: Int, panelHeight: Int): String = when (device) {
        WINDOW -> "size window"
        PANEL -> "size ${panelWidth.coerceAtLeast(120)}x${panelHeight.coerceAtLeast(120)}"
        else -> "size ${device.width}x${device.height}"
    }

    /** Landscape twin of a device; the app window and the panel have no fixed orientation. */
    fun rotate(device: Device): Device = when (device) {
        PANEL, WINDOW -> device
        else -> Device(device.label, device.height, device.width)
    }
}
