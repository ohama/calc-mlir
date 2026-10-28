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

/// SyntaxError 의 메시지와 위치(1부터)를 확인
let expectLexError input message line column =
    Expect.throwsC
        (fun () -> tokens input |> ignore)
        (fun ex ->
            match ex with
            | Errors.SyntaxError (m, l, c) -> Expect.equal (m, l, c) (message, line, column) input
            | _ -> failtestf "SyntaxError 가 아님: %s" (ex.GetType().Name))

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
            Expect.equal (tokens "_a foo_1 x2") [ IDENT "_a"; IDENT "foo_1"; IDENT "x2"; EOF ] ""
        }
        test "대문자로 시작하면 생성자" {
            Expect.equal (tokens "X2 Some None_1") [ CTOR "X2"; CTOR "Some"; CTOR "None_1"; EOF ] ""
            Expect.equal (tokens "Let True") [ CTOR "Let"; CTOR "True"; EOF ] ""
        }
        test "타입 선언 토큰" {
            Expect.equal
                (tokens "type 'a t = A of 'a")
                [ TYPE; TYVAR "a"; IDENT "t"; EQUALS; CTOR "A"; OF; TYVAR "a"; EOF ]
                ""
        }
        test "키워드로 시작하는 식별자: typed, often" {
            Expect.equal (tokens "typed often") [ IDENT "typed"; IDENT "often"; EOF ] ""
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
        test "보간 문자열 $\"...\": 글자는 포맷(% 는 %%), 구멍은 %A 또는 붙인 지정자, 중괄호 두 번은 글자" {
            Expect.equal
                (tokens "$\"a {x} %d{y + 1} {{}} 5%\"")
                [ INTERP ("a %A %d {} 5%%", [ Ast.Var "x"; Ast.Add (Ast.Var "y", Ast.Number 1) ]); EOF ]
                ""
            Expect.equal (tokens "$\"{\"}\"}\"") [ INTERP ("%A", [ Ast.Str "}" ]); EOF ] "구멍 안 문자열의 } 는 세지 않음"
            expectLexError "$\"a } b\"" "Unmatched } in interpolated string (use }} for a brace)" 1 5
            expectLexError "$\"a {}\"" "Empty expression in interpolated string" 1 6
            expectLexError "$\"a {1 +}\"" "parse error: unexpected end of interpolated expression" 1 9
            expectLexError "$\"abc" "Unterminated string" 1 1
        }
        test "try, exception 키워드 (raise, failwith 는 보통 이름)" {
            Expect.equal
                (tokens "exception E try raise failwith")
                [ EXCEPTION; CTOR "E"; TRY; IDENT "raise"; IDENT "failwith"; EOF ]
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
        test "_ 하나는 UNDERSCORE, 더 길면 IDENT" {
            Expect.equal (tokens "_ _a a_ __") [ UNDERSCORE; IDENT "_a"; IDENT "a_"; IDENT "__"; EOF ] ""
        }
        test "쉼표와 when" {
            Expect.equal
                (tokens "(a, b) when")
                [ LPAREN; IDENT "a"; COMMA; IDENT "b"; RPAREN; WHEN; EOF ]
                ""
        }
        test "키워드로 시작하는 식별자: whenever" {
            Expect.equal (tokens "whenever") [ IDENT "whenever"; EOF ] ""
        }
        test "키워드로 시작하는 식별자: matches, without" {
            Expect.equal (tokens "matches without") [ IDENT "matches"; IDENT "without"; EOF ] ""
        }
        test "문자열" {
            Expect.equal (tokens "\"hello\" \"\"") [ STRING "hello"; STRING ""; EOF ] ""
        }
        test "문자열 안의 키워드와 기호는 그대로" {
            Expect.equal (tokens "\"let x = [1] // \"") [ STRING "let x = [1] // "; EOF ] ""
        }
        test "문자열 이스케이프" {
            Expect.equal (tokens "\"a\\\"b\\\\c\\nd\\te\"") [ STRING "a\"b\\c\nd\te"; EOF ] ""
        }
        test "문자열: 유니코드" {
            Expect.equal (tokens "\"안녕 😀\"") [ STRING "안녕 😀"; EOF ] ""
        }
        test "문자열 안의 줄바꿈은 그대로 포함" {
            Expect.equal (tokens "\"a\nb\"") [ STRING "a\nb"; EOF ] ""
        }
        test "닫히지 않은 문자열: 여는 \" 위치" {
            expectLexError "\"abc" "Unterminated string" 1 1
            expectLexError "1 + \"abc" "Unterminated string" 1 5
        }
        test "알 수 없는 이스케이프: \\ 위치" {
            expectLexError "\"a\\qb\"" "Invalid escape sequence: \\q" 1 3
        }
        test "문자열 끝의 \\ 는 닫는 \" 를 이스케이프" {
            expectLexError "\"abc\\\"" "Unterminated string" 1 1
        }
        test ": 는 타입 표기, :: 는 cons (가장 긴 토큰)" {
            Expect.equal (tokens "(x : 'a) :: :::") [ LPAREN; IDENT "x"; COLON; TYVAR "a"; RPAREN; CONS; CONS; COLON; EOF ] ""
        }
        test "& 하나는 알 수 없는 문자" {
            expectLexError "1 & 2" "Unexpected character: &" 1 3
        }
        test "알 수 없는 문자" {
            expectLexError "2 $ 3" "Unexpected character: $" 1 3
        }
        test "범위를 넘는 정수 리터럴" {
            expectLexError "1 + 2147483648" "Integer literal out of range: 2147483648" 1 5
        }
        test "int 최댓값 리터럴은 허용" {
            Expect.equal (tokens "2147483647") [ NUMBER 2147483647; EOF ] ""
        }
        test "오류 위치: 여러 줄" {
            expectLexError "1 +\n  2 $" "Unexpected character: $" 2 5
        }
        test "오류 위치: \\r\\n 줄바꿈" {
            expectLexError "1\r\n\r\n $" "Unexpected character: $" 3 2
        }
        test "오류 위치: 문자열 안의 줄바꿈도 줄 수에 포함" {
            expectLexError "\"a\nb\" $" "Unexpected character: $" 2 4
        }
        test "오류 위치: 탭은 한 칸" {
            expectLexError "\t$" "Unexpected character: $" 1 2
        }
        test "Message 에 위치 포함" {
            Expect.throwsC
                (fun () -> tokens "2 $ 3" |> ignore)
                (fun ex -> Expect.equal ex.Message "Unexpected character: $ (line 1, column 3)" "")
        }
    ]
