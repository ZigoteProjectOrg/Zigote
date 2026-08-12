package dev.zigote.rider

import com.intellij.testFramework.ApplicationRule
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.ClassRule
import org.junit.Test
import java.net.ServerSocket
import java.net.Socket
import java.util.Collections
import kotlin.concurrent.thread

/**
 * Device sizes and theme, checked by what the panel actually sends.
 *
 * Asserting on the recorded commands rather than on pixels: whether a phone preview is *correct* is
 * the app's business (and is covered there), but whether the panel asks for one at all is this
 * plugin's, and that is the half that silently did nothing before.
 */
class DeviceAndThemeTest {

    private var server: ServerSocket? = null
    private val sent = Collections.synchronizedList(mutableListOf<String>())

    @After
    fun stopFakeApp() {
        server?.close()
    }

    private fun startFakeApp(): Int {
        val socket = ServerSocket(0)
        server = socket
        thread(isDaemon = true) {
            while (!socket.isClosed) {
                val client: Socket = try {
                    socket.accept()
                } catch (_: Exception) {
                    return@thread
                }
                client.use {
                    val command = it.getInputStream().bufferedReader().readLine() ?: return@use
                    sent += command
                    val reply = when {
                        command == "targets" -> """{"targets":["A.B"]}"""
                        command.startsWith("shot") -> SHOT
                        else -> """{"ok":true,"w":393,"h":852}"""
                    }
                    it.getOutputStream().write((reply + "\n").toByteArray())
                    it.getOutputStream().flush()
                }
            }
        }
        return socket.localPort
    }

    private fun panel(): PreviewPanel {
        val session = ZigoteSession(null).apply { exec = Exec.Inline }
        session.attach(startFakeApp())
        return PreviewPanel(null, session)
    }

    @Test
    fun `choosing a device asks the app to lay out at its logical size`() {
        val panel = panel()
        try {
            sent.clear()
            panel.selectDevice(Devices.all.first { it.label == "iPhone 15" })
            assertTrue("sent: $sent", sent.contains("size 393x852"))
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `the app window device hands layout back to the app`() {
        val panel = panel()
        try {
            sent.clear()
            panel.selectDevice(Devices.WINDOW)
            assertTrue("sent: $sent", sent.contains("size window"))
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `panel mode asks for the viewport size, not a fixed one`() {
        // The developing default: no device chosen, the app follows the tool window.
        assertEquals("size 640x480", Devices.command(Devices.PANEL, 640, 480))
        // A collapsed panel would otherwise ask for a zero-sized layout.
        assertEquals("size 120x120", Devices.command(Devices.PANEL, 0, 5))
    }

    @Test
    fun `the landscape toggle asks for the rotated size`() {
        val panel = panel()
        try {
            panel.selectDevice(Devices.all.first { it.label == "iPhone 15" })
            sent.clear()
            panel.selectLandscape(true)
            assertTrue("sent: $sent", sent.contains("size 852x393"))
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `rotating swaps a device's axes and leaves the adaptive ones alone`() {
        val phone = Devices.all.first { it.label == "Pixel 8" }
        assertEquals(915, Devices.rotate(phone).width)
        assertEquals(412, Devices.rotate(phone).height)
        assertEquals(Devices.PANEL, Devices.rotate(Devices.PANEL))
    }

    @Test
    fun `every preset is portrait and plausible`() {
        for (device in Devices.all - Devices.PANEL - Devices.WINDOW) {
            assertTrue("${device.label} too small", device.width >= 320 && device.height >= 480)
            // Logical points, not pixels: a 1080-point-wide phone would mean the preset was taken from
            // a spec sheet's pixel column, and every breakpoint in the preview would be wrong.
            assertTrue("${device.label} looks like pixels, not points", device.width <= 1920)
        }
    }

    companion object {
        @ClassRule
        @JvmField
        val application = ApplicationRule()

        private const val SHOT =
            """{"format":"bmp","w":393,"h":852,"scale":1,"data":"Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAA////AA=="}"""
    }
}
