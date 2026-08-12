package dev.zigote.rider

import java.net.ServerSocket
import java.net.Socket
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The client half, against a socket that answers exactly what `InspectServer` answers.
 *
 * These replies are copied from a real run of the Adwaita gallery, not invented — the previous bug was
 * a client that compiled against a JSON library it could not load at runtime, which no test of the
 * server would ever have caught.
 */
class ZigoteInspectTest {

    /** A one-shot stand-in for InspectServer: read a line, write a reply, close. */
    private fun serve(reply: (String) -> String): Pair<Int, ServerSocket> {
        val server = ServerSocket(0)
        thread(isDaemon = true) {
            while (!server.isClosed) {
                val client: Socket = try {
                    server.accept()
                } catch (_: Exception) {
                    return@thread
                }
                client.use {
                    val command = it.getInputStream().bufferedReader().readLine() ?: return@use
                    it.getOutputStream().write((reply(command) + "\n").toByteArray())
                    it.getOutputStream().flush()
                }
            }
        }
        return server.localPort to server
    }

    @Test
    fun `targets round-trips over a socket`() {
        val (port, server) = serve { """{"targets":["AdwaitaGallery.Pages.AvatarPage","A.B"]}""" }
        server.use {
            val reply = ZigoteInspect.query(port, "targets")
            assertEquals(listOf("AdwaitaGallery.Pages.AvatarPage", "A.B"), reply.strings("targets"))
        }
    }

    @Test
    fun `a widget tree parses into nodes with geometry`() {
        val json = """
            {"tree":{"id":1,"type":"ThemeProvider","desc":null,"x":0,"y":0,"w":1100,"h":760,"children":[
              {"id":2,"type":"Navigator","desc":"route /","x":0,"y":0,"w":1100,"h":760,"children":[]}]}}
        """.trimIndent()
        val (port, server) = serve { json }
        server.use {
            val tree = ZigoteInspect.query(port, "widgets").node("tree")!!
            assertEquals("ThemeProvider", tree.text("type"))
            assertEquals(1100, tree.int("w"))
            assertEquals("ThemeProvider  1100×760", widgetLabel(tree))

            val child = tree.children().single()
            assertEquals("Navigator  route /  1100×760", widgetLabel(child))
        }
    }

    @Test
    fun `a semantics tree keeps roles and labels`() {
        val json = """{"tree":{"id":0,"role":"Button","label":"OK","value":null,"hint":null,
            "flags":"None","actions":"Tap","x":4,"y":4,"w":90,"h":20,"children":[]}}"""
        val (port, server) = serve { json }
        server.use {
            val tree = ZigoteInspect.query(port, "semantics").node("tree")!!
            assertEquals("Button  \"OK\"  90×20", semanticsLabel(tree))
        }
    }

    @Test
    fun `an error reply is readable rather than thrown`() {
        val (port, server) = serve { """{"error":"unknown command 'bogus'"}""" }
        server.use {
            assertEquals("unknown command 'bogus'", ZigoteInspect.query(port, "bogus").error())
        }
    }

    @Test
    fun `text the app escaped comes back intact`() {
        // Widget descriptions carry user strings; the reader has to undo what the writer did.
        val (port, server) = serve { """{"tree":{"type":"Text","desc":"say \"hi\"\\ \n now","w":10,"h":5,"children":[]}}""" }
        server.use {
            val tree = ZigoteInspect.query(port, "widgets").node("tree")!!
            assertEquals("say \"hi\"\\ \n now", tree.text("desc"))
        }
    }

    @Test
    fun `reachable tells a live port from a dead one`() {
        val (port, server) = serve { "{}" }
        server.use { assertTrue(ZigoteInspect.reachable(port)) }
        assertFalse(ZigoteInspect.reachable(port)) // closed by use{}
    }

    @Test
    fun `a reply that is not an object is an error, not a crash`() {
        val (port, server) = serve { "not json at all" }
        server.use { assertFailsWith<Exception> { ZigoteInspect.query(port, "widgets") } }
    }

    @Test
    fun `numbers, booleans and nulls read back as themselves`() {
        val parsed = ZigoteInspect.Json.parse("""{"a":12.5,"b":-3,"c":true,"d":false,"e":null,"f":[1,2]}""")
        @Suppress("UNCHECKED_CAST") val map = parsed as Map<String, Any?>
        assertEquals(12.5, map["a"])
        assertEquals(-3.0, map["b"])
        assertEquals(true, map["c"])
        assertEquals(false, map["d"])
        assertEquals(null, map["e"])
        assertEquals(listOf(1.0, 2.0), map["f"])
    }
}
