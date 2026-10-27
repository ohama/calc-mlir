module EvalTests

open System
open Expecto
open Ast
open FsCheck

let calc input = Program.eval (Program.parse input)

let cases =
    [ "1 + 2", 3
      "2 + 3 * 4", 14
      "(2 + 3) * 4", 20
      "10 - 3 - 2", 5
      "-5 + 2", -3
      "7 / 2", 3
      "-7 / 2", -3
      "2 + 3 * (4 - 1)", 11
      // let 바인딩
      "let x = 5 in x * 2", 10
      "let x = 1 + 2 in x * x", 9
      "let x = 2 in let y = x * 3 in x + y", 8
      "let x = 1 in let x = 2 in x", 2
      "let x = 1 in (let x = 2 in x) + x", 3
      "(let x = 1 in x) + 10", 11
      "-let x = 1 in x + 2", -3 ]

[<Tests>]
let tests =
    testList "Eval" [
        testList "식 계산" [
            for input, expected in cases ->
                test input { Expect.equal (calc input) expected input }
        ]

        test "0으로 나누기" {
            Expect.throwsT<DivideByZeroException> (fun () -> calc "1 / 0" |> ignore) ""
        }

        test "정의되지 않은 변수" {
            Expect.throwsC
                (fun () -> calc "y + 1" |> ignore)
                (fun ex -> Expect.equal ex.Message "Unbound variable: y" "")
        }

        test "let 범위 밖에서는 변수 사용 불가" {
            Expect.throws (fun () -> calc "(let x = 1 in x) + x" |> ignore) ""
        }

        test "evalIn: 주어진 환경에서 평가" {
            let env = Map.ofList [ "a", 10; "b", 3 ]
            Expect.equal (Program.evalIn env (Program.parse "a - b")) 7 ""
        }

        testList "속성" [
            testProperty "let x = n in x = n" <| fun (NonNegativeInt n) ->
                calc (sprintf "let x = %d in x" n) = n

            testProperty "let x = a in let y = b in x + y" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                calc (sprintf "let x = %d in let y = %d in x + y" a b) = a + b

            testProperty "a + b" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                calc (sprintf "%d + %d" a b) = a + b

            testProperty "a * b" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                calc (sprintf "%d * %d" a b) = a * b

            testProperty "-a" <| fun (NonNegativeInt a) ->
                calc (sprintf "-%d" a) = -a

            testProperty "AST 직접 평가: Add" <| fun (a: int) (b: int) ->
                Program.eval (Add (Number a, Number b)) = a + b
        ]
    ]
