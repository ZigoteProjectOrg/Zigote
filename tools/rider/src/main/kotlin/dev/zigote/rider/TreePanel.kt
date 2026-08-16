package dev.zigote.rider

import com.intellij.openapi.Disposable
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.components.JBTextField
import com.intellij.ui.treeStructure.Tree
import java.awt.BorderLayout
import java.awt.FlowLayout
import javax.swing.JButton
import javax.swing.JPanel
import javax.swing.JSplitPane
import javax.swing.event.DocumentEvent
import javax.swing.event.DocumentListener
import javax.swing.tree.DefaultMutableTreeNode
import javax.swing.tree.DefaultTreeModel
import javax.swing.tree.TreeSelectionModel

/**
 * One tree, whichever the app calls it: [command] is the socket command, [label] renders a node.
 *
 * A list of node names is not worth opening; what makes a tree useful is answering "which one is that
 * on screen" and "what is it set to". So selecting a node outlines it in the Preview tab and loads its
 * properties, and the filter box keeps a 300-node tree navigable.
 */
internal class TreePanel(
    private val session: ZigoteSession,
    private val command: String,
    private val label: (Map<String, Any?>) -> String,
) : JPanel(BorderLayout()), Disposable {

    private val model = DefaultTreeModel(DefaultMutableTreeNode("(not loaded)"))
    private val tree = Tree(model).apply {
        isRootVisible = true
        selectionModel.selectionMode = TreeSelectionModel.SINGLE_TREE_SELECTION
    }
    private val filter = JBTextField(14)
    private val details = DefaultTreeModel(DefaultMutableTreeNode("(no selection)"))
    private val detailsTree = Tree(details).apply { isRootVisible = false }
    private val status = JBLabel("")
    private var loaded: Map<String, Any?>? = null
    private val subscriptions = mutableListOf<Unsubscribe>()

    init {
        add(JPanel(FlowLayout(FlowLayout.LEFT, 4, 2)).apply {
            add(JButton("Refresh").apply { addActionListener { refresh() } })
            add(JBLabel("Filter:"))
            add(filter)
            add(status)
        }, BorderLayout.NORTH)

        add(
            JSplitPane(JSplitPane.VERTICAL_SPLIT, JBScrollPane(tree), JBScrollPane(detailsTree))
                .apply { resizeWeight = 0.65 },
            BorderLayout.CENTER,
        )

        filter.document.addDocumentListener(object : DocumentListener {
            override fun insertUpdate(e: DocumentEvent) = rebuild()
            override fun removeUpdate(e: DocumentEvent) = rebuild()
            override fun changedUpdate(e: DocumentEvent) = rebuild()
        })

        tree.addTreeSelectionListener { selected() }

        subscriptions += session.onChanged { if (session.port != null) refresh() }
        if (session.port != null) refresh()
    }

    override fun dispose() {
        subscriptions.forEach { it() }
        subscriptions.clear()
    }

    internal fun statusText(): String = status.text
    internal fun nodeCount(): Int = countRows(model.root as DefaultMutableTreeNode)
    internal fun rootLabel(): String = (model.root as DefaultMutableTreeNode).userObject.toString()

    private fun countRows(node: DefaultMutableTreeNode): Int =
        1 + (0 until node.childCount).sumOf { countRows(node.getChildAt(it) as DefaultMutableTreeNode) }

    private fun refresh() {
        query(session, command, status) { reply ->
            loaded = reply.node("tree")
            rebuild()
        }
    }

    private fun rebuild() {
        val root = loaded
        if (root == null) {
            model.setRoot(DefaultMutableTreeNode("(empty)"))
            return
        }

        val needle = filter.text.trim().lowercase()
        model.setRoot(build(root, needle) ?: DefaultMutableTreeNode("(nothing matches '$needle')"))
        // Two levels when browsing; a filtered tree is small, so open all of it.
        repeat(if (needle.isEmpty()) 2 else 12) {
            for (row in tree.rowCount - 1 downTo 0) tree.expandRow(row)
        }
        status.text = "${countRows(model.root as DefaultMutableTreeNode)} nodes"
    }

    /** Keeps a node when it matches, or when a descendant does — a match with no path to it is noise. */
    private fun build(node: Map<String, Any?>, needle: String): DefaultMutableTreeNode? {
        val kept = node.children().mapNotNull { build(it, needle) }
        val text = label(node)
        if (needle.isNotEmpty() && kept.isEmpty() && !text.lowercase().contains(needle)) return null

        val branch = DefaultMutableTreeNode(Node(text, node))
        kept.forEach(branch::add)
        return branch
    }

    private fun selected() {
        val node = (tree.lastSelectedPathComponent as? DefaultMutableTreeNode)?.userObject as? Node
        if (node == null) {
            session.highlight(null)
            return
        }

        val source = node.source
        session.highlight(
            floatArrayOf(
                source.int("x").toFloat(),
                source.int("y").toFloat(),
                source.int("w").toFloat(),
                source.int("h").toFloat(),
            )
        )
        showDetails(source)
    }

    /** Bounds and role come with the tree; the rest is one `props` round trip for widgets only. */
    private fun showDetails(source: Map<String, Any?>) {
        val root = DefaultMutableTreeNode("props")
        for (key in listOf("type", "role", "label", "value", "hint", "flags", "actions")) {
            source.text(key)?.let { root.add(DefaultMutableTreeNode("$key = $it")) }
        }
        root.add(DefaultMutableTreeNode("bounds = ${source.int("x")}, ${source.int("y")}  ${source.int("w")}×${source.int("h")}"))
        details.setRoot(root)
        expandDetails()

        if (command != "widgets") return
        val id = (source["id"] as? Double)?.toInt() ?: return
        query(session, "props $id", status) { reply ->
            @Suppress("UNCHECKED_CAST")
            val props = reply["props"] as? Map<String, Any?> ?: return@query
            props.forEach { (k, v) -> root.add(DefaultMutableTreeNode("$k = $v")) }
            details.reload()
            expandDetails()
        }
    }

    private fun expandDetails() {
        for (row in detailsTree.rowCount - 1 downTo 0) detailsTree.expandRow(row)
    }

    /** Carries the raw node so selection can highlight and drill into it, while rendering as a label. */
    private class Node(private val text: String, val source: Map<String, Any?>) {
        override fun toString() = text
    }
}

internal fun widgetLabel(node: Map<String, Any?>): String {
    val type = node.text("type") ?: "?"
    val size = "${node.int("w")}×${node.int("h")}"
    val desc = node.text("desc")
    return if (desc.isNullOrBlank()) "$type  $size" else "$type  $desc  $size"
}

internal fun semanticsLabel(node: Map<String, Any?>): String {
    val role = node.text("role") ?: "?"
    val size = "${node.int("w")}×${node.int("h")}"
    val label = node.text("label")
    return if (label.isNullOrBlank()) "$role  $size" else "$role  \"$label\"  $size"
}
