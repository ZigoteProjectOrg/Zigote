package dev.zigote.rider

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Reading previewable declarations out of C# source.
 *
 * The gutter is only as good as this: an icon on the wrong line, or a name that does not match what
 * the app calls the same widget, is worse than no icon. The names here are the exact strings the
 * `preview` command takes.
 */
class PreviewScanTest {

    private val source = """
        namespace Shop.Pages;

        [Obsolete("moving")]
        [Preview("Product card", Width = 412, Height = 915)]
        public sealed class ProductCard : Widget
        {
            public static Widget Empty() => new ProductCard();
        }

        internal class Plain : Widget
        {
        }
    """.trimIndent()

    private fun names() = PreviewScan.scan(source).associateBy { it.name }

    @Test
    fun `types are qualified by their namespace`() {
        assertTrue(names().containsKey("Shop.Pages.ProductCard"))
        assertTrue(names().containsKey("Shop.Pages.Plain"))
    }

    @Test
    fun `a static widget factory is qualified by the type that holds it`() {
        assertTrue(names().containsKey("Shop.Pages.ProductCard.Empty"))
    }

    @Test
    fun `the annotation is found through other attributes and modifiers`() {
        assertTrue(names().getValue("Shop.Pages.ProductCard").annotated)
        assertFalse(names().getValue("Shop.Pages.Plain").annotated)
    }

    @Test
    fun `an annotated factory is annotated, not its enclosing type`() {
        val scanned = PreviewScan.scan(
            """
            namespace Gallery;

            internal static class Previews
            {
                [Preview("Buttons")]
                public static Widget Buttons() => new Widget();
            }
            """.trimIndent()
        ).associateBy { it.name }

        assertTrue(scanned.getValue("Gallery.Previews.Buttons").annotated)
        assertFalse(scanned.getValue("Gallery.Previews").annotated)
    }

    @Test
    fun `the icon sits on the declared name`() {
        val card = names().getValue("Shop.Pages.ProductCard")
        assertEquals("ProductCard", source.substring(card.start, card.end))
    }

    @Test
    fun `the caret resolves to the declaration it is inside`() {
        // Inside the factory's body — the nearest declaration, which is the one to preview.
        val inFactory = source.indexOf("new ProductCard();")
        assertEquals("Shop.Pages.ProductCard.Empty", PreviewScan.at(source, inFactory))

        // Inside the class but above the factory.
        val inClass = source.indexOf(": Widget") + 3
        assertEquals("Shop.Pages.ProductCard", PreviewScan.at(source, inClass))

        // Above everything: nothing has been declared yet.
        assertEquals(null, PreviewScan.at(source, 0))
    }
}
