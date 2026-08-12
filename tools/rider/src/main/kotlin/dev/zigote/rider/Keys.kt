package dev.zigote.rider

import java.awt.event.KeyEvent

/**
 * AWT key events → the framework's `KeyCode` names, for the inspect socket's `input keydown NAME`.
 *
 * Only keys the framework names are mapped; anything else (dead keys, media keys, bare modifier
 * presses) returns null and is simply not sent — its effect either travels as committed text
 * (`input text …`) or has no meaning inside a preview. Modifiers ride along on every event instead.
 */
internal object Keys {

    fun name(code: Int): String? = when (code) {
        in KeyEvent.VK_A..KeyEvent.VK_Z -> ('A' + (code - KeyEvent.VK_A)).toString()
        in KeyEvent.VK_0..KeyEvent.VK_9 -> "Digit${code - KeyEvent.VK_0}"
        in KeyEvent.VK_F1..KeyEvent.VK_F12 -> "F${code - KeyEvent.VK_F1 + 1}"
        KeyEvent.VK_ENTER -> "Enter"
        KeyEvent.VK_ESCAPE -> "Escape"
        KeyEvent.VK_BACK_SPACE -> "Backspace"
        KeyEvent.VK_TAB -> "Tab"
        KeyEvent.VK_SPACE -> "Space"
        KeyEvent.VK_MINUS -> "Minus"
        KeyEvent.VK_EQUALS -> "Equals"
        KeyEvent.VK_OPEN_BRACKET -> "LeftBracket"
        KeyEvent.VK_CLOSE_BRACKET -> "RightBracket"
        KeyEvent.VK_BACK_SLASH -> "Backslash"
        KeyEvent.VK_SEMICOLON -> "Semicolon"
        KeyEvent.VK_QUOTE -> "Apostrophe"
        KeyEvent.VK_BACK_QUOTE -> "Grave"
        KeyEvent.VK_COMMA -> "Comma"
        KeyEvent.VK_PERIOD -> "Period"
        KeyEvent.VK_SLASH -> "Slash"
        KeyEvent.VK_INSERT -> "Insert"
        KeyEvent.VK_HOME -> "Home"
        KeyEvent.VK_PAGE_UP -> "PageUp"
        KeyEvent.VK_DELETE -> "Delete"
        KeyEvent.VK_END -> "End"
        KeyEvent.VK_PAGE_DOWN -> "PageDown"
        KeyEvent.VK_RIGHT -> "Right"
        KeyEvent.VK_LEFT -> "Left"
        KeyEvent.VK_DOWN -> "Down"
        KeyEvent.VK_UP -> "Up"
        else -> null
    }

    /** The `+`-joined modifier suffix for an event, or "" — `shift+ctrl` in the wire grammar. */
    fun mods(e: KeyEvent): String = buildString {
        if (e.isShiftDown) append("shift+")
        if (e.isControlDown) append("ctrl+")
        if (e.isAltDown) append("alt+")
        if (e.isMetaDown) append("cmd+")
    }.trimEnd('+')

    /** The full wire command for a key transition, or null for keys the framework has no name for. */
    fun command(e: KeyEvent, down: Boolean): String? {
        val name = name(e.keyCode) ?: return null
        val mods = mods(e)
        val verb = if (down) "keydown" else "keyup"
        return if (mods.isEmpty()) "input $verb $name" else "input $verb $name $mods"
    }
}
