package dev.zigote.rider

import java.net.URLEncoder

/**
 * One editable property of a preview: a constructor or factory parameter the app reported a default
 * for. [kind] is what to draw — `string`, `bool`, `int`, `number`, `enum` — decided in the app, so
 * this side needs no table of .NET type names.
 */
internal data class PreviewParam(
    val name: String,
    val kind: String,
    val value: String,
    val options: List<String> = emptyList(),
)

/**
 * One thing the running app can show, as `previews` describes it.
 *
 * Defaulted all the way down so a `targets` reply from an app built before `previews` existed becomes
 * a list of these with nothing attached — the panel then has one kind of item to handle instead of two.
 */
internal data class PreviewTarget(
    val target: String,
    val label: String? = null,
    val group: String? = null,
    val annotated: Boolean = false,
    val width: Int = 0,
    val height: Int = 0,
    val theme: String? = null,
    val params: List<PreviewParam> = emptyList(),
) {
    /** What the combo shows: the name `[Preview("…")]` gave it, or the type it was found as. */
    val display: String get() = label ?: target

    override fun toString(): String = display
}

/**
 * Reading the `previews` reply, and writing the spec that shows one.
 *
 * Free of Swing and IntelliJ on purpose, like [ZigoteInspect] — this is the part with rules in it
 * (which values are worth sending, how they are escaped), and rules are what a plain JVM test can hold.
 */
internal object Previews {

    fun parse(reply: Map<String, Any?>): List<PreviewTarget> =
        (reply["previews"] as? List<*>).orEmpty()
            .filterIsInstance<Map<String, Any?>>()
            .mapNotNull { row ->
                val name = row.text("target") ?: return@mapNotNull null
                PreviewTarget(
                    target = name,
                    label = row.text("label"),
                    group = row.text("group"),
                    annotated = row["annotated"] == true,
                    width = row.int("w"),
                    height = row.int("h"),
                    theme = row.text("theme"),
                    params = (row["params"] as? List<*>).orEmpty()
                        .filterIsInstance<Map<String, Any?>>()
                        .mapNotNull { it.param() },
                )
            }

    private fun Map<String, Any?>.param(): PreviewParam? {
        val name = text("name") ?: return null
        return PreviewParam(
            name = name,
            kind = text("kind") ?: "string",
            value = text("value") ?: "",
            options = strings("options"),
        )
    }

    /**
     * The argument for `preview` — and the same string `ZIGOTE_PREVIEW` takes, which is why it is
     * built here rather than assembled inline: one shape, one escaping, both ends of the contract.
     *
     * Only values that differ from the declared default travel. A spec of nothing but a type name is
     * the app's own defaults, so pressing Reset leaves no trace of the property editor having been
     * open — and a default that changes in the source is picked up on the next reload instead of being
     * pinned to whatever it used to be.
     */
    fun spec(target: PreviewTarget, values: Map<String, String>): String {
        val query = target.params
            .mapNotNull { param -> values[param.name]?.takeIf { it != param.value }?.let { param to it } }
            .joinToString("&") { (param, value) -> "${encode(param.name)}=${encode(value)}" }
        return if (query.isEmpty()) target.target else "${target.target}?$query"
    }

    // '+' for a space is what the app decodes; a literal '+' goes out as %2B, which is the point.
    private fun encode(text: String): String = URLEncoder.encode(text, "UTF-8")
}
