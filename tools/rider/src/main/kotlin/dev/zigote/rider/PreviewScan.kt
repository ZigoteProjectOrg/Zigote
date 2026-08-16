package dev.zigote.rider

/**
 * Finding the previewable declarations in a C# file, by reading it.
 *
 * Text, not PSI, for the same reason as [ZigoteColors]: Rider keeps the C# PSI in the ReSharper
 * backend, so a frontend plugin that wanted to resolve symbols would be a second plugin built against
 * a second SDK. What is needed here is much smaller than symbol resolution — the *name* of the thing a
 * line declares — and a name is spelled out in the text.
 *
 * Being approximate is safe because this is only ever a candidate list: whether a candidate is really
 * previewable is answered by the running app's own target list (see [ZigotePreviewGutter]), and with
 * no app running only an explicit `[Preview]` counts. So a missed nested type costs an icon, never a
 * wrong one.
 */
internal object PreviewScan {

    /** A declaration worth an icon: where its name sits, what it is called, and whether it opted in. */
    data class Declaration(
        val start: Int,
        val end: Int,
        val name: String,
        val annotated: Boolean,
    )

    private val NAMESPACE = Regex("""\bnamespace\s+([A-Za-z_][\w.]*)""")
    private val TYPE = Regex("""\b(?:class|record|struct)\s+([A-Za-z_]\w*)""")

    /**
     * A static factory: `static [modifiers] SomethingWidget Name(`. The return type is matched by
     * name because that is all the text says — which is exactly why the result is a candidate rather
     * than an answer.
     */
    private val FACTORY =
        Regex("""\bstatic\s+(?:[\w.<>?\[\]]+\s+)*?([\w.<>?\[\]]*Widget[\w.<>?\[\]]*)\s+([A-Za-z_]\w*)\s*\(""")

    /** Every declaration in the file, in the order they appear. */
    fun scan(text: CharSequence): List<Declaration> {
        val namespaces = NAMESPACE.findAll(text).map { it.range.first to it.groupValues[1] }.toList()
        val types = TYPE.findAll(text).toList()

        val declarations = ArrayList<Declaration>()

        for (type in types) {
            val name = type.groups[1] ?: continue
            declarations += Declaration(
                start = name.range.first,
                end = name.range.last + 1,
                name = qualify(namespaces, type.range.first, type.groupValues[1]),
                annotated = annotated(text, type.range.first),
            )
        }

        for (factory in FACTORY.findAll(text)) {
            val name = factory.groups[2] ?: continue
            // A method's owner is the type declared above it, which is what its full name needs.
            val owner = types.lastOrNull { it.range.first < factory.range.first } ?: continue
            declarations += Declaration(
                start = name.range.first,
                end = name.range.last + 1,
                name = qualify(namespaces, owner.range.first, owner.groupValues[1]) +
                    "." + factory.groupValues[2],
                annotated = annotated(text, factory.range.first),
            )
        }

        return declarations.sortedBy { it.start }
    }

    /** The name of the declaration the caret is in: the last one that starts at or before it. */
    fun at(text: CharSequence, offset: Int): String? =
        scan(text).lastOrNull { it.start <= offset.coerceIn(0, text.length) }?.name

    private fun qualify(
        namespaces: List<Pair<Int, String>>,
        offset: Int,
        name: String,
    ): String {
        val namespace = namespaces.lastOrNull { it.first < offset }?.second
        return if (namespace == null) name else "$namespace.$name"
    }

    /**
     * Whether a `[Preview]` sits above the declaration starting at [offset].
     *
     * Walks back over whitespace and whole attribute groups, so it still finds it through the
     * `[Obsolete]`, `[SupportedOSPlatform]` and access modifiers a real declaration carries. The
     * modifiers are skipped by the caller's regex starting at `class`/`static`, so what is left in
     * front is attributes and space.
     */
    private fun annotated(text: CharSequence, offset: Int): Boolean {
        var at = backOverModifiers(text, offset)
        while (at > 0) {
            at = skipSpaceBack(text, at)
            if (at <= 0 || text[at - 1] != ']') return false
            val close = at - 1
            val open = lastIndexOf(text, '[', close)
            if (open < 0) return false
            if (PREVIEW.containsMatchIn(text.subSequence(open, close + 1))) return true
            at = open
        }
        return false
    }

    private val PREVIEW = Regex("""\[\s*Preview\b""")

    // `public sealed partial class X` — the regexes start at `class`/`static`, so the words in front
    // of them are modifiers, and an attribute is whatever is in front of those.
    private fun backOverModifiers(text: CharSequence, offset: Int): Int {
        var at = offset
        while (true) {
            val before = skipSpaceBack(text, at)
            if (before <= 0) return before
            var word = before
            while (word > 0 && text[word - 1].isLetter()) word--
            if (word == before) return before // not a word — an attribute's ']', or a brace
            if (text.subSequence(word, before).toString() !in MODIFIERS) return before
            at = word
        }
    }

    private val MODIFIERS = setOf(
        "public", "internal", "private", "protected", "sealed", "abstract", "static", "partial",
        "file", "readonly", "ref", "unsafe", "new", "override", "virtual", "async",
    )

    private fun skipSpaceBack(text: CharSequence, from: Int): Int {
        var at = from
        while (at > 0 && text[at - 1].isWhitespace()) at--
        return at
    }

    private fun lastIndexOf(text: CharSequence, c: Char, before: Int): Int {
        for (i in before - 1 downTo 0) if (text[i] == c) return i
        return -1
    }
}
