namespace Zigote.Modules.UI.CodeEditor

open System
open System.Collections.Generic
open Zigote.UI.TextShaping
open FParsec

// The carried lexer state (FParsec user state):  are we inside an unterminated  /* … */  block?
type private S = bool

// ─────────────────────────────────────────────────────────────────────────────
// Parser primitives — each token parser consumes ≥1 char and FAILS at eof, so the
// `many` driver in buildLine always terminates (no "succeeded without consuming" trap).
// ─────────────────────────────────────────────────────────────────────────────
module private P =

    let idx: Parser<int, S> = getPosition |>> fun p -> int p.Index

    /// Bracket a body parser with positions → a Token spanning what it consumed.
    let span (kind: TokenKind) (body: Parser<unit, S>) : Parser<Token, S> =
        pipe3 idx body idx (fun s () e -> Token(s, e - s, kind))

    // ── Comments ──────────────────────────────────────────────────────────────

    let lineCommentP: Parser<Token, S> =
        span TokenKind.Comment (skipString "//" >>. skipRestOfLine false)

    /// «/*» opening on this line. Sets user state = true iff «*/» is not also on this line.
    let blockCommentOpenP: Parser<Token, S> =
        span
            TokenKind.Comment
            (skipString "/*"
             >>. (attempt ((manyCharsTill anyChar (skipString "*/") >>% ()) >>. setUserState false)
                  <|> (skipRestOfLine false >>. setUserState true)))

    /// Continuation of a block comment opened on a previous line (used only as a line prefix
    /// when the incoming state is "in block"). Clears the flag when it finds the closing «*/».
    let blockContinuationP: Parser<Token, S> =
        span
            TokenKind.Comment
            (attempt ((manyCharsTill anyChar (skipString "*/") >>% ()) >>. setUserState false)
             <|> (skipRestOfLine false >>. setUserState true))

    // ── String / char literals ──────────────────────────────────────────────

    let private quoted (quote: char) : Parser<unit, S> =
        let escaped = attempt (skipChar '\\' >>. skipAnyChar)
        let normal = satisfy (fun c -> c <> quote && c <> '\\') >>% ()
        skipChar quote >>. skipMany (escaped <|> normal) >>. (skipChar quote <|> eof)

    let stringP (quote: char) : Parser<Token, S> = span TokenKind.String (quoted quote)

    /// C# verbatim string  @"…"  where  ""  is an embedded quote.
    let verbatimStringP: Parser<Token, S> =
        span
            TokenKind.String
            (skipString "@\""
             >>. skipMany (attempt (skipString "\"\"") <|> (satisfy (fun c -> c <> '"') >>% ()))
             >>. (skipChar '"' <|> eof))

    // ── Numbers ────────────────────────────────────────────────────────────────

    let numberP: Parser<Token, S> =
        let body =
            attempt (skipString "0x" >>. skipMany1 ((hex <|> pchar '_') >>% ()))
            <|> attempt (skipString "0b" >>. skipMany1 (anyOf "01_" >>% ()))
            <|> (skipMany1 ((digit <|> pchar '_') >>% ())
                 >>. (opt (skipChar '.' >>. skipMany ((digit <|> pchar '_') >>% ())) |>> ignore)
                 >>. (opt (
                          anyOf "eE" >>. (opt (anyOf "+-") |>> ignore) >>. skipMany1 (digit >>% ())
                      )
                      |>> ignore)
                 >>. skipMany (anyOf "fFdDmMuUlL" >>% ()))

        span TokenKind.Number body

    // ── Identifiers / keywords / types ───────────────────────────────────────

    let identP (kw: Set<string>) (ty: Set<string>) : Parser<Token, S> =
        let isStart c = Char.IsLetter c || c = '_' || c = '@'
        let isCont c = Char.IsLetterOrDigit c || c = '_'

        pipe2 idx (many1Satisfy2 isStart isCont) (fun start word ->
            let kind =
                if Set.contains word kw then
                    TokenKind.Keyword
                elif Set.contains word ty then
                    TokenKind.Type
                elif word.Length > 0 && Char.IsUpper word[0] then
                    TokenKind.Type // PascalCase heuristic
                else
                    TokenKind.Default

            Token(start, word.Length, kind))

    // ── Operators & punctuation ───────────────────────────────────────────────

    let operatorP: Parser<Token, S> =
        span TokenKind.Operator (skipMany1 (satisfy (fun c -> "+-*/%=<>!&|^~?".Contains c) >>% ()))

    let punctP: Parser<Token, S> = span TokenKind.Punctuation (anyOf "()[]{},:;." >>% ())

    let wsP: Parser<Token option, S> = skipMany1 (anyOf " \t" >>% ()) >>% None

    let catchAllP: Parser<Token, S> = span TokenKind.Default skipAnyChar

    /// Build a whole-line parser from a token `choice`. `supportsBlock` prepends a block-comment
    /// continuation when the incoming user state says we're inside one.
    let buildLine (supportsBlock: bool) (choices: Parser<Token option, S>) : Parser<Token list, S> =
        let prefix: Parser<Token list, S> =
            if supportsBlock then
                getUserState
                >>= fun inBlock ->
                    if inBlock then
                        blockContinuationP |>> List.singleton
                    else
                        preturn ([]: Token list)
            else
                preturn ([]: Token list)

        pipe2 prefix ((many choices) .>> eof) (fun pre rest -> pre @ List.choose id rest)

// ─────────────────────────────────────────────────────────────────────────────
// Language grammars
// ─────────────────────────────────────────────────────────────────────────────
module private Grammar =

    let csKw =
        Set.ofList
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
        Set.ofList
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
        Set.ofList
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
        Set.ofList
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
        Set.ofList
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
        Set.ofList
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

    let jsonKw = Set.ofList [ "true"; "false"; "null" ]
    let empty: Set<string> = Set.empty

    // C# — block comments, verbatim + regular strings, char literals.
    let csLine =
        P.buildLine
            true
            (choice
                [
                    P.wsP
                    attempt P.lineCommentP |>> Some
                    attempt P.blockCommentOpenP |>> Some
                    attempt P.verbatimStringP |>> Some
                    attempt (P.stringP '"') |>> Some
                    attempt (P.stringP '\'') |>> Some
                    attempt P.numberP |>> Some
                    attempt (P.identP csKw csTy) |>> Some
                    attempt P.operatorP |>> Some
                    attempt P.punctP |>> Some
                    P.catchAllP |>> Some
                ])

    // WGSL — line comments only (shader code, no string literals worth lexing).
    let wgslLine =
        P.buildLine
            false
            (choice
                [
                    P.wsP
                    attempt P.lineCommentP |>> Some
                    attempt P.numberP |>> Some
                    attempt (P.identP wgslKw wgslTy) |>> Some
                    attempt P.operatorP |>> Some
                    attempt P.punctP |>> Some
                    P.catchAllP |>> Some
                ])

    // Zig — line comments, string literals.
    let zigLine =
        P.buildLine
            false
            (choice
                [
                    P.wsP
                    attempt P.lineCommentP |>> Some
                    attempt (P.stringP '"') |>> Some
                    attempt P.numberP |>> Some
                    attempt (P.identP zigKw zigTy) |>> Some
                    attempt P.operatorP |>> Some
                    attempt P.punctP |>> Some
                    P.catchAllP |>> Some
                ])

    // JSON — a string immediately followed by «:» is an object key (rendered as a Type).
    let private jsonStringP: Parser<Token, S> =
        let strSpan =
            P.span
                TokenKind.String
                (skipChar '"'
                 >>. skipMany (
                     attempt (skipChar '\\' >>. skipAnyChar)
                     <|> (satisfy (fun c -> c <> '"') >>% ())
                 )
                 >>. (skipChar '"' <|> eof))

        strSpan
        >>= fun tok ->
            (attempt (skipMany (anyOf " \t" >>% ()) >>. followedBy (skipChar ':'))
             >>% Token(tok.Start, tok.Length, TokenKind.Type))
            <|> preturn tok

    let jsonLine =
        P.buildLine
            false
            (choice
                [
                    P.wsP
                    attempt jsonStringP |>> Some
                    attempt P.numberP |>> Some
                    attempt (P.identP jsonKw empty) |>> Some
                    attempt P.punctP |>> Some
                    attempt P.operatorP |>> Some
                    P.catchAllP |>> Some
                ])

// ─────────────────────────────────────────────────────────────────────────────
// ILineTokenizer adapter — bridges an FParsec line parser to the C# widget contract.
// ─────────────────────────────────────────────────────────────────────────────
type private LineHighlighter(parser: Parser<Token list, S>) =
    interface ILineTokenizer with
        member _.Tokenize(line: string, state: int, output: List<Token>) =
            match runParserOnString parser (state = 1) "line" line with
            | Success(tokens, finalState, _) ->
                for t in tokens do
                    output.Add t

                if finalState then 1 else 0
            | Failure _ -> state // shouldn't happen (catch-all is total); carry state forward

// ─────────────────────────────────────────────────────────────────────────────
// Public factory — consumed by C# (the editor) and the F# demo.
// ─────────────────────────────────────────────────────────────────────────────
[<AbstractClass; Sealed>]
type Highlighting private () =
    [<DefaultValue>]
    static val mutable private csharp: ILineTokenizer

    [<DefaultValue>]
    static val mutable private wgsl: ILineTokenizer

    [<DefaultValue>]
    static val mutable private zig: ILineTokenizer

    [<DefaultValue>]
    static val mutable private json: ILineTokenizer

    static member CSharp =
        if isNull Highlighting.csharp then
            Highlighting.csharp <- BuiltInCodeTokenizer(CodeLanguage.CSharp)

        Highlighting.csharp

    static member Wgsl =
        if isNull Highlighting.wgsl then
            Highlighting.wgsl <- BuiltInCodeTokenizer(CodeLanguage.Wgsl)

        Highlighting.wgsl

    static member Zig =
        if isNull Highlighting.zig then
            Highlighting.zig <- BuiltInCodeTokenizer(CodeLanguage.Zig)

        Highlighting.zig

    static member Json =
        if isNull Highlighting.json then
            Highlighting.json <- BuiltInCodeTokenizer(CodeLanguage.Json)

        Highlighting.json

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
