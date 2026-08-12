package dev.zigote.rider

import com.intellij.testFramework.ApplicationRule
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.ClassRule
import org.junit.Test
import java.awt.Component
import java.awt.event.KeyEvent
import java.awt.event.MouseEvent
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.net.ServerSocket
import java.net.Socket
import java.util.Collections
import kotlin.concurrent.thread

/**
 * Interactivity and streaming, checked at the wire: what reaches the fake app when the picture is
 * clicked, typed at, scrolled — and that the frame stream parses what the server frames.
 */
class InteractivityTest {

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
                        command == "locales" -> """{"current":null,"locales":[]}"""
                        command.startsWith("shot") -> SHOT
                        command.startsWith("stream") -> """{"error":"unknown command 'stream'"}"""
                        else -> """{"ok":true,"w":8,"h":8}"""
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

    private fun waitFor(what: String, condition: () -> Boolean) {
        val deadline = System.currentTimeMillis() + 5_000
        while (System.currentTimeMillis() < deadline) {
            if (condition()) return
            Thread.sleep(20)
        }
        throw AssertionError("timed out waiting for $what — sent: $sent")
    }

    // ── canvas → wire ─────────────────────────────────────────────────────────

    @Test
    fun `clicking the picture sends a press and a release at app coordinates`() {
        val panel = panel()
        try {
            val canvas = panel.canvasForTest()
            sent.clear()
            canvas.dispatchEvent(mouse(canvas, MouseEvent.MOUSE_PRESSED, 0, 0, MouseEvent.BUTTON1))
            canvas.dispatchEvent(mouse(canvas, MouseEvent.MOUSE_RELEASED, 0, 0, MouseEvent.BUTTON1))
            waitFor("down+up") {
                sent.contains("input down 0.0 0.0 left") && sent.contains("input up 0.0 0.0 left")
            }
            // Ordering is the contract: the press must reach the app before the release.
            assertTrue(sent.indexOf("input down 0.0 0.0 left") < sent.indexOf("input up 0.0 0.0 left"))
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `typing sends the physical key and the text it produced`() {
        val panel = panel()
        try {
            val canvas = panel.canvasForTest()
            sent.clear()
            // Straight to the listeners: dispatchEvent would hand key events to the focus manager,
            // which redirects them to the focus owner — and a headless test has none.
            canvas.keyListeners.forEach { it.keyPressed(key(canvas, KeyEvent.KEY_PRESSED, KeyEvent.VK_A, 'a')) }
            canvas.keyListeners.forEach { it.keyTyped(key(canvas, KeyEvent.KEY_TYPED, KeyEvent.VK_UNDEFINED, 'a')) }
            canvas.keyListeners.forEach { it.keyReleased(key(canvas, KeyEvent.KEY_RELEASED, KeyEvent.VK_A, 'a')) }
            waitFor("key + text") {
                sent.contains("input keydown A") && sent.contains("input text a") &&
                    sent.contains("input keyup A")
            }
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `presses outside the picture are not sent`() {
        val panel = panel()
        try {
            val canvas = panel.canvasForTest()
            sent.clear()
            // The test image is 1×1 at the canvas origin; (300, 300) is empty panel.
            canvas.dispatchEvent(mouse(canvas, MouseEvent.MOUSE_PRESSED, 300, 300, MouseEvent.BUTTON1))
            Thread.sleep(150)
            assertFalse("sent: $sent", sent.any { it.startsWith("input down") })
        } finally {
            panel.dispose()
        }
    }

    // ── coordinate mapping ────────────────────────────────────────────────────

    @Test
    fun `mapping is inverse of the zoom factor and clamping stays on the picture`() {
        val session = ZigoteSession(null).apply { exec = Exec.Inline }
        val canvas = Canvas({ "100%" }, session)
        canvas.image = java.awt.image.BufferedImage(100, 50, java.awt.image.BufferedImage.TYPE_INT_RGB)
        canvas.setSize(100, 50)

        assertEquals(25f to 10f, canvas.toApp(25, 10))
        assertNull(canvas.toApp(150, 10)) // off the picture
        assertEquals(99f to 10f, canvas.toAppClamped(150, 10)) // a drag that slipped off
    }

    // ── the frame stream ──────────────────────────────────────────────────────

    @Test
    fun `the stream parses length-prefixed frames until the server closes`() {
        val socket = ServerSocket(0)
        server = socket
        thread(isDaemon = true) {
            val client = socket.accept()
            client.getInputStream().bufferedReader().readLine() // the `stream` command
            val out = DataOutputStream(client.getOutputStream())
            out.write("{\"format\":\"bmp\",\"stream\":true}\n".toByteArray())
            for (payload in listOf(byteArrayOf(1, 2, 3), byteArrayOf(4, 5))) {
                out.writeInt(payload.size)
                out.write(payload)
            }
            out.flush()
            client.close()
        }

        val frames = mutableListOf<ByteArray>()
        val ended = runCatching {
            ZigoteInspect.stream(socket.localPort, {}) { frames += it }
        }
        // The server closing mid-read surfaces as an IOException — that is "ended", not "unsupported".
        assertTrue(ended.isFailure || ended.getOrNull() == true)
        assertEquals(2, frames.size)
        assertTrue(frames[0].contentEquals(byteArrayOf(1, 2, 3)))
        assertTrue(frames[1].contentEquals(byteArrayOf(4, 5)))
    }

    @Test
    fun `an old server without stream reports unsupported instead of hanging`() {
        val socket = ServerSocket(0)
        server = socket
        thread(isDaemon = true) {
            val client = socket.accept()
            client.getInputStream().bufferedReader().readLine()
            client.getOutputStream().write("{\"error\":\"unknown command 'stream'\"}\n".toByteArray())
            client.getOutputStream().flush()
            client.close()
        }

        assertFalse(ZigoteInspect.stream(socket.localPort, {}) { })
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private fun mouse(source: Component, id: Int, x: Int, y: Int, button: Int) =
        MouseEvent(source, id, System.currentTimeMillis(), 0, x, y, 1, false, button)

    private fun key(source: Component, id: Int, code: Int, char: Char) =
        KeyEvent(source, id, System.currentTimeMillis(), 0, code, char)

    companion object {
        @ClassRule
        @JvmField
        val application = ApplicationRule()

        // 1×1 24-bit BMP so the canvas has a real picture at the origin.
        private const val SHOT =
            """{"format":"bmp","w":1,"h":1,"scale":1,"data":"Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAA////AA=="}"""
    }
}
