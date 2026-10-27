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
        test "알 수 없는 문자" {
            Expect.throwsC
                (fun () -> tokens "2 $ 3" |> ignore)
                (fun ex -> Expect.equal ex.Message "Unexpected character: $" "")
        }
    ]
