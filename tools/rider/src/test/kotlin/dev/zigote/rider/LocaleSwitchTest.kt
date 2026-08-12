package dev.zigote.rider

import com.intellij.testFramework.ApplicationRule
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.ClassRule
import org.junit.Test
import java.net.ServerSocket
import java.net.Socket
import java.util.Collections
import kotlin.concurrent.thread

/**
 * The locale combo, checked by what the panel sends — same reasoning as [DeviceAndThemeTest]: whether
 * a locale switch re-renders correctly is the app's business, whether the panel asks for one is ours.
 */
class LocaleSwitchTest {

    private var server: ServerSocket? = null
    private val sent = Collections.synchronizedList(mutableListOf<String>())

    @After
    fun stopFakeApp() {
        server?.close()
    }

    private fun startFakeApp(localesReply: String): Int {
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
                        command == "locales" -> localesReply
                        command.startsWith("shot") -> SHOT
                        else -> """{"ok":true,"w":8,"h":8}"""
                    }
                    it.getOutputStream().write((reply + "\n").toByteArray())
                    it.getOutputStream().flush()
                }
            }
        }
        return socket.localPort
    }

    private fun panel(localesReply: String): PreviewPanel {
        val session = ZigoteSession(null).apply { exec = Exec.Inline }
        session.attach(startFakeApp(localesReply))
        return PreviewPanel(null, session)
    }

    @Test
    fun `a localized app fills the combo and shows its active locale`() {
        val panel = panel("""{"current":"es","locales":["en","es","ar"]}""")
        try {
            assertTrue(panel.localeVisible())
            assertEquals("es", panel.selectedLocale())
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `choosing a locale asks the app to switch to it`() {
        val panel = panel("""{"current":"en","locales":["en","es"]}""")
        try {
            sent.clear()
            panel.selectLocale("es")
            assertTrue("sent: $sent", sent.contains("locale es"))
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `an app without localization hides the combo instead of erroring`() {
        val panel = panel("""{"current":null,"locales":[]}""")
        try {
            assertFalse(panel.localeVisible())
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `a framework build predating the command reads as no localization, not as broken`() {
        val panel = panel("""{"error":"unknown command 'locales'"}""")
        try {
            assertFalse(panel.localeVisible())
            // The status ends on the shot's size — the error never reached it.
            assertEquals("1×1", panel.statusText())
        } finally {
            panel.dispose()
        }
    }

    companion object {
        @ClassRule
        @JvmField
        val application = ApplicationRule()

        private const val SHOT =
            """{"format":"bmp","w":1,"h":1,"scale":1,"data":"Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAA////AA=="}"""
    }
}
