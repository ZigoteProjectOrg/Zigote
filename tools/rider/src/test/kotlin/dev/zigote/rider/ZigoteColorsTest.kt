package dev.zigote.rider

import dev.zigote.rider.ZigoteColors.Form
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The parsing half — no IDE needed to run it. Round-tripping is what matters: a swatch that reads a
 * colour one way and writes it back another silently corrupts source on every click.
 */
class ZigoteColorsTest {

    @Test
    fun `finds every call shape`() {
        val source = """
            var a = new Color(0xFF2196F3);
            var b = Color.FromHex(0x802196F3);
            var c = new Color(0.5f, 0.25f, 0f);
            var d = Color.Rgb(16, 18, 22);
            var e = Color.Rgba(16, 18, 22, 0.5f);
        """.trimIndent()

        val found = ZigoteColors.scan(source)
        assertEquals(listOf(Form.HEX, Form.HEX, Form.FLOATS, Form.RGB, Form.RGBA), found.map { it.form })
        assertEquals(0xFF2196F3.toInt(), found[0].argb)
        assertEquals(0x802196F3.toInt(), found[1].argb)
        assertEquals(0xFF, found[3].argb ushr 24) // Rgb is opaque
        assertEquals(0x80, found[4].argb ushr 24)
    }

    @Test
    fun `hex without an alpha byte is opaque, not invisible`() {
        assertEquals(0xFF2196F3.toInt(), ZigoteColors.parse("0x2196F3", Form.HEX))
    }

    @Test
    fun `the marked span covers the arguments only`() {
        val source = "new Color(0xFF2196F3)"
        val literal = ZigoteColors.scan(source).single()
        assertEquals("0xFF2196F3", source.substring(literal.start, literal.end))
    }

    @Test
    fun `every shape round-trips`() {
        for (form in Form.entries) {
            val argb = if (form.supportsAlpha) 0x802196F3.toInt() else 0xFF2196F3.toInt()
            val text = ZigoteColors.format(argb, form)
            assertEquals(argb, ZigoteColors.parse(text, form), "$form wrote '$text'")
        }
    }

    @Test
    fun `an opaque float colour keeps three components`() {
        assertTrue(ZigoteColors.format(0xFF2196F3.toInt(), Form.FLOATS).count { it == ',' } == 2)
    }

    @Test
    fun `nonsense is not a colour`() {
        assertEquals(emptyList(), ZigoteColors.scan("new ColorScheme(0xFF2196F3); Color.Rgb(999, 0, 0);"))
    }

    @Test
    fun `the type at the caret is the one it sits in`() {
        val source = """
            namespace My.App;
            internal sealed class FirstPage : Widget { }
            internal sealed class SecondPage : Widget { /* caret */ }
        """.trimIndent()

        assertEquals("My.App.SecondPage", PreviewWidgetAction.typeAtCaret(source, source.indexOf("caret")))
        assertEquals("My.App.FirstPage", PreviewWidgetAction.typeAtCaret(source, source.indexOf("FirstPage") + 9))
    }
}
