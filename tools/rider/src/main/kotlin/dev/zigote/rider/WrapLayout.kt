package dev.zigote.rider

import java.awt.Container
import java.awt.Dimension
import java.awt.FlowLayout
import javax.swing.JScrollPane
import javax.swing.SwingUtilities

/**
 * A [FlowLayout] that reports the height it actually needs once it has wrapped.
 *
 * Plain FlowLayout wraps its rows but still reports a single row's preferred height, so in a
 * `BorderLayout.NORTH` slot the container is given one row's worth of space and everything past the
 * first row is simply not drawn. In a docked tool window that is narrower than the toolbar, that means
 * the buttons at the end — **Run app** among them — are invisible with no scrollbar and no hint that
 * anything is missing.
 */
internal class WrapLayout(align: Int = LEFT, hgap: Int = 4, vgap: Int = 2) : FlowLayout(align, hgap, vgap) {

    override fun preferredLayoutSize(target: Container): Dimension = layoutSize(target, true)

    override fun minimumLayoutSize(target: Container): Dimension =
        layoutSize(target, false).also { it.width -= hgap + 1 }

    private fun layoutSize(target: Container, preferred: Boolean): Dimension {
        synchronized(target.treeLock) {
            // Width to wrap against: the container's own, or an enclosing scroll pane's, or — before
            // the first layout, when both are zero — unbounded, which degrades to one row.
            var targetWidth = target.size.width
            var container: Container? = target
            while (container?.size?.width == 0 && container.parent != null) container = container.parent
            if (container?.size?.width != 0) targetWidth = container?.size?.width ?: targetWidth
            if (targetWidth == 0) targetWidth = Int.MAX_VALUE

            val insets = target.insets
            val maxWidth = targetWidth - (insets.left + insets.right + hgap * 2)

            val dim = Dimension(0, 0)
            var rowWidth = 0
            var rowHeight = 0

            fun endRow() {
                dim.width = maxOf(dim.width, rowWidth)
                dim.height += rowHeight + vgap
                rowWidth = 0
                rowHeight = 0
            }

            for (i in 0 until target.componentCount) {
                val component = target.getComponent(i)
                if (!component.isVisible) continue
                val size = if (preferred) component.preferredSize else component.minimumSize
                if (rowWidth + size.width > maxWidth && rowWidth > 0) endRow()
                if (rowWidth > 0) rowWidth += hgap
                rowWidth += size.width
                rowHeight = maxOf(rowHeight, size.height)
            }
            endRow()

            dim.width += insets.left + insets.right + hgap * 2
            dim.height += insets.top + insets.bottom + vgap * 2

            // Inside a scroll pane the viewport is one gap narrower than it claims; without this the
            // last row oscillates between wrapped and not on every resize.
            if (SwingUtilities.getAncestorOfClass(JScrollPane::class.java, target) != null &&
                target.isValid
            ) dim.width += hgap * 2

            return dim
        }
    }
}
