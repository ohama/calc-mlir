module LexerTests

open Expecto
open FSharp.Text.Lexing
open Parser

/// 입력 문자열을 EOF 까지 토큰 리스트로 변환
let tokens (input: string) =
    let lexbuf = LexBuffer<char>.FromString input
    let rec loop acc =
        match Lexer.tokenize lexbuf with
        | EOF -> List.rev (EOF :: acc)
        | t -> loop (t :: acc)
    loop []

[<Tests>]
let tests =
    testList "Lexer" [
        test "정수" {
            Expect.equal (tokens "42") [ NUMBER 42; EOF ] ""
        }
        test "연산자와 괄호" {
            Expect.equal
                (tokens "( + - * / )")
                [ LPAREN; PLUS; MINUS; STAR; SLASH; RPAREN; EOF ]
                ""
        }
        test "공백 없는 식" {
            Expect.equal (tokens "1+2") [ NUMBER 1; PLUS; NUMBER 2; EOF ] ""
        }
        test "공백, 탭, 줄바꿈 무시" {
            Expect.equal (tokens " 1\t+\n2\r\n") [ NUMBER 1; PLUS; NUMBER 2; EOF ] ""
        }
        test "빈 입력" {
            Expect.equal (tokens "") [ EOF ] ""
        }
        test "let 식" {
            Expect.equal
                (tokens "let x = 1 in x")
                [ LET; IDENT "x"; EQUALS; NUMBER 1; IN; IDENT "x"; EOF ]
                ""
        }
        test "식별자: 밑줄과 숫자" {
            Expect.equal (tokens "_a foo_1 X2") [ IDENT "_a"; IDENT "foo_1"; IDENT "X2"; EOF ] ""
        }
        test "키워드로 시작하는 식별자는 IDENT" {
            Expect.equal (tokens "letx inner") [ IDENT "letx"; IDENT "inner"; EOF ] ""
        }
        test "숫자 뒤 식별자는 분리" {
            Expect.equal (tokens "2x") [ NUMBER 2; IDENT "x"; EOF ] ""
        }
        test "불리언과 if 키워드" {
            Expect.equal
                (tokens "if true then false else not x")
                [ IF; TRUE; THEN; FALSE; ELSE; NOT; IDENT "x"; EOF ]
                ""
        }
        test "비교 연산자" {
            Expect.equal
                (tokens "= <> < > <= >=")
                [ EQUALS; NOTEQUAL; LESS; GREATER; LESSEQUAL; GREATEREQUAL; EOF ]
                ""
        }
        test "비교 연산자: 공백 없이 (가장 긴 일치)" {
            Expect.equal (tokens "1<=2") [ NUMBER 1; LESSEQUAL; NUMBER 2; EOF ] ""
            Expect.equal (tokens "1<>2") [ NUMBER 1; NOTEQUAL; NUMBER 2; EOF ] ""
        }
        test "논리 연산자" {
            Expect.equal (tokens "a && b || c") [ IDENT "a"; AND; IDENT "b"; OR; IDENT "c"; EOF ] ""
        }
        test "키워드로 시작하는 식별자: iffy, trueish, notx" {
            Expect.equal
                (tokens "iffy trueish notx")
                [ IDENT "iffy"; IDENT "trueish"; IDENT "notx"; EOF ]
                ""
        }
        test "함수 키워드와 화살표" {
            Expect.equal
                (tokens "let rec f = fun x -> x")
                [ LET; REC; IDENT "f"; EQUALS; FUN; IDENT "x"; ARROW; IDENT "x"; EOF ]
                ""
        }
        test "-> 와 - 구분 (가장 긴 일치)" {
            Expect.equal (tokens "x->-1") [ IDENT "x"; ARROW; MINUS; NUMBER 1; EOF ] ""
        }
        test "키워드로 시작하는 식별자: funny, record" {
            Expect.equal (tokens "funny record") [ IDENT "funny"; IDENT "record"; EOF ] ""
        }
        test "리스트 토큰" {
            Expect.equal
                (tokens "[1; 2] :: []")
                [ LBRACKET; NUMBER 1; SEMI; NUMBER 2; RBRACKET; CONS; LBRACKET; RBRACKET; EOF ]
                ""
        }
        test "match 키워드와 |" {
            Expect.equal
                (tokens "match xs with | h :: t -> h")
                [ MATCH; IDENT "xs"; WITH; BAR; IDENT "h"; CONS; IDENT "t"; ARROW; IDENT "h"; EOF ]
                ""
        }
        test "| 와 || 구분 (가장 긴 일치)" {
            Expect.equal (tokens "| ||") [ BAR; OR; EOF ] ""
        }
        test "키워드로 시작하는 식별자: matches, without" {
            Expect.equal (tokens "matches without") [ IDENT "matches"; IDENT "without"; EOF ] ""
        }
        test ": 하나는 알 수 없는 문자" {
            Expect.throwsC
                (fun () -> tokens "1 : 2" |> ignore)
                (fun ex -> Expect.equal ex.Message "Unexpected character: :" "")
        }
        test "& 하나는 알 수 없는 문자" {
            Expect.throwsC
                (fun () -> tokens "1 & 2" |> ignore)
                (fun ex -> Expect.equal ex.Message "Unexpected character: &" "")
        }
        test "알 수 없는 문자" {
            Expect.throwsC
                (fun () -> tokens "2 $ 3" |> ignore)
                (fun ex -> Expect.equal ex.Message "Unexpected character: $" "")
        }
    ]
