namespace Zigote.Modules.UI.CodeEditor

open System
open System.Collections.Generic
open Zigote.UI.TextShaping
open XParsec
open XParsec.Parsers
open XParsec.CharParsers

// The carried lexer state (XParsec user state):  are we inside an unterminated  /* … */  block?
type private S = bool

/// A line-scoped XParsec parser over string input.
type private P<'a> = Parser<'a, char, S, ReadableString>

// ─────────────────────────────────────────────────────────────────────────────
// Parser primitives — each token parser consumes ≥1 char and FAILS at eof, so the
// tokenize loop in LineHighlighter always makes progress. XParsec's <|> / choice
// always backtrack, so no `attempt` wrapping is needed.
// ─────────────────────────────────────────────────────────────────────────────
module private P =

    /// Bracket a body parser with reader indices → a Token spanning what it consumed.
    let span (kind: TokenKind) (body: P<unit>) : P<Token> =
        fun reader ->
            let s = reader.Index

            match body reader with
            | Ok() -> Ok(Token(s, reader.Index - s, kind))
            | Error e -> Error e

    /// Input is a single line, so "rest of line" is everything up to eof.
    let skipToEol: P<unit> = skipManySatisfies (fun _ -> true)

    // ── Comments ──────────────────────────────────────────────────────────────

    let lineCommentP: P<Token> =
        span TokenKind.Comment (stringReturn "//" () >>. skipToEol)

    /// Body of a block comment: consume up to and including «*/» and clear the
    /// user state, or consume the rest of the line and set it.
    let private blockBody: P<unit> =
        (skipManyTill skip (stringReturn "*/" ()) >>. setUserState false)
        <|> (skipToEol >>. setUserState true)

    /// «/*» opening on this line. Sets user state = true iff «*/» is not also on this line.
    let blockCommentOpenP: P<Token> =
        span TokenKind.Comment (stringReturn "/*" () >>. blockBody)

    /// Continuation of a block comment opened on a previous line (used only as a line prefix
    /// when the incoming state is "in block"). Clears the flag when it finds the closing «*/».
    let blockContinuationP: P<Token> = span TokenKind.Comment blockBody

    // ── String / char literals ──────────────────────────────────────────────

    let private quoted (quote: char) : P<unit> =
        let escaped = skipChar '\\' >>. skipAnyChar
        let normal = satisfy (fun c -> c <> quote && c <> '\\') >>% ()
        skipChar quote >>. skipMany (escaped <|> normal) >>. (skipChar quote <|> eof)

    let stringP (quote: char) : P<Token> = span TokenKind.String (quoted quote)

    /// C# verbatim string  @"…"  where  ""  is an embedded quote.
    let verbatimStringP: P<Token> =
        span
            TokenKind.String
            (stringReturn "@\"" ()
             >>. skipMany (stringReturn "\"\"" () <|> (satisfy (fun c -> c <> '"') >>% ()))
             >>. (skipChar '"' <|> eof))

    // ── Numbers ────────────────────────────────────────────────────────────────

    let numberP: P<Token> =
        let digitOr_ c = Char.IsAsciiDigit c || c = '_'
        let hexOr_ c = Char.IsAsciiHexDigit c || c = '_'
        let binOr_ c = c = '0' || c = '1' || c = '_'

        let body =
            (stringReturn "0x" () >>. skipMany1Satisfies hexOr_)
            <|> (stringReturn "0b" () >>. skipMany1Satisfies binOr_)
            <|> (skipMany1Satisfies digitOr_
                 >>. optional (skipChar '.' >>. skipManySatisfies digitOr_)
                 >>. optional (
                     skipAnyOf "eE" >>. optional (skipAnyOf "+-")
                     >>. skipMany1Satisfies Char.IsAsciiDigit
                 )
                 >>. skipManySatisfies "fFdDmMuUlL".Contains)

        span TokenKind.Number body

    // ── Identifiers / keywords / types ───────────────────────────────────────

    let private expectedIdent = Message "identifier"

    /// A hand-rolled primitive: scans the word in place and classifies it via
    /// allocation-free span lookups into the keyword / type sets.
    let identP (kw: HashSet<string>) (ty: HashSet<string>) : P<Token> =
        let kwL = kw.GetAlternateLookup<ReadOnlySpan<char>>()
        let tyL = ty.GetAlternateLookup<ReadOnlySpan<char>>()
        let isStart c = Char.IsLetter c || c = '_' || c = '@'
        let isCont c = Char.IsLetterOrDigit c || c = '_'

        fun reader ->
            match reader.Peek() with
            | ValueSome c when isStart c ->
                let start = reader.Index
                reader.Skip()
                skipManySatisfies isCont reader |> ignore
                let word = reader.Input.AsSpan(start, reader.Index - start)

                let kind =
                    if kwL.Contains word then TokenKind.Keyword
                    elif tyL.Contains word then TokenKind.Type
                    elif Char.IsUpper word[0] then TokenKind.Type // PascalCase heuristic
                    else TokenKind.Default

                Ok(Token(start, word.Length, kind))
            | _ -> fail expectedIdent reader

    // ── Operators & punctuation ───────────────────────────────────────────────

    let operatorP: P<Token> =
        span TokenKind.Operator (skipMany1Satisfies "+-*/%=<>!&|^~?".Contains)

    let punctP: P<Token> = span TokenKind.Punctuation (skipAnyOf "()[]{},:;.")

    let catchAllP: P<Token> = span TokenKind.Default skipAnyChar

// ─────────────────────────────────────────────────────────────────────────────
// Language grammars
// ─────────────────────────────────────────────────────────────────────────────
module private Grammar =

    let csKw =
        HashSet
            [
                "abstract"
                "as"
                "base"
                "break"
                "case"
                "catch"
                "checked"
                "class"
                "const"
                "continue"
                "default"
                "delegate"
                "do"
                "else"
                "enum"
                "event"
                "explicit"
                "extern"
                "false"
                "finally"
                "fixed"
                "for"
                "foreach"
                "goto"
                "if"
                "implicit"
                "in"
                "interface"
                "internal"
                "is"
                "lock"
                "namespace"
                "new"
                "null"
                "operator"
                "out"
                "override"
                "params"
                "private"
                "protected"
                "public"
                "readonly"
                "ref"
                "return"
                "sealed"
                "sizeof"
                "stackalloc"
                "static"
                "struct"
                "switch"
                "this"
                "throw"
                "true"
                "try"
                "typeof"
                "unchecked"
                "unsafe"
                "using"
                "virtual"
                "volatile"
                "while"
                "async"
                "await"
                "var"
                "yield"
                "get"
                "set"
                "value"
                "when"
                "where"
                "nameof"
                "partial"
                "record"
                "init"
                "with"
                "global"
                "and"
                "or"
                "not"
                "required"
                "file"
            ]

    let csTy =
        HashSet
            [
                "bool"
                "byte"
                "sbyte"
                "char"
                "decimal"
                "double"
                "float"
                "int"
                "uint"
                "long"
                "ulong"
                "short"
                "ushort"
                "object"
                "string"
                "void"
                "nint"
                "nuint"
                "dynamic"
                "Span"
                "List"
                "Dictionary"
                "Action"
                "Func"
                "Task"
                "Color"
                "Vec2"
                "Vec3"
                "Vec4"
                "Mat4"
                "Quat"
                "Rect"
                "Size"
                "Offset"
            ]

    let wgslKw =
        HashSet
            [
                "alias"
                "break"
                "case"
                "const"
                "continue"
                "continuing"
                "default"
                "discard"
                "else"
                "enable"
                "false"
                "fn"
                "for"
                "if"
                "let"
                "loop"
                "override"
                "return"
                "struct"
                "switch"
                "true"
                "var"
                "while"
                "private"
                "function"
                "workgroup"
                "uniform"
                "storage"
                "read"
                "write"
                "read_write"
                "fragment"
                "vertex"
                "compute"
                "ptr"
                "requires"
            ]

    let wgslTy =
        HashSet
            [
                "bool"
                "i32"
                "u32"
                "f32"
                "f16"
                "vec2"
                "vec3"
                "vec4"
                "vec2f"
                "vec3f"
                "vec4f"
                "vec2i"
                "vec3i"
                "vec4i"
                "vec2u"
                "vec3u"
                "vec4u"
                "mat2x2"
                "mat3x3"
                "mat4x4"
                "mat2x2f"
                "mat3x3f"
                "mat4x4f"
                "array"
                "atomic"
                "sampler"
                "sampler_comparison"
                "texture_2d"
                "texture_2d_array"
                "texture_cube"
                "texture_cube_array"
                "texture_3d"
                "texture_depth_2d"
                "texture_storage_2d"
                "texture_multisampled_2d"
            ]

    let zigKw =
        HashSet
            [
                "addrspace"
                "align"
                "allowzero"
                "and"
                "anyframe"
                "anytype"
                "asm"
                "async"
                "await"
                "break"
                "callconv"
                "catch"
                "comptime"
                "const"
                "continue"
                "defer"
                "else"
                "enum"
                "errdefer"
                "error"
                "export"
                "extern"
                "false"
                "fn"
                "for"
                "if"
                "inline"
                "noalias"
                "nosuspend"
                "noinline"
                "opaque"
                "or"
                "orelse"
                "packed"
                "pub"
                "resume"
                "return"
                "linksection"
                "struct"
                "suspend"
                "switch"
                "test"
                "threadlocal"
                "true"
                "try"
                "undefined"
                "union"
                "unreachable"
                "usingnamespace"
                "var"
                "volatile"
                "while"
                "null"
            ]

    let zigTy =
        HashSet
            [
                "bool"
                "void"
                "type"
                "anyerror"
                "anyopaque"
                "c_int"
                "c_uint"
                "c_long"
                "c_ulong"
                "c_short"
                "c_ushort"
                "c_char"
                "isize"
                "usize"
                "comptime_int"
                "comptime_float"
                "i8"
                "i16"
                "i32"
                "i64"
                "i128"
                "u8"
                "u16"
                "u32"
                "u64"
                "u128"
                "f16"
                "f32"
                "f64"
                "f80"
                "f128"
            ]

    let jsonKw = HashSet [ "true"; "false"; "null" ]
    let empty = HashSet<string>()

    // C# — block comments, verbatim + regular strings, char literals.
    let csLine: P<Token> =
        choiceL
            [
                P.lineCommentP
                P.blockCommentOpenP
                P.verbatimStringP
                P.stringP '"'
                P.stringP '\''
                P.numberP
                P.identP csKw csTy
                P.operatorP
                P.punctP
                P.catchAllP
            ]
            "token"

    // WGSL — line comments only (shader code, no string literals worth lexing).
    let wgslLine: P<Token> =
        choiceL
            [
                P.lineCommentP
                P.numberP
                P.identP wgslKw wgslTy
                P.operatorP
                P.punctP
                P.catchAllP
            ]
            "token"

    // Zig — line comments, string literals.
    let zigLine: P<Token> =
        choiceL
            [
                P.lineCommentP
                P.stringP '"'
                P.numberP
                P.identP zigKw zigTy
                P.operatorP
                P.punctP
                P.catchAllP
            ]
            "token"

    // JSON — a string immediately followed by «:» is an object key (rendered as a Type).
    let private jsonStringP: P<Token> =
        P.stringP '"'
        >>= fun tok ->
            ((skipManySatisfies (fun c -> c = ' ' || c = '\t') >>. lookAhead (skipChar ':'))
             >>% Token(tok.Start, tok.Length, TokenKind.Type))
            <|> preturn tok

    let jsonLine: P<Token> =
        choiceL
            [
                jsonStringP
                P.numberP
                P.identP jsonKw empty
                P.punctP
                P.operatorP
                P.catchAllP
            ]
            "token"

// ─────────────────────────────────────────────────────────────────────────────
// ILineTokenizer adapter — drives an XParsec token parser over one line, streaming
// tokens straight into the caller's list (no intermediate token collections).
// ─────────────────────────────────────────────────────────────────────────────
type private LineHighlighter(supportsBlock: bool, tokenP: P<Token>) =
    interface ILineTokenizer with
        member _.Tokenize(line: string, state: int, output: List<Token>) =
            let reader = Reader.ofString line (state = 1)

            if supportsBlock && reader.State then
                match P.blockContinuationP reader with
                | Ok t -> output.Add t
                | Error _ -> ()

            while not reader.AtEnd do
                skipManySatisfies (fun c -> c = ' ' || c = '\t') reader |> ignore

                if not reader.AtEnd then
                    match tokenP reader with
                    | Ok t -> output.Add t
                    | Error _ -> reader.Skip() // unreachable (catch-all is total); keep making progress

            if reader.State then 1 else 0

// ─────────────────────────────────────────────────────────────────────────────
// Public factory — consumed by C# (the editor) and the F# demo.
// ─────────────────────────────────────────────────────────────────────────────
/// Shared tokenizer singletons — initialized with the rest of the file's bindings,
/// in declaration order (a type cctor here can re-enter the file initializer).
module private Tokenizers =
    let csharp: ILineTokenizer = LineHighlighter(true, Grammar.csLine)
    let wgsl: ILineTokenizer = LineHighlighter(false, Grammar.wgslLine)
    let zig: ILineTokenizer = LineHighlighter(false, Grammar.zigLine)
    let json: ILineTokenizer = LineHighlighter(false, Grammar.jsonLine)

[<AbstractClass; Sealed>]
type Highlighting private () =
    static member CSharp = Tokenizers.csharp
    static member Wgsl = Tokenizers.wgsl
    static member Zig = Tokenizers.zig
    static member Json = Tokenizers.json

    /// An <c>ILineTokenizer</c> for the given file extension (with or without the leading dot),
    /// or <c>null</c> for plain-text / unknown files (the editor then renders unhighlighted text).
    static member ForExtension(ext: string) : ILineTokenizer =
        if String.IsNullOrEmpty ext then
            null
        else
            let e = if ext.StartsWith '.' then ext[1..] else ext

            match e.ToLowerInvariant() with
            | "cs" -> Highlighting.CSharp
            | "wgsl"
            | "glsl"
            | "vert"
            | "frag"
            | "comp" -> Highlighting.Wgsl
            | "zig" -> Highlighting.Zig
            | "json" -> Highlighting.Json
            | _ -> null
