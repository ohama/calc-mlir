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
        test "알 수 없는 문자" {
            Expect.throwsC
                (fun () -> tokens "2 $ 3" |> ignore)
                (fun ex -> Expect.equal ex.Message "Unexpected character: $" "")
        }
    ]
