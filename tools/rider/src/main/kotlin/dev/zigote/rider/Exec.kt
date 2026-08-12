package dev.zigote.rider

import com.intellij.openapi.application.ApplicationManager

/**
 * Where the panels' work runs: something off the EDT, and something back on it.
 *
 * A seam rather than direct `ApplicationManager` calls, because the bug that kept the widget list
 * empty lived in exactly this hop and nothing could reach it — the panels needed a `Project`, and a
 * Rider `Project` in a test needs a real solution. With the threading behind an interface the whole
 * path is a plain JVM test.
 */
internal interface Exec {
    fun background(block: () -> Unit)
    fun ui(block: () -> Unit)

    object Platform : Exec {
        override fun background(block: () -> Unit) {
            ApplicationManager.getApplication().executeOnPooledThread(block)
        }

        override fun ui(block: () -> Unit) {
            ApplicationManager.getApplication().invokeLater(block)
        }
    }

    /** Straight-line: every hop on the calling thread, in order. Tests only. */
    object Inline : Exec {
        override fun background(block: () -> Unit) = block()
        override fun ui(block: () -> Unit) = block()
    }
}
