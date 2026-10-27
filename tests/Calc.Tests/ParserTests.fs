module ParserTests

open Expecto
open Ast

let parse = Program.parse

[<Tests>]
let tests =
    testList "Parser" [
        test "정수" {
            Expect.equal (parse "7") (Number 7) ""
        }
        test "*가 +보다 우선" {
            Expect.equal
                (parse "2 + 3 * 4")
                (Add (Number 2, Multiply (Number 3, Number 4)))
                ""
        }
        test "/가 -보다 우선" {
            Expect.equal
                (parse "8 - 6 / 2")
                (Subtract (Number 8, Divide (Number 6, Number 2)))
                ""
        }
        test "괄호" {
            Expect.equal
                (parse "(2 + 3) * 4")
                (Multiply (Add (Number 2, Number 3), Number 4))
                ""
        }
        test "- 는 왼쪽 결합" {
            Expect.equal
                (parse "10 - 3 - 2")
                (Subtract (Subtract (Number 10, Number 3), Number 2))
                ""
        }
        test "/ 는 왼쪽 결합" {
            Expect.equal
                (parse "8 / 4 / 2")
                (Divide (Divide (Number 8, Number 4), Number 2))
                ""
        }
        test "단항 마이너스는 이항 연산보다 우선" {
            Expect.equal
                (parse "-5 + 2")
                (Add (Negate (Number 5), Number 2))
                ""
        }
        test "중첩 단항 마이너스" {
            Expect.equal (parse "--3") (Negate (Negate (Number 3))) ""
        }
        test "변수" {
            Expect.equal (parse "x") (Var "x") ""
        }
        test "let 식" {
            Expect.equal (parse "let x = 5 in x") (Let ("x", Number 5, Var "x")) ""
        }
        test "let 본문은 오른쪽으로 최대한 확장" {
            Expect.equal
                (parse "let x = 1 in x + 2")
                (Let ("x", Number 1, Add (Var "x", Number 2)))
                ""
        }
        test "let 바인딩 값에 식" {
            Expect.equal
                (parse "let x = 1 + 2 in x")
                (Let ("x", Add (Number 1, Number 2), Var "x"))
                ""
        }
        test "중첩 let" {
            Expect.equal
                (parse "let x = 1 in let y = 2 in x + y")
                (Let ("x", Number 1, Let ("y", Number 2, Add (Var "x", Var "y"))))
                ""
        }
        test "괄호로 let 범위 제한" {
            Expect.equal
                (parse "(let x = 1 in x) + 10")
                (Add (Let ("x", Number 1, Var "x"), Number 10))
                ""
        }
        test "이항 연산 오른쪽의 let" {
            Expect.equal
                (parse "1 + let x = 2 in x")
                (Add (Number 1, Let ("x", Number 2, Var "x")))
                ""
        }
        test "in 없는 let 은 parse error" {
            Expect.throws (fun () -> parse "let x = 1" |> ignore) ""
        }
        test "키워드는 변수 이름이 될 수 없음" {
            Expect.throws (fun () -> parse "let in = 1 in 2" |> ignore) ""
        }
        test "불완전한 식은 parse error" {
            Expect.throwsC
                (fun () -> parse "1 +" |> ignore)
                (fun ex -> Expect.equal ex.Message "parse error" "")
        }
        test "닫히지 않은 괄호는 parse error" {
            Expect.throws (fun () -> parse "(1 + 2" |> ignore) ""
        }
        test "빈 입력은 parse error" {
            Expect.throws (fun () -> parse "" |> ignore) ""
        }
    ]
