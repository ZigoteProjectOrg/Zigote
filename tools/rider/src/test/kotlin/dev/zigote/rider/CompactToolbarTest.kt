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
import kotlin.concurrent.thread

/**
 * Compact mode, checked by the thing it exists to change: the rows the toolbar takes from the picture.
 *
 * Asserting on the measured height rather than on which control is where — a toolbar that hides half
 * its controls and still wraps into four rows has done nothing, and that is the only way this feature
 * can fail while looking implemented.
 */
class CompactToolbarTest {

    private var server: ServerSocket? = null

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
                    val reply = when {
                        command == "targets" -> """{"targets":["AdwaitaGallery.Pages.ImageGridPage"]}"""
                        command == "locales" -> """{"current":null,"locales":[]}"""
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

    private fun panel(): PreviewPanel {
        val session = ZigoteSession(null).apply { exec = Exec.Inline }
        session.attach(startFakeApp())
        return PreviewPanel(null, session)
    }

    @Test
    fun `compact gives the picture back rows at a docked width`() {
        val panel = panel()
        try {
            val expanded = panel.toolbarHeightForTest(NARROW)
            panel.chooseCompactForTest(true)
            val compact = panel.toolbarHeightForTest(NARROW)
            assertTrue("expanded $expanded, compact $compact", compact < expanded)
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `nothing is lost — expanding brings every control back`() {
        val panel = panel()
        try {
            val all = panel.shownCountForTest()
            panel.chooseCompactForTest(true)
            assertTrue("compact showed ${panel.shownCountForTest()} of $all", panel.shownCountForTest() < all)
            assertFalse("captions are the first thing compact drops", panel.captionShownForTest())

            panel.chooseCompactForTest(false)
            assertEquals(all, panel.shownCountForTest())
            assertTrue(panel.captionShownForTest())
        } finally {
            panel.dispose()
        }
    }

    /** An icon with no tooltip is a button nobody can identify — the words have to go somewhere. */
    @Test
    fun `a compacted verb keeps its words as its tooltip`() {
        val panel = panel()
        try {
            val run = panel.runButtonForTest()
            assertEquals("Run app", run.text)
            assertEquals("Run app", run.toolTipText)

            panel.chooseCompactForTest(true)
            assertEquals("", run.text)
            assertTrue("an icon-only button needs an icon", run.icon != null)
            assertEquals("Run app", run.toolTipText)

            panel.chooseCompactForTest(false)
            assertEquals("Run app", run.text)
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `a narrow panel compacts itself and a wide one does not`() {
        val panel = panel()
        try {
            panel.widthChangedForTest(NARROW)
            assertTrue(panel.compactForTest())
            panel.widthChangedForTest(WIDE)
            assertFalse(panel.compactForTest())
        } finally {
            panel.dispose()
        }
    }

    /** The width rule is a default, not an override: resizing must not undo what was just clicked. */
    @Test
    fun `once the toggle is pressed the width stops deciding`() {
        val panel = panel()
        try {
            panel.chooseCompactForTest(true)
            panel.widthChangedForTest(WIDE)
            assertTrue("a resize expanded a toolbar the user compacted", panel.compactForTest())

            panel.chooseCompactForTest(false)
            panel.widthChangedForTest(NARROW)
            assertFalse("a resize compacted a toolbar the user expanded", panel.compactForTest())
        } finally {
            panel.dispose()
        }
    }

    companion object {
        @ClassRule
        @JvmField
        val application = ApplicationRule()

        /** A tool window docked to the right edge, and one docked to the bottom. */
        private const val NARROW = 420
        private const val WIDE = 1200

        private const val SHOT =
            """{"format":"bmp","w":1,"h":1,"scale":1,"data":"Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAA////AA=="}"""
    }
}
