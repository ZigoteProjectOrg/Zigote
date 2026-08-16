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
 * `[Preview]` and the property editor, end to end: the app describes a target, the panel draws
 * controls for it, and what is typed into them comes back as the `preview` spec that shows it.
 *
 * The assertion that matters is the *command sent* — every part of this feature exists to turn a
 * control into that one string, and a panel that renders the fields but sends the wrong spec looks
 * completely correct until the picture does not change.
 */
class PreviewPropertiesTest {

    private var server: ServerSocket? = null
    private val seen: MutableList<String> = Collections.synchronizedList(mutableListOf())

    @After
    fun stopFakeApp() {
        server?.close()
    }

    private fun session() = ZigoteSession(null).apply { exec = Exec.Inline }

    private fun startFakeApp(previews: String = PREVIEWS): Int {
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
                    seen += command
                    val reply = when {
                        command == "previews" -> previews
                        command == "targets" -> """{"targets":["Old.App.Page"]}"""
                        command.startsWith("shot") -> SHOT
                        command.startsWith("preview") || command.startsWith("size") ||
                            command.startsWith("theme") -> """{"ok":true,"w":412,"h":915}"""

                        else -> """{"error":"unknown command '$command'"}"""
                    }
                    it.getOutputStream().write((reply + "\n").toByteArray())
                    it.getOutputStream().flush()
                }
            }
        }
        return socket.localPort
    }

    private fun lastPreview(): String? = seen.lastOrNull { it.startsWith("preview ") }

    @Test
    fun `an annotated target is listed by its name, first`() {
        val session = session()
        session.attach(startFakeApp())
        val panel = PreviewPanel(null, session)
        try {
            assertEquals("Product card", panel.labelAt(0))
            assertEquals("Shop.Cards.ProductCard", panel.targetAt(0))
            // Unannotated targets keep their type name, which is all there is to call them.
            assertEquals("Shop.Pages.Plain", panel.labelAt(1))
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `choosing a target with properties opens the editor, one without closes it`() {
        val session = session()
        session.attach(startFakeApp())
        val panel = PreviewPanel(null, session)
        try {
            panel.select("Shop.Cards.ProductCard")
            assertTrue(panel.propsForTest().isVisible)
            panel.select("Shop.Pages.Plain")
            assertFalse(panel.propsForTest().isVisible)
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `an edited property becomes the preview spec, and only the edited one`() {
        val session = session()
        session.attach(startFakeApp())
        val panel = PreviewPanel(null, session)
        try {
            panel.select("Shop.Cards.ProductCard")
            panel.propsForTest().setForTest("title", "Flat white")
            // 'sale' is untouched, so it is left to the app's own default rather than pinned here.
            assertEquals("preview Shop.Cards.ProductCard?title=Flat+white", lastPreview())
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `Reset puts the declared defaults back into the controls, not just into the app`() {
        val session = session()
        session.attach(startFakeApp())
        val panel = PreviewPanel(null, session)
        try {
            panel.select("Shop.Cards.ProductCard")
            panel.propsForTest().setForTest("title", "Flat white")
            assertEquals("Flat white", panel.propsForTest().shownForTest("title"))

            panel.propsForTest().resetForTest()

            assertEquals("Espresso", panel.propsForTest().shownForTest("title"))
            assertEquals("preview Shop.Cards.ProductCard", lastPreview())
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `a property set back to its default drops out of the spec`() {
        val session = session()
        session.attach(startFakeApp())
        val panel = PreviewPanel(null, session)
        try {
            panel.select("Shop.Cards.ProductCard")
            panel.propsForTest().setForTest("title", "Flat white")
            panel.propsForTest().setForTest("title", "Espresso")
            assertEquals("preview Shop.Cards.ProductCard", lastPreview())
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `the size and theme the annotation asked for are applied, and shown`() {
        val session = session()
        session.attach(startFakeApp())
        val panel = PreviewPanel(null, session)
        try {
            panel.select("Shop.Cards.ProductCard")
            assertEquals(412, panel.deviceForTest()?.width)
            assertEquals(915, panel.deviceForTest()?.height)
            assertEquals("Light", panel.themeForTest())
            assertTrue(seen.contains("size 412x915"))

            // The entry belongs to that target: a widget with no annotation must not inherit it.
            panel.select("Shop.Pages.Plain")
            assertFalse(panel.deviceForTest()?.fromAnnotation ?: false)
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `an app too old for previews still fills the list`() {
        val session = session()
        session.attach(startFakeApp(previews = """{"error":"unknown command 'previews'"}"""))
        val panel = PreviewPanel(null, session)
        try {
            assertEquals(1, panel.targetCount())
            assertEquals("Old.App.Page", panel.targetAt(0))
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `asking for a widget swaps a running app instead of restarting it`() {
        val session = session()
        session.attach(startFakeApp())
        val panel = PreviewPanel(null, session)
        try {
            // What the gutter icon and Alt+Shift+P both do. No project is passed, so a launch is not
            // even possible: swapping in place is the whole point.
            session.show("Shop.Pages.Plain", null)

            assertEquals("preview Shop.Pages.Plain", lastPreview())
            // …and the panel says so, instead of naming one widget while showing another.
            assertEquals("Shop.Pages.Plain", panel.selected())
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `asking twice for the same widget still moves the panel back`() {
        val session = session()
        session.attach(startFakeApp())
        val panel = PreviewPanel(null, session)
        try {
            session.show("Shop.Pages.Plain", null)
            panel.select("Shop.Cards.ProductCard")
            session.show("Shop.Pages.Plain", null)
            assertEquals("Shop.Pages.Plain", panel.selected())
        } finally {
            panel.dispose()
        }
    }

    @Test
    fun `a disposed panel stops listening to the session`() {
        // A tool window is opened and closed all day. A listener held by a dead panel is a leak that
        // also calls into disposed Swing components.
        val session = session()
        val panel = PreviewPanel(null, session)
        panel.dispose()
        val before = panel.statusText()

        session.attach(startFakeApp())

        assertEquals(before, panel.statusText())
        assertEquals(0, panel.targetCount())
    }

    @Test
    fun `values are escaped so a title may contain anything`() {
        val target = PreviewTarget(
            target = "A.B",
            params = listOf(PreviewParam("title", "string", "x")),
        )
        assertEquals("A.B?title=a%26b+%3D+c", Previews.spec(target, mapOf("title" to "a&b = c")))
    }

    companion object {
        @ClassRule
        @JvmField
        val application = ApplicationRule()

        private const val PREVIEWS = """{"previews":[
            {"target":"Shop.Cards.ProductCard","label":"Product card","group":"Shop","annotated":true,
             "w":412,"h":915,"theme":"light","params":[
                {"name":"title","kind":"string","value":"Espresso","options":[]},
                {"name":"sale","kind":"bool","value":"false","options":[]}]},
            {"target":"Shop.Pages.Plain","label":null,"group":null,"annotated":false,
             "w":0,"h":0,"theme":null,"params":[]}]}"""

        // A 1×1 24-bit BMP, so the panel's decode path runs for real.
        private const val SHOT =
            """{"format":"bmp","w":1,"h":1,"scale":1,"data":"Qk06AAAAAAAAADYAAAAoAAAAAQAAAAEAAAABABgAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAA////AA=="}"""
    }
}
