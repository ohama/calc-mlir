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
