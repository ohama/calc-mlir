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
      "if 1 = 1 then true else false", VBool true
      // 함수
      "let f x = x + 1 in f 41", VInt 42
      "(fun x -> x * 2) 21", VInt 42
      "let add x y = x + y in add 3 4", VInt 7
      "let f x y = x - y in f 10 3", VInt 7
      "let add x y = x + y in let inc = add 1 in inc 41", VInt 42
      "let g = fun x -> fun y -> x * y in g 6 7", VInt 42
      "let twice f x = f (f x) in twice (fun x -> x * 2) 5", VInt 20
      "let f x = x in f 1 + f 2", VInt 3
      "let f x = x in -f 3", VInt -3
      "let f x = x in f (-1)", VInt -1
      "let isPos n = n > 0 in isPos 5", VBool true
      "let x = 1 in let f y = x + y in let x = 100 in f 1", VInt 2
      "let f x = let x = x * 10 in x in f 3", VInt 30
      // 재귀
      "let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 10", VInt 3628800
      "let rec fib n = if n < 2 then n else fib (n - 1) + fib (n - 2) in fib 20", VInt 6765
      "let rec even n = if n = 0 then true else not (even (n - 1)) in even 7", VBool false
      "let rec pow b e = if e = 0 then 1 else b * pow b (e - 1) in pow 2 10", VInt 1024
      // rec 이 아니면 본문의 f 는 바깥 f 를 가리킴
      "let f n = n in let f n = f (n + 1) in f 1", VInt 2 ]

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

        testList "함수 오류" [
            expectError "1 2" "Type error: expected function but got int"
            expectError "let f x = x in f -1" "Type error: expected int but got function"
            expectError "(fun x -> x) = (fun x -> x)" "Type error: expected int or bool but got function"
            expectError "(fun x -> x) + 1" "Type error: expected int but got function"
            // let (rec 아님) 으로 정의한 함수는 자기 자신을 볼 수 없음
            expectError "let f x = f x in f 1" "Unbound variable: f"
        ]

        test "함수 값" {
            Expect.equal (formatValue (calc "fun x -> x")) "<fun>" ""
            Expect.equal (formatValue (calc "let rec f n = n in f")) "<fun>" ""
            Expect.equal (typeName (calc "fun x -> x")) "function" ""
        }

        test "클로저는 정의 시점의 환경을 저장" {
            match calc "let y = 5 in fun x -> x + y" with
            | VClosure ("x", _, env) -> Expect.equal (Map.tryFind "y" env) (Some (VInt 5)) ""
            | v -> failtestf "클로저가 아님: %A" v
        }

        test "인자는 호출 전에 평가 (값에 의한 호출)" {
            Expect.throwsT<DivideByZeroException>
                (fun () -> calc "let k x = 1 in k (1 / 0)" |> ignore) ""
        }

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

            testProperty "커링: add a b = a + b" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                calc (sprintf "let add x y = x + y in add %d %d" a b) = VInt (a + b)

            testProperty "재귀 합: sum n = n(n+1)/2" <| fun (NonNegativeInt n) ->
                calc (sprintf "let rec sum n = if n = 0 then 0 else n + sum (n - 1) in sum %d" n)
                    = VInt (n * (n + 1) / 2)

            testProperty "if 는 조건에 따라 분기" <| fun (c: bool) (NonNegativeInt a) (NonNegativeInt b) ->
                let cond = if c then "true" else "false"
                calc (sprintf "if %s then %d else %d" cond a b) = VInt (if c then a else b)
        ]
    ]
