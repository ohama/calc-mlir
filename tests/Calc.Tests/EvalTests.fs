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
      "2 + 3 * (4 - 1)", 11 ]

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

        testList "속성" [
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
