module EvalTests

open System
open Expecto
open Ast
open Eval
open FsCheck

let calc input = eval (Program.parse input)

let cases =
    [ "1 + 2", VInt 3
      "2 + 3 * 4", VInt 14
      "(2 + 3) * 4", VInt 20
      "10 - 3 - 2", VInt 5
      "-5 + 2", VInt -3
      "7 / 2", VInt 3
      "-7 / 2", VInt -3
      "2 + 3 * (4 - 1)", VInt 11
      // let 바인딩
      "let x = 5 in x * 2", VInt 10
      "let x = 1 + 2 in x * x", VInt 9
      "let x = 2 in let y = x * 3 in x + y", VInt 8
      "let x = 1 in let x = 2 in x", VInt 2
      "let x = 1 in (let x = 2 in x) + x", VInt 3
      "(let x = 1 in x) + 10", VInt 11
      "-let x = 1 in x + 2", VInt -3
      // 불리언
      "true", VBool true
      "false", VBool false
      "not true", VBool false
      "not not true", VBool true
      // 비교
      "1 = 1", VBool true
      "1 = 2", VBool false
      "true = true", VBool true
      "true = false", VBool false
      "3 <> 4", VBool true
      "false <> false", VBool false
      "1 < 2", VBool true
      "2 > 1", VBool true
      "2 <= 2", VBool true
      "1 >= 2", VBool false
      "1 + 2 < 4", VBool true
      "1 - 2 = -1", VBool true
      // 논리 연산
      "true && false", VBool false
      "true || false", VBool true
      "1 < 2 && 3 > 4", VBool false
      "true || false && false", VBool true
      "not true = false", VBool true
      "not (1 = 2)", VBool true
      // if-then-else
      "if true then 1 else 2", VInt 1
      "if false then 1 else 2", VInt 2
      "if 1 < 2 then 10 else 20", VInt 10
      "if false then 1 else 2 + 3", VInt 5
      "if true then if false then 1 else 2 else 3", VInt 2
      "(if true then 1 else 2) + 10", VInt 11
      "let x = 5 in if x > 3 then x * 2 else 0", VInt 10
      "let b = 1 < 2 in if b then 10 else 20", VInt 10
      "let x = 1 = 1 in x", VBool true
      "if 1 = 1 then true else false", VBool true ]

/// 오류 메시지가 정확히 일치하는지 확인
let expectError input message =
    test input {
        Expect.throwsC (fun () -> calc input |> ignore) (fun ex -> Expect.equal ex.Message message input)
    }

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

        expectError "y + 1" "Unbound variable: y"

        test "let 범위 밖에서는 변수 사용 불가" {
            Expect.throws (fun () -> calc "(let x = 1 in x) + x" |> ignore) ""
        }

        test "evalIn: 주어진 환경에서 평가" {
            let env = Map.ofList [ "a", VInt 10; "b", VInt 3; "c", VBool true ]
            Expect.equal (evalIn env (Program.parse "if c then a - b else 0")) (VInt 7) ""
        }

        testList "단락 평가" [
            test "false && <오류> 는 false" {
                Expect.equal (calc "false && 1 / 0 = 0") (VBool false) ""
            }
            test "true || <오류> 는 true" {
                Expect.equal (calc "true || 1 / 0 = 0") (VBool true) ""
            }
            test "선택되지 않은 if 분기는 평가하지 않음" {
                Expect.equal (calc "if true then 1 else 1 / 0") (VInt 1) ""
            }
        ]

        testList "타입 오류" [
            expectError "1 + true" "Type error: expected int but got bool"
            expectError "-true" "Type error: expected int but got bool"
            expectError "true < false" "Type error: expected int but got bool"
            expectError "1 = true" "Type error: expected int but got bool"
            expectError "true = 1" "Type error: expected bool but got int"
            expectError "not 1" "Type error: expected bool but got int"
            expectError "1 && true" "Type error: expected bool but got int"
            expectError "if 1 then 2 else 3" "Type error: expected bool but got int"
            expectError "1 < 2 < 3" "Type error: expected int but got bool"
        ]

        test "if 분기의 타입이 달라도 선택된 분기 값만 반환" {
            Expect.equal (calc "if true then 1 else false") (VInt 1) ""
        }

        test "formatValue" {
            Expect.equal (formatValue (VInt -3)) "-3" ""
            Expect.equal (formatValue (VBool true)) "true" ""
            Expect.equal (formatValue (VBool false)) "false" ""
        }

        testList "속성" [
            testProperty "a + b" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                calc (sprintf "%d + %d" a b) = VInt (a + b)

            testProperty "a * b" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                calc (sprintf "%d * %d" a b) = VInt (a * b)

            testProperty "-a" <| fun (NonNegativeInt a) ->
                calc (sprintf "-%d" a) = VInt -a

            testProperty "AST 직접 평가: Add" <| fun (a: int) (b: int) ->
                eval (Add (Number a, Number b)) = VInt (a + b)

            testProperty "let x = n in x = n" <| fun (NonNegativeInt n) ->
                calc (sprintf "let x = %d in x" n) = VInt n

            testProperty "let x = a in let y = b in x + y" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                calc (sprintf "let x = %d in let y = %d in x + y" a b) = VInt (a + b)

            testProperty "a < b 는 F# 의 < 와 같음" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                calc (sprintf "%d < %d" a b) = VBool (a < b)

            testProperty "a = b 는 F# 의 = 와 같음" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                calc (sprintf "%d = %d" a b) = VBool (a = b)

            testProperty "&& 와 || 는 F# 와 같음" <| fun (a: bool) (b: bool) ->
                let s (x: bool) = if x then "true" else "false"
                calc (sprintf "%s && %s" (s a) (s b)) = VBool (a && b)
                && calc (sprintf "%s || %s" (s a) (s b)) = VBool (a || b)

            testProperty "if 는 조건에 따라 분기" <| fun (c: bool) (NonNegativeInt a) (NonNegativeInt b) ->
                let cond = if c then "true" else "false"
                calc (sprintf "if %s then %d else %d" cond a b) = VInt (if c then a else b)
        ]
    ]
