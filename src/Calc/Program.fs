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

type Env = Map<string, int>

let rec evalIn (env: Env) expr =
    match expr with
    | Number n -> n
    | Var name ->
        match Map.tryFind name env with
        | Some v -> v
        | None -> failwithf "Unbound variable: %s" name
    | Let (name, value, body) ->
        let v = evalIn env value
        evalIn (Map.add name v env) body
    | Add (a, b) -> evalIn env a + evalIn env b
    | Subtract (a, b) -> evalIn env a - evalIn env b
    | Multiply (a, b) -> evalIn env a * evalIn env b
    | Divide (a, b) -> evalIn env a / evalIn env b
    | Negate e -> -(evalIn env e)

/// 빈 환경에서 평가
let eval expr = evalIn Map.empty expr

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
