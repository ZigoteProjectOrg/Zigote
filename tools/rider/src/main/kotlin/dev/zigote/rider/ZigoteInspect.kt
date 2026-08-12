package dev.zigote.rider

import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket

/**
 * The client half of `Zigote.UI.Host.InspectServer`: open a socket, send a word, read a line of JSON.
 *
 * Free of IntelliJ imports on purpose — this is the part with logic worth testing, and it is testable
 * against a fake server in a plain JVM test. Everything above it is wiring.
 *
 * The JSON reader is hand-written rather than Gson or kotlinx.serialization. Both are *present* in the
 * IDE's internal jars and both compile against it, but neither is a supported part of the plugin
 * classpath — the failure mode is a `NoClassDefFoundError` at runtime, in a panel, on someone else's
 * machine. The replies here are five shapes produced by code in this same repository, so a reader for
 * exactly those is smaller than the risk.
 */
object ZigoteInspect {

    private const val TIMEOUT_MS = 10_000
    private const val PROBE_MS = 200

    /** True when something is listening — used to wait for an app that is still building. */
    fun reachable(port: Int): Boolean = try {
        Socket().use {
            it.connect(InetSocketAddress(InetAddress.getLoopbackAddress(), port), PROBE_MS)
            true
        }
    } catch (_: IOException) {
        false
    }

    /** Send one command, return the parsed reply. Blocking — callers belong off the EDT. */
    fun query(port: Int, command: String): Map<String, Any?> {
        val text = raw(port, command)
        val parsed = Json.parse(text)
        return parsed as? Map<String, Any?>
            ?: throw IOException("expected a JSON object, got: ${text.take(120)}")
    }

    /**
     * Open the persistent frame stream: `stream` on the same socket, one JSON header line back, then
     * each frame as a 4-byte big-endian length + BMP bytes, pushed only when the picture changed.
     *
     * Blocks until the socket dies — run it on its own thread. [register] receives the open socket so
     * the owner can close it to stop the stream. Returns false immediately when the server predates
     * the command (it answers `{"error":…}`), so the caller can fall back to polling `shot`.
     */
    fun stream(port: Int, register: (Socket) -> Unit, onFrame: (ByteArray) -> Unit): Boolean {
        Socket(InetAddress.getLoopbackAddress(), port).use { socket ->
            register(socket)
            socket.getOutputStream().apply {
                write("stream\n".toByteArray())
                flush()
            }

            val input = socket.getInputStream()
            val header = StringBuilder()
            while (true) {
                val c = input.read()
                if (c < 0) return false
                if (c == '\n'.code) break
                header.append(c.toChar())
            }
            if (header.contains("\"error\"")) return false

            val length = ByteArray(4)
            while (true) {
                readFully(input, length)
                val size = ((length[0].toInt() and 0xFF) shl 24) or
                    ((length[1].toInt() and 0xFF) shl 16) or
                    ((length[2].toInt() and 0xFF) shl 8) or
                    (length[3].toInt() and 0xFF)
                // A frame bigger than this is a torn stream, not a picture; stop before allocating it.
                if (size <= 0 || size > 256 shl 20) return true
                val frame = ByteArray(size)
                readFully(input, frame)
                onFrame(frame)
            }
        }
    }

    private fun readFully(input: java.io.InputStream, into: ByteArray) {
        var at = 0
        while (at < into.size) {
            val n = input.read(into, at, into.size - at)
            if (n < 0) throw IOException("stream closed mid-frame")
            at += n
        }
    }

    /** The unparsed reply, for tests and for anything that wants the bytes. */
    fun raw(port: Int, command: String): String {
        Socket(InetAddress.getLoopbackAddress(), port).use { socket ->
            socket.soTimeout = TIMEOUT_MS
            socket.getOutputStream().apply {
                write((command + "\n").toByteArray())
                flush()
            }
            // The server answers and closes, so "everything until EOF" is the whole reply.
            return socket.getInputStream().readBytes().toString(Charsets.UTF_8).trim()
        }
    }

    // ── a JSON reader for the five shapes above ──────────────────────────────

    /**
     * Objects become `Map<String, Any?>`, arrays `List<Any?>`, numbers `Double`, and the rest what you
     * would expect. No pretty errors, no streaming, no big-number handling: it reads what
     * `InspectServer` writes.
     */
    object Json {
        fun parse(text: String): Any? {
            val reader = Reader(text)
            val value = reader.value()
            reader.skipSpace()
            if (!reader.done) throw IOException("trailing text at ${reader.at}")
            return value
        }

        private class Reader(private val s: String) {
            var at = 0

            val done get() = at >= s.length

            fun skipSpace() {
                while (at < s.length && s[at].isWhitespace()) at++
            }

            fun value(): Any? {
                skipSpace()
                if (done) throw IOException("unexpected end of reply")
                return when (s[at]) {
                    '{' -> obj()
                    '[' -> arr()
                    '"' -> string()
                    't' -> literal("true", true)
                    'f' -> literal("false", false)
                    'n' -> literal("null", null)
                    else -> number()
                }
            }

            private fun obj(): Map<String, Any?> {
                val map = LinkedHashMap<String, Any?>()
                at++ // {
                skipSpace()
                if (s[at] == '}') { at++; return map }
                while (true) {
                    skipSpace()
                    val key = string()
                    skipSpace()
                    expect(':')
                    map[key] = value()
                    skipSpace()
                    if (s[at] == ',') { at++; continue }
                    expect('}')
                    return map
                }
            }

            private fun arr(): List<Any?> {
                val list = ArrayList<Any?>()
                at++ // [
                skipSpace()
                if (s[at] == ']') { at++; return list }
                while (true) {
                    list += value()
                    skipSpace()
                    if (s[at] == ',') { at++; continue }
                    expect(']')
                    return list
                }
            }

            private fun string(): String {
                expect('"')
                val out = StringBuilder()
                while (true) {
                    when (val c = s[at++]) {
                        '"' -> return out.toString()
                        '\\' -> when (val e = s[at++]) {
                            '"', '\\', '/' -> out.append(e)
                            'n' -> out.append('\n')
                            'r' -> out.append('\r')
                            't' -> out.append('\t')
                            'b' -> out.append('\b')
                            'f' -> out.append('')
                            'u' -> {
                                out.append(s.substring(at, at + 4).toInt(16).toChar())
                                at += 4
                            }

                            else -> throw IOException("bad escape \\$e at $at")
                        }

                        else -> out.append(c)
                    }
                }
            }

            private fun number(): Double {
                val start = at
                while (at < s.length && (s[at].isDigit() || s[at] in "-+.eE")) at++
                return s.substring(start, at).toDoubleOrNull()
                    ?: throw IOException("bad number at $start")
            }

            private fun <T> literal(word: String, value: T): T {
                if (!s.startsWith(word, at)) throw IOException("bad literal at $at")
                at += word.length
                return value
            }

            private fun expect(c: Char) {
                if (done || s[at] != c) throw IOException("expected '$c' at $at")
                at++
            }
        }
    }
}

// ── reply accessors ───────────────────────────────────────────────────────────
//
// The replies are maps; these keep the casts in one place instead of scattered through the panels.

fun Map<String, Any?>.error(): String? = this["error"] as? String

fun Map<String, Any?>.strings(key: String): List<String> =
    (this[key] as? List<*>)?.filterIsInstance<String>() ?: emptyList()

fun Map<String, Any?>.node(key: String): Map<String, Any?>? {
    @Suppress("UNCHECKED_CAST")
    return this[key] as? Map<String, Any?>
}

fun Map<String, Any?>.children(): List<Map<String, Any?>> {
    @Suppress("UNCHECKED_CAST")
    return (this["children"] as? List<*>)?.filterIsInstance<Map<String, Any?>>() ?: emptyList()
}

fun Map<String, Any?>.text(key: String): String? = this[key] as? String

fun Map<String, Any?>.int(key: String): Int = (this[key] as? Double)?.toInt() ?: 0
