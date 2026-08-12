package dev.zigote.rider

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.awt.event.KeyEvent
import javax.swing.JPanel

/** The AWT→KeyCode table — pure, no IDE. A wrong name here is a key the preview silently swallows. */
class KeysTest {

    @Test
    fun `letters digits and named keys map to KeyCode names`() {
        assertEquals("A", Keys.name(KeyEvent.VK_A))
        assertEquals("Z", Keys.name(KeyEvent.VK_Z))
        assertEquals("Digit0", Keys.name(KeyEvent.VK_0))
        assertEquals("Digit9", Keys.name(KeyEvent.VK_9))
        assertEquals("F12", Keys.name(KeyEvent.VK_F12))
        assertEquals("Enter", Keys.name(KeyEvent.VK_ENTER))
        assertEquals("Backspace", Keys.name(KeyEvent.VK_BACK_SPACE))
        assertEquals("PageDown", Keys.name(KeyEvent.VK_PAGE_DOWN))
        assertNull(Keys.name(KeyEvent.VK_SHIFT)) // bare modifiers ride along, never travel alone
    }

    @Test
    fun `commands carry the transition and the modifiers`() {
        val source = JPanel()
        val plain = KeyEvent(source, KeyEvent.KEY_PRESSED, 0, 0, KeyEvent.VK_LEFT, KeyEvent.CHAR_UNDEFINED)
        assertEquals("input keydown Left", Keys.command(plain, down = true))
        assertEquals("input keyup Left", Keys.command(plain, down = false))

        val chord = KeyEvent(
            source, KeyEvent.KEY_PRESSED, 0,
            KeyEvent.SHIFT_DOWN_MASK or KeyEvent.CTRL_DOWN_MASK,
            KeyEvent.VK_A, KeyEvent.CHAR_UNDEFINED,
        )
        assertEquals("input keydown A shift+ctrl", Keys.command(chord, down = true))
    }
}
