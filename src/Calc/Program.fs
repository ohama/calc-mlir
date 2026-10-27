module Program

open FSharp.Text.Lexing
open Ast

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

[<EntryPoint>]
let main argv =
    let input =
        if argv.Length > 0 then String.concat " " argv
        else stdin.ReadToEnd()
    try
        let ast = parse input
        printfn "AST:    %A" ast
        printfn "Result: %d" (eval ast)
        0
    with ex ->
        eprintfn "Error: %s" ex.Message
        1
