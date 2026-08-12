package dev.zigote.rider

/**
 * Zigote colour literals in C# source, found by pattern rather than by resolving symbols.
 *
 * Rider keeps the C# PSI in the ReSharper backend, so a frontend plugin cannot ask what type
 * `new Color(0xFF2196F3)` is without a second, backend half built against the ReSharper SDK. Matching
 * the text instead costs one regex and no SDK, and the thing being asked for — "show me what that
 * number looks like" — needs no type information to be right. The trade is that a `Color` from some
 * other library with the same call shape also gets a swatch, which is harmless.
 *
 * This file is deliberately free of IntelliJ imports: it is the part worth testing, and it is what a
 * port to another editor would reuse.
 */
object ZigoteColors {

    /** Which call shape a literal was written in, so an edit is written back in the same shape. */
    enum class Form(val supportsAlpha: Boolean) {
        /** `new Color(0xAARRGGBB)` / `Color.FromHex(0xAARRGGBB)` */
        HEX(true),

        /** `new Color(r, g, b)` / `new Color(r, g, b, a)` with 0..1 floats */
        FLOATS(true),

        /** `Color.Rgb(r, g, b)` — 0..255, opaque by definition */
        RGB(false),

        /** `Color.Rgba(r, g, b, a)` — 0..255 channels, 0..1 alpha */
        RGBA(true),
    }

    /**
     * One literal. [start]/[end] bound the *arguments only*, never the surrounding call, so replacing
     * that span rewrites the value without having to reproduce whichever spelling introduced it.
     */
    data class Literal(val start: Int, val end: Int, val argb: Int, val form: Form)

    private const val NUM = """-?\d+(?:\.\d+)?"""

    private val HEX = Regex("""(?:new\s+Color|Color\s*\.\s*FromHex)\s*\(\s*(0[xX][0-9a-fA-F]{1,8})\s*\)""")
    private val FLOATS = Regex("""new\s+Color\s*\(\s*($NUM[fF]?\s*,\s*$NUM[fF]?\s*,\s*$NUM[fF]?(?:\s*,\s*$NUM[fF]?)?)\s*\)""")
    private val RGB = Regex("""Color\s*\.\s*Rgb\s*\(\s*(\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3})\s*\)""")
    private val RGBA = Regex("""Color\s*\.\s*Rgba\s*\(\s*(\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*$NUM[fF]?)\s*\)""")

    /** Every colour literal in [text], in document order. */
    fun scan(text: CharSequence): List<Literal> {
        val found = ArrayList<Literal>()
        collect(HEX, text, Form.HEX, found)
        collect(FLOATS, text, Form.FLOATS, found)
        collect(RGB, text, Form.RGB, found)
        collect(RGBA, text, Form.RGBA, found)
        return found.sortedBy { it.start }
    }

    private fun collect(regex: Regex, text: CharSequence, form: Form, into: MutableList<Literal>) {
        for (match in regex.findAll(text)) {
            val args = match.groupValues[1]
            val argb = parse(args, form) ?: continue
            val group = match.groups[1]!!.range
            into += Literal(group.first, group.last + 1, argb, form)
        }
    }

    /** The 0xAARRGGBB value the arguments denote, or null when they are not a colour after all. */
    fun parse(args: String, form: Form): Int? {
        // Block body, not `= when(...)`: the branches bail out with `return null`, which Kotlin only
        // allows in a function that has one.
        return when (form) {
            Form.HEX -> {
                val digits = args.substring(2)
                val value = digits.toLongOrNull(16) ?: return null
                // Six digits or fewer never carried an alpha, so reading the top byte as one would
                // make every `new Color(0x2196F3)` render as fully transparent.
                if (digits.length > 6) value.toInt() else (value.toInt() or (0xFF shl 24))
            }

            Form.FLOATS -> {
                val parts = split(args) ?: return null
                if (parts.size !in 3..4) return null
                val f = parts.map { it.toFloatOrNull() ?: return null }
                pack(
                    if (f.size == 4) unit(f[3]) else 255,
                    unit(f[0]),
                    unit(f[1]),
                    unit(f[2]),
                )
            }

            Form.RGB, Form.RGBA -> {
                val parts = split(args) ?: return null
                if (parts.size != if (form == Form.RGB) 3 else 4) return null
                val c = (0..2).map { parts[it].toIntOrNull()?.takeIf { v -> v in 0..255 } ?: return null }
                val a = if (form == Form.RGBA) unit(parts[3].toFloatOrNull() ?: return null) else 255
                pack(a, c[0], c[1], c[2])
            }
        }
    }

    /** The argument text for [argb] in [form] — the inverse of [parse], for writing an edit back. */
    fun format(argb: Int, form: Form): String {
        val a = (argb ushr 24) and 0xFF
        val r = (argb shr 16) and 0xFF
        val g = (argb shr 8) and 0xFF
        val b = argb and 0xFF
        return when (form) {
            Form.HEX -> "0x%08X".format(argb)
            // Three components while the colour is opaque: adding `, 1f` to every edit would churn
            // source that never asked about alpha.
            Form.FLOATS ->
                if (a == 255) "%sf, %sf, %sf".format(f(r), f(g), f(b))
                else "%sf, %sf, %sf, %sf".format(f(r), f(g), f(b), f(a))

            Form.RGB -> "$r, $g, $b"
            Form.RGBA -> "$r, $g, $b, %sf".format(f(a))
        }
    }

    private fun f(channel: Int): String =
        "%.3f".format(channel / 255f).trimEnd('0').trimEnd('.').ifEmpty { "0" }

    private fun split(args: String): List<String>? {
        val parts = args.split(',').map { it.trim().removeSuffix("f").removeSuffix("F").trim() }
        return if (parts.any { it.isEmpty() }) null else parts
    }

    private fun unit(v: Float): Int = (v.coerceIn(0f, 1f) * 255f + 0.5f).toInt()

    private fun pack(a: Int, r: Int, g: Int, b: Int): Int =
        (a shl 24) or (r shl 16) or (g shl 8) or b
}
