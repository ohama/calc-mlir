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
      "let f n = n in let f n = f (n + 1) in f 1", VInt 2
      // 리스트
      "[]", VList []
      "[1; 2; 3]", VList [ VInt 1; VInt 2; VInt 3 ]
      "1 :: 2 :: []", VList [ VInt 1; VInt 2 ]
      "0 :: [1; 2]", VList [ VInt 0; VInt 1; VInt 2 ]
      "1 + 2 :: [3]", VList [ VInt 3; VInt 3 ]
      "[true; 1 < 2]", VList [ VBool true; VBool true ]
      "[[1]; []]", VList [ VList [ VInt 1 ]; VList [] ]
      "[1; 2] = 1 :: [2]", VBool true
      "[1] = [1; 2]", VBool false
      "[] = []", VBool true
      "[1] <> [2]", VBool true
      "[[1; 2]] = [[1; 2]]", VBool true
      "match [] with [] -> 0 | h :: t -> h", VInt 0
      "match [5; 6] with [] -> 0 | h :: t -> h", VInt 5
      "match [5; 6] with h :: t -> t | [] -> []", VList [ VInt 6 ]
      "let rec len xs = match xs with [] -> 0 | h :: t -> 1 + len t in len [1; 2; 3]", VInt 3
      "let rec sum xs = match xs with [] -> 0 | h :: t -> h + sum t in sum [1; 2; 3; 4]", VInt 10
      "let rec map f xs = match xs with [] -> [] | h :: t -> f h :: map f t in map (fun x -> x * x) [1; 2; 3]",
          VList [ VInt 1; VInt 4; VInt 9 ]
      "let rec append xs ys = match xs with [] -> ys | h :: t -> h :: append t ys in append [1; 2] [3]",
          VList [ VInt 1; VInt 2; VInt 3 ]
      "let rec range n = if n = 0 then [] else n :: range (n - 1) in range 3",
          VList [ VInt 3; VInt 2; VInt 1 ]
      // match 분기에서 바인딩한 이름은 바깥 이름을 가림
      "let h = 100 in match [1] with [] -> h | h :: t -> h", VInt 1
      // 문자열
      "\"hello\"", VString "hello"
      "\"\"", VString ""
      "\"a\" + \"b\"", VString "ab"
      "\"a\" + \"b\" + \"c\"", VString "abc"
      "\"안녕\" + \", 세계\"", VString "안녕, 세계"
      "\"say \\\"hi\\\"\"", VString "say \"hi\""
      "\"a\\nb\\tc\\\\\"", VString "a\nb\tc\\"
      "\"a\" = \"a\"", VBool true
      "\"a\" = \"b\"", VBool false
      "\"a\" <> \"b\"", VBool true
      "\"apple\" < \"banana\"", VBool true
      "\"abc\" >= \"abc\"", VBool true
      "\"ab\" < \"abc\"", VBool true
      // 문자 코드 순서: 대문자가 소문자보다 앞
      "\"B\" < \"a\"", VBool true
      "[\"a\"; \"b\"] = [\"a\"; \"b\"]", VBool true
      "let greet name = \"Hello, \" + name in greet \"F#\"", VString "Hello, F#"
      "let rec join xs = match xs with [] -> \"\" | h :: t -> h + join t in join [\"a\"; \"b\"]", VString "ab"
      "if \"a\" = \"a\" then \"yes\" else \"no\"", VString "yes"
      // 내장 함수
      "length \"hello\"", VInt 5
      "length \"\"", VInt 0
      "length \"안녕\"", VInt 2
      // UTF-16 코드 단위: 이모지는 서로게이트 쌍이라 2
      "length \"😀\"", VInt 2
      "length [1; 2; 3]", VInt 3
      "length []", VInt 0
      "substr \"hello\" 1 3", VString "ell"
      "substr \"hello\" 0 5", VString "hello"
      "substr \"hello\" 5 0", VString ""
      "upper \"abc\"", VString "ABC"
      "lower \"ABC\"", VString "abc"
      "trim \"  a b \\t\\n\"", VString "a b"
      "contains \"hello\" \"ell\"", VBool true
      "contains \"hello\" \"x\"", VBool false
      "contains \"hello\" \"\"", VBool true
      "startsWith \"hello\" \"he\"", VBool true
      "startsWith \"hello\" \"lo\"", VBool false
      "endsWith \"hello\" \"lo\"", VBool true
      "indexOf \"hello\" \"l\"", VInt 2
      "indexOf \"hello\" \"z\"", VInt -1
      "replace \"a-b-c\" \"-\" \"+\"", VString "a+b+c"
      "replace \"aaa\" \"a\" \"\"", VString ""
      "split \"a,b,,c\" \",\"", VList [ VString "a"; VString "b"; VString ""; VString "c" ]
      "split \"abc\" \"\"", VList [ VString "a"; VString "b"; VString "c" ]
      "split \"\" \",\"", VList [ VString "" ]
      "split \"a::b\" \"::\"", VList [ VString "a"; VString "b" ]
      "join \", \" [\"a\"; \"b\"]", VString "a, b"
      "join \",\" []", VString ""
      "join \"-\" (split \"2026-10-28\" \"-\")", VString "2026-10-28"
      "toString 42", VString "42"
      "toString (-5)", VString "-5"
      "toString true", VString "true"
      "toString \"x\"", VString "x"
      "toString [1; 2]", VString "[1; 2]"
      "toString [\"a\"]", VString "[\"a\"]"
      "toInt \"42\"", VInt 42
      "toInt \"-42\"", VInt -42
      "toInt (toString 123) + 1", VInt 124
      // 커링: 내장 함수도 부분 적용 가능
      "let first = substr \"hello\" 0 in first 2", VString "he"
      "let rec map f xs = match xs with [] -> [] | h :: t -> f h :: map f t in map upper (split \"a b\" \" \")",
          VList [ VString "A"; VString "B" ]
      "let rec map f xs = match xs with [] -> [] | h :: t -> f h :: map f t in map (contains \"abc\") [\"a\"; \"z\"]",
          VList [ VBool true; VBool false ]
      // 내장 함수 이름은 let 으로 가릴 수 있음
      "let length = 5 in length", VInt 5
      "let upper s = s + \"!\" in upper \"a\"", VString "a!" ]

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
            expectError "true < false" "Type error: expected int or string but got bool"
            expectError "1 = true" "Type error: expected int but got bool"
            expectError "true = 1" "Type error: expected bool but got int"
            expectError "not 1" "Type error: expected bool but got int"
            expectError "1 && true" "Type error: expected bool but got int"
            expectError "if 1 then 2 else 3" "Type error: expected bool but got int"
            expectError "1 < 2 < 3" "Type error: expected int or string but got bool"
        ]

        testList "함수 오류" [
            expectError "1 2" "Type error: expected function but got int"
            expectError "let f x = x in f -1" "Type error: expected int but got function"
            expectError "(fun x -> x) = (fun x -> x)" "Type error: expected int, bool, string or list but got function"
            expectError "(fun x -> x) + 1" "Type error: expected int or string but got function"
            // let (rec 아님) 으로 정의한 함수는 자기 자신을 볼 수 없음
            expectError "let f x = f x in f 1" "Unbound variable: f"
        ]

        testList "리스트 오류" [
            expectError "1 :: 2" "Type error: expected list but got int"
            expectError "match 1 with [] -> 0 | h :: t -> h" "Type error: expected list but got int"
            expectError "[1] = 1" "Type error: expected list but got int"
            expectError "1 = [1]" "Type error: expected int but got list"
            expectError "[1] < [2]" "Type error: expected int or string but got list"
            expectError "[1] + 1" "Type error: expected int or string but got list"
            expectError "[1] = [true]" "Type error: expected int but got bool"
            expectError "[1] 2" "Type error: expected function but got list"
        ]

        testList "문자열 오류" [
            expectError "\"a\" + 1" "Type error: expected string but got int"
            expectError "1 + \"a\"" "Type error: expected int but got string"
            expectError "true + 1" "Type error: expected int or string but got bool"
            expectError "\"a\" < 1" "Type error: expected string but got int"
            expectError "\"a\" = 1" "Type error: expected string but got int"
            expectError "1 = \"a\"" "Type error: expected int but got string"
            expectError "\"a\" - \"b\"" "Type error: expected int but got string"
            expectError "-\"a\"" "Type error: expected int but got string"
            expectError "if \"a\" then 1 else 2" "Type error: expected bool but got string"
            expectError "\"f\" 1" "Type error: expected function but got string"
        ]

        testList "내장 함수 오류" [
            expectError "length 1" "Type error: expected string or list but got int"
            expectError "upper 1" "Type error: expected string but got int"
            expectError "substr \"hello\" true 1" "Type error: expected int but got bool"
            expectError "substr \"hello\" 3 5" "substr: index out of range (length 5, start 3, count 5)"
            expectError "substr \"hello\" (-1) 2" "substr: index out of range (length 5, start -1, count 2)"
            expectError "substr \"hello\" 6 0" "substr: index out of range (length 5, start 6, count 0)"
            expectError "substr \"hello\" 0 (-1)" "substr: index out of range (length 5, start 0, count -1)"
            expectError "replace \"abc\" \"\" \"x\"" "replace: empty search string"
            expectError "join \",\" [1]" "Type error: expected string but got int"
            expectError "join \",\" \"a\"" "Type error: expected list but got string"
            expectError "toInt \"abc\"" "toInt: invalid integer: \"abc\""
            expectError "toInt \" 4\"" "toInt: invalid integer: \" 4\""
            expectError "toInt \"\"" "toInt: invalid integer: \"\""
            expectError "toInt \"2147483648\"" "toInt: invalid integer: \"2147483648\""
            expectError "length = length" "Type error: expected int, bool, string or list but got function"
            // 인자를 다 받기 전에는 실행하지 않음 (오류도 나중에)
            expectError "let f = substr 1 in f 2 3" "Type error: expected string but got int"
        ]

        test "내장 함수 값" {
            Expect.equal (formatValue (calc "length")) "<fun>" ""
            Expect.equal (formatValue (calc "substr \"a\"")) "<fun>" ""
            Expect.equal (typeName (calc "upper")) "function" ""
            Expect.equal (calc "substr \"abc\" 1") (VBuiltin ("substr", [ VString "abc"; VInt 1 ])) ""
        }

        test "evalIn 빈 환경에는 내장 함수가 없음" {
            Expect.throwsC
                (fun () -> evalIn Map.empty (Program.parse "length \"a\"") |> ignore)
                (fun ex -> Expect.equal ex.Message "Unbound variable: length" "")
        }

        test "모든 내장 함수가 builtins 환경에 있음" {
            Expect.equal (builtins |> Map.keys |> Set.ofSeq) (builtinArity |> Map.keys |> Set.ofSeq) ""
        }

        test "문자열 값 출력 (리터럴 형태로 이스케이프)" {
            Expect.equal (formatValue (VString "hi")) "\"hi\"" ""
            Expect.equal (formatValue (VString "")) "\"\"" ""
            Expect.equal (formatValue (VString "a\r\nb")) "\"a\\r\\nb\"" ""
            Expect.equal (formatValue (VString "a\"b\\c\nd\te")) "\"a\\\"b\\\\c\\nd\\te\"" ""
            Expect.equal (formatValue (calc "[\"a\"; \"b\"]")) "[\"a\"; \"b\"]" ""
            Expect.equal (typeName (calc "\"a\"")) "string" ""
        }

        test "리스트 값 출력" {
            Expect.equal (formatValue (calc "[]")) "[]" ""
            Expect.equal (formatValue (calc "[1; -2; 3]")) "[1; -2; 3]" ""
            Expect.equal (formatValue (calc "[[true]; []]")) "[[true]; []]" ""
            Expect.equal (formatValue (calc "[fun x -> x]")) "[<fun>]" ""
            Expect.equal (typeName (calc "[]")) "list" ""
        }

        test "선택되지 않은 match 분기는 평가하지 않음" {
            Expect.equal (calc "match [] with [] -> 0 | h :: t -> 1 / 0") (VInt 0) ""
        }

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

            testProperty "리스트 리터럴은 F# 리스트와 같음" <| fun (xs: NonNegativeInt list) ->
                let ns = xs |> List.map (fun (NonNegativeInt n) -> n)
                let src = "[" + (ns |> List.map string |> String.concat "; ") + "]"
                calc src = VList (ns |> List.map VInt)

            testProperty "재귀 len 은 List.length 와 같음" <| fun (xs: NonNegativeInt list) ->
                let src = "[" + (xs |> List.map (fun (NonNegativeInt n) -> string n) |> String.concat "; ") + "]"
                calc (sprintf "let rec len xs = match xs with [] -> 0 | h :: t -> 1 + len t in len %s" src)
                    = VInt (List.length xs)

            testProperty "formatValue 출력을 다시 읽으면 같은 문자열" <| fun (NonNull (s: string)) ->
                calc (formatValue (VString s)) = VString s

            testProperty "문자열 + 는 F# 의 + 와 같음" <| fun (NonNull (a: string)) (NonNull (b: string)) ->
                calc (formatValue (VString a) + " + " + formatValue (VString b)) = VString (a + b)

            testProperty "length 는 .NET String.Length 와 같음" <| fun (NonNull (s: string)) ->
                calc (sprintf "length %s" (formatValue (VString s))) = VInt s.Length

            testProperty "toInt (toString n) = n" <| fun (NonNegativeInt n) ->
                // 음수는 괄호로 감싸야 함수 인자가 됨 (toString -n 은 뺄셈)
                calc (sprintf "toInt (toString %d)" n) = VInt n
                && calc (sprintf "toInt (toString (-%d))" n) = VInt -n

            testProperty "join sep (split s sep) = s" <| fun (NonNull (s: string)) ->
                let lit = formatValue (VString s)
                calc (sprintf "join \",\" (split %s \",\")" lit) = VString s

            testProperty "substr s 0 (length s) = s" <| fun (NonNull (s: string)) ->
                let lit = formatValue (VString s)
                calc (sprintf "substr %s 0 (length %s)" lit lit) = VString s

            testProperty "if 는 조건에 따라 분기" <| fun (c: bool) (NonNegativeInt a) (NonNegativeInt b) ->
                let cond = if c then "true" else "false"
                calc (sprintf "if %s then %d else %d" cond a b) = VInt (if c then a else b)
        ]
    ]
