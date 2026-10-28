module Program

open System.Reflection
open Argu
open FSharp.Text.Lexing
open Ast

[<HelpFlags("--help", "-h")>]
type CliArgs =
    | [<AltCommandLine("-e"); EqualsAssignmentOrSpaced>] Expr of expr: string
    | [<AltCommandLine("-v")>] Version
    interface IArgParserTemplate with
        member a.Usage =
            match a with
            | Expr _ -> "evaluate the given expression (reads stdin if omitted)"
            | Version -> "print version and exit"

let parse (input: string) : Expr =
    let lexbuf = LexBuffer<char>.FromString input
    try
        Parser.start Lexer.tokenize lexbuf
    with Errors.ParseFailure ->
        // 오류를 낸 토큰은 마지막으로 읽은 토큰 (StartPos ~ EndPos)
        let startPos, endPos = lexbuf.StartPos, lexbuf.EndPos
        if startPos.AbsoluteOffset = endPos.AbsoluteOffset then
            // 입력 끝: 끝의 공백·줄바꿈을 건너뛴 마지막 글자 바로 뒤를 가리킴
            let offset = input.TrimEnd().Length
            let before = input.Substring(0, offset)
            let line = before |> Seq.filter ((=) '\n') |> Seq.length
            let column = offset - (before.LastIndexOf '\n' + 1)
            raise (Errors.SyntaxError ("parse error: unexpected end of input", line + 1, column + 1))
        else
            let token = input.Substring(startPos.AbsoluteOffset, endPos.AbsoluteOffset - startPos.AbsoluteOffset)
            Errors.syntaxError (sprintf "parse error: unexpected '%s'" token) startPos

/// Calc.fsproj 의 <Version> 값 (빌드 메타데이터 "+<git hash>" 는 제거)
let version () =
    let attr =
        Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    attr.InformationalVersion.Split('+').[0]

/// 평가기는 재귀 호출 깊이만큼 .NET 스택을 사용하고, StackOverflow 는 catch 할 수 없음.
/// 기본 스택(1MB 내외)으로는 재귀 깊이 수천에서 프로세스가 죽으므로 큰 스택의 스레드에서 실행.
let evalStackSize = 512 * 1024 * 1024

let runWithLargeStack (f: unit -> 'T) : 'T =
    let mutable result = Unchecked.defaultof<'T>
    let mutable error = None
    let thread =
        System.Threading.Thread(
            (fun () ->
                try result <- f ()
                with ex -> error <- Some ex),
            evalStackSize)
    thread.Start()
    thread.Join()
    match error with
    | Some ex -> raise ex
    | None -> result

/// 오류가 난 줄과 그 아래 ^ 표시. 탭은 그대로 두어야 ^ 가 같은 열에 맞음
let errorLocation (input: string) line column =
    let lines = input.Split('\n')
    if line < 1 || line > lines.Length then []
    else
        let text = lines.[line - 1].TrimEnd('\r')
        let prefix = text.Substring(0, min (column - 1) text.Length)
        let padding = prefix |> String.map (fun c -> if c = '\t' then '\t' else ' ')
        let gutter = string line
        [ sprintf "  %s | %s" gutter text
          sprintf "  %s | %s^" (String.replicate gutter.Length " ") padding ]

let run (input: string) =
    try
        let ast = parse input
        printfn "AST:    %A" ast
        let value = runWithLargeStack (fun () -> Eval.eval ast)
        printfn "Result: %s" (Eval.formatValue value)
        0
    with
    | Errors.SyntaxError (_, line, column) as ex ->
        eprintfn "Error: %s" ex.Message
        errorLocation input line column |> List.iter (eprintfn "%s")
        1
    | ex ->
        eprintfn "Error: %s" ex.Message
        1

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<CliArgs>(programName = "calc")
    try
        let results = parser.ParseCommandLine(argv)
        if results.Contains Version then
            printfn "calc %s" (version ())
            0
        else
            match results.TryGetResult Expr with
            | Some expr -> run expr
            | None -> run (stdin.ReadToEnd())
    with :? ArguParseException as ex ->
        match ex.ErrorCode with
        | ErrorCode.HelpText ->
            printfn "%s" ex.Message
            0
        | _ ->
            eprintfn "%s" ex.Message
            1
