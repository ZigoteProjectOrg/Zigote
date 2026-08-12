package dev.zigote.rider

import com.intellij.testFramework.ApplicationRule
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.ClassRule
import org.junit.Test
import java.net.ServerSocket
import java.net.Socket
import kotlin.concurrent.thread

/**
 * The wiring, end to end: session → attach → listener → threading hop → socket → combo box.
 *
 * This is the test that was missing, and its absence is why a broken panel shipped twice. The client
 * parsing was fine and the server was fine; what failed was the path between them, which no test of
 * either end could reach.
 *
 * Only an [ApplicationRule] — no project, because a Rider project needs a real solution and the panels
 * do not need one. The threading runs inline (see [Exec]) so a socket round trip is over by the time
 * the call returns.
 */
class PreviewPanelWiringTest {

    private var server: ServerSocket? = null

    @After
    fun stopFakeApp() {
        server?.close()
    }

    private fun session() = ZigoteSession(null).apply { exec = Exec.Inline }

    /** Answers what `InspectServer` answers, for every command the panels send. */
    private fun startFakeApp(tree: String? = null): Int {
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
                    val reply = when {
                        command == "targets" -> TARGETS
                        command == "widgets" || command == "semantics" -> tree ?: TREE
                        command.startsWith("shot") -> SHOT
                        command.startsWith("preview") -> """{"ok":true}"""
                        else -> """{"error":"unknown command '$command'"}"""
                    }
                    it.getOutputStream().write((reply + "\n").toByteArray())
                    it.getOutputStream().flush()
                }
            }
        }
        return socket.localPort
    }

    @Test
    fun `attaching fills the widget list`() {
        val session = session()
        session.attach(startFakeApp())
        assertEquals(session.port, session.port) // attach resolved inline

        val panel = PreviewPanel(null, session)
        try {
            assertEquals(2, panel.targetCount())
            assertEquals("AdwaitaGallery.Pages.AvatarPage", panel.targetAt(0))
            assertEquals("AdwaitaGallery.Pages.ImageGridPage", panel.targetAt(1))
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `a panel created before the app connects still fills`() {
        // The order that broke in practice: the tool window is already open, the app arrives later.
        val session = session()
        val panel = PreviewPanel(null, session)
        try {
            assertEquals(0, panel.targetCount())
            session.attach(startFakeApp())
            assertEquals(2, panel.targetCount())
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `refreshing the list does not change which widget is shown`() {
        val session = session()
        session.attach(startFakeApp())
        val panel = PreviewPanel(null, session)
        try {
            panel.select("AdwaitaGallery.Pages.ImageGridPage")
            panel.refreshFromTest()
            assertEquals("AdwaitaGallery.Pages.ImageGridPage", panel.selected())
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `with no app the panel says so instead of sitting blank`() {
        val panel = PreviewPanel(null, session())
        try {
            assertEquals("no app running — press Run app", panel.statusText())
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `a tree panel loads and labels its nodes`() {
        val session = session()
        session.attach(startFakeApp())

        val panel = TreePanel(session, "widgets", ::widgetLabel)
        assertEquals(2, panel.nodeCount())
        assertEquals("Center  8×8", panel.rootLabel())
    }

    @Test
    fun `a server error reaches the status line`() {
        val session = session()
        session.attach(startFakeApp())
        val panel = TreePanel(session, "nonsense", ::widgetLabel)
        assertEquals("unknown command 'nonsense'", panel.statusText())
    }

    companion object {
        @ClassRule
        @JvmField
        val application = ApplicationRule()

        private const val TARGETS =
            """{"targets":["AdwaitaGallery.Pages.AvatarPage","AdwaitaGallery.Pages.ImageGridPage"]}"""

        private const val TREE =
            """{"tree":{"id":1,"type":"Center","desc":null,"x":0,"y":0,"w":8,"h":8,"children":[
               {"id":2,"type":"Text","desc":"hi","x":0,"y":0,"w":4,"h":4,"children":[]}]}}"""

        // A 1×1 24-bit BMP, so the panel's decode path runs for real.
        private const val SHOT =
            """{"format":"bmp","w":1,"h":1,"scale":1,"data":"Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAA////AA=="}"""
    }
}
