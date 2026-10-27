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
        test "불리언 리터럴" {
            Expect.equal (parse "true") (Bool true) ""
            Expect.equal (parse "false") (Bool false) ""
        }
        test "산술이 비교보다 우선" {
            Expect.equal
                (parse "1 + 2 < 4")
                (Less (Add (Number 1, Number 2), Number 4))
                ""
        }
        test "비교 연산자 6종" {
            Expect.equal (parse "a = b") (Equal (Var "a", Var "b")) ""
            Expect.equal (parse "a <> b") (NotEqual (Var "a", Var "b")) ""
            Expect.equal (parse "a < b") (Less (Var "a", Var "b")) ""
            Expect.equal (parse "a > b") (Greater (Var "a", Var "b")) ""
            Expect.equal (parse "a <= b") (LessEqual (Var "a", Var "b")) ""
            Expect.equal (parse "a >= b") (GreaterEqual (Var "a", Var "b")) ""
        }
        test "비교는 왼쪽 결합" {
            Expect.equal
                (parse "1 < 2 < 3")
                (Less (Less (Number 1, Number 2), Number 3))
                ""
        }
        test "비교가 && 보다 우선" {
            Expect.equal
                (parse "a < b && c")
                (And (Less (Var "a", Var "b"), Var "c"))
                ""
        }
        test "&& 가 || 보다 우선" {
            Expect.equal
                (parse "a || b && c")
                (Or (Var "a", And (Var "b", Var "c")))
                ""
        }
        test "&&, || 는 왼쪽 결합" {
            Expect.equal (parse "a && b && c") (And (And (Var "a", Var "b"), Var "c")) ""
            Expect.equal (parse "a || b || c") (Or (Or (Var "a", Var "b"), Var "c")) ""
        }
        test "not 은 비교보다 우선" {
            Expect.equal
                (parse "not a = b")
                (Equal (Not (Var "a"), Var "b"))
                ""
        }
        test "if-then-else" {
            Expect.equal
                (parse "if c then 1 else 2")
                (If (Var "c", Number 1, Number 2))
                ""
        }
        test "else 분기는 오른쪽으로 최대한 확장" {
            Expect.equal
                (parse "if c then 1 else 2 + 3")
                (If (Var "c", Number 1, Add (Number 2, Number 3)))
                ""
        }
        test "then 분기 안의 if" {
            Expect.equal
                (parse "if a then if b then 1 else 2 else 3")
                (If (Var "a", If (Var "b", Number 1, Number 2), Number 3))
                ""
        }
        test "괄호로 if 범위 제한" {
            Expect.equal
                (parse "(if c then 1 else 2) + 10")
                (Add (If (Var "c", Number 1, Number 2), Number 10))
                ""
        }
        test "let 값으로 비교식" {
            Expect.equal
                (parse "let x = 1 = 1 in x")
                (Let ("x", Equal (Number 1, Number 1), Var "x"))
                ""
        }
        test "함수 호출" {
            Expect.equal (parse "f x") (App (Var "f", Var "x")) ""
        }
        test "함수 호출은 왼쪽 결합 (커링)" {
            Expect.equal (parse "f x y") (App (App (Var "f", Var "x"), Var "y")) ""
        }
        test "함수 호출이 이항 연산보다 우선" {
            Expect.equal
                (parse "f x + g y")
                (Add (App (Var "f", Var "x"), App (Var "g", Var "y")))
                ""
        }
        test "함수 호출이 단항 마이너스보다 우선" {
            Expect.equal (parse "-f x") (Negate (App (Var "f", Var "x"))) ""
        }
        test "f -1 은 뺄셈" {
            Expect.equal (parse "f -1") (Subtract (Var "f", Number 1)) ""
        }
        test "괄호로 인자 묶기" {
            Expect.equal
                (parse "f (g x)")
                (App (Var "f", App (Var "g", Var "x")))
                ""
            Expect.equal (parse "f (-1)") (App (Var "f", Negate (Number 1))) ""
        }
        test "람다" {
            Expect.equal (parse "fun x -> x") (Lambda ("x", Var "x")) ""
        }
        test "람다 본문은 오른쪽으로 최대한 확장" {
            Expect.equal
                (parse "fun x -> x + 1")
                (Lambda ("x", Add (Var "x", Number 1)))
                ""
        }
        test "여러 인자 람다는 중첩 람다" {
            Expect.equal
                (parse "fun x y -> x")
                (Lambda ("x", Lambda ("y", Var "x")))
                ""
        }
        test "람다 즉시 호출" {
            Expect.equal
                (parse "(fun x -> x) 1")
                (App (Lambda ("x", Var "x"), Number 1))
                ""
        }
        test "let 함수 정의는 람다의 문법 설탕" {
            Expect.equal
                (parse "let f x y = x + y in f 1 2")
                (Let ("f",
                      Lambda ("x", Lambda ("y", Add (Var "x", Var "y"))),
                      App (App (Var "f", Number 1), Number 2)))
                ""
        }
        test "let rec" {
            Expect.equal
                (parse "let rec f n = f n in f 1")
                (LetRec ("f", "n", App (Var "f", Var "n"), App (Var "f", Number 1)))
                ""
        }
        test "let rec 여러 인자" {
            Expect.equal
                (parse "let rec f a b = a in f")
                (LetRec ("f", "a", Lambda ("b", Var "a"), Var "f"))
                ""
        }
        test "인자 없는 let rec 은 parse error" {
            Expect.throws (fun () -> parse "let rec f = 1 in f" |> ignore) ""
        }
        test "인자 없는 fun 은 parse error" {
            Expect.throws (fun () -> parse "fun -> 1" |> ignore) ""
        }
        test "else 없는 if 는 parse error" {
            Expect.throws (fun () -> parse "if true then 1" |> ignore) ""
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
