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

let rec eval expr =
    match expr with
    | Number n -> n
    | Add (a, b) -> eval a + eval b
    | Subtract (a, b) -> eval a - eval b
    | Multiply (a, b) -> eval a * eval b
    | Divide (a, b) -> eval a / eval b
    | Negate e -> -(eval e)

let parse (input: string) : Expr =
    let lexbuf = LexBuffer<char>.FromString input
    Parser.start Lexer.tokenize lexbuf

/// Calc.fsproj 의 <Version> 값 (빌드 메타데이터 "+<git hash>" 는 제거)
let version () =
    let attr =
        Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    attr.InformationalVersion.Split('+').[0]

let run (input: string) =
    try
        let ast = parse input
        printfn "AST:    %A" ast
        printfn "Result: %d" (eval ast)
        0
    with ex ->
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
