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
      "let upper s = s + \"!\" in upper \"a\"", VString "a!"
      // 리스트 내장 함수
      "head [1; 2]", VInt 1
      "tail [1; 2]", VList [ VInt 2 ]
      "tail [1]", VList []
      "isEmpty []", VBool true
      "isEmpty [1]", VBool false
      "nth [10; 20; 30] 0", VInt 10
      "nth [10; 20; 30] 2", VInt 30
      "reverse [1; 2; 3]", VList [ VInt 3; VInt 2; VInt 1 ]
      "reverse []", VList []
      "append [1] [2; 3]", VList [ VInt 1; VInt 2; VInt 3 ]
      "append [] []", VList []
      "range 1 5", VList [ VInt 1; VInt 2; VInt 3; VInt 4; VInt 5 ]
      "range 3 3", VList [ VInt 3 ]
      "range 5 1", VList []
      "range (-1) 1", VList [ VInt -1; VInt 0; VInt 1 ]
      "sum [1; 2; 3]", VInt 6
      "sum []", VInt 0
      // + 와 같이 오버플로는 한 바퀴 돎
      "sum [2147483647; 1]", VInt -2147483648
      "sort [3; 1; 2; 1]", VList [ VInt 1; VInt 1; VInt 2; VInt 3 ]
      "sort [\"b\"; \"B\"; \"a\"]", VList [ VString "B"; VString "a"; VString "b" ]
      "sort []", VList []
      "map (fun x -> x * 2) [1; 2; 3]", VList [ VInt 2; VInt 4; VInt 6 ]
      "map upper [\"a\"; \"b\"]", VList [ VString "A"; VString "B" ]
      "map (fun x -> x) []", VList []
      "filter (fun x -> x > 1) [1; 2; 3]", VList [ VInt 2; VInt 3 ]
      "filter (fun x -> false) [1; 2]", VList []
      "fold (fun acc x -> acc + x) 0 [1; 2; 3]", VInt 6
      "fold (fun acc x -> x :: acc) [] [1; 2; 3]", VList [ VInt 3; VInt 2; VInt 1 ]
      "fold (fun acc x -> acc + x) \"\" [\"a\"; \"b\"]", VString "ab"
      "fold (fun acc x -> acc - x) 10 [1; 2]", VInt 7
      "exists (fun x -> x = 2) [1; 2]", VBool true
      "exists (fun x -> x = 3) [1; 2]", VBool false
      "exists (fun x -> true) []", VBool false
      "forall (fun x -> x > 0) [1; 2]", VBool true
      "forall (fun x -> x > 1) [1; 2]", VBool false
      "forall (fun x -> false) []", VBool true
      "sum (map (fun x -> x * x) (range 1 10))", VInt 385
      "let inc = map (fun x -> x + 1) in inc [1; 2]", VList [ VInt 2; VInt 3 ]
      "let evens = filter (fun x -> x / 2 * 2 = x) in evens (range 1 6)", VList [ VInt 2; VInt 4; VInt 6 ]
      "map (fun x -> x * 10) (filter (fun x -> x > 2) [1; 3; 5])", VList [ VInt 30; VInt 50 ]
      "let rec fact n = if n = 0 then 1 else n * fact (n - 1) in map fact (range 0 5)",
          VList [ VInt 1; VInt 1; VInt 2; VInt 6; VInt 24; VInt 120 ]
      "join \",\" (map toString (range 1 3))", VString "1,2,3"
      "length (range 1 100000)", VInt 100000
      // 사용자가 정의한 map 이 내장 map 을 가림
      "let map f xs = 0 in map 1 2", VInt 0
      // 튜플
      "(1, 2)", VTuple [ VInt 1; VInt 2 ]
      "(1, \"a\", true)", VTuple [ VInt 1; VString "a"; VBool true ]
      "((1, 2), [3])", VTuple [ VTuple [ VInt 1; VInt 2 ]; VList [ VInt 3 ] ]
      "(1 + 1, 2 * 3)", VTuple [ VInt 2; VInt 6 ]
      "(1, 2) = (1, 2)", VBool true
      "(1, 2) = (1, 3)", VBool false
      "(1, 2) <> (2, 1)", VBool true
      "(1, 2) = (1, 2, 3)", VBool false
      "[(1, 2)] = [(1, 2)]", VBool true
      "fst (1, \"a\")", VInt 1
      "snd (1, \"a\")", VString "a"
      "map fst [(1, 2); (3, 4)]", VList [ VInt 1; VInt 3 ]
      "let (a, b) = (1, 2) in a + b", VInt 3
      "let (a, (b, c)) = (1, (2, 3)) in a * 100 + b * 10 + c", VInt 123
      "let (a, _) = (1, 2) in a", VInt 1
      "let (h :: t, n) = ([1; 2], 3) in h + n", VInt 4
      // 패턴 매칭
      "match 3 with 1 -> \"one\" | 2 -> \"two\" | _ -> \"many\"", VString "many"
      "match 2 with 1 -> \"one\" | 2 -> \"two\" | _ -> \"many\"", VString "two"
      "match -1 with -1 -> \"neg\" | _ -> \"x\"", VString "neg"
      "match \"hi\" with \"hi\" -> 1 | _ -> 0", VInt 1
      "match false with true -> 1 | false -> 0", VInt 0
      "match [] with [] -> 0 | [x] -> x | x :: y :: _ -> x + y", VInt 0
      "match [5] with [] -> 0 | [x] -> x | x :: y :: _ -> x + y", VInt 5
      "match [1; 2; 3] with [] -> 0 | [x] -> x | x :: y :: _ -> x + y", VInt 3
      "match [1; 2] with [a; b] -> a * 10 + b | _ -> 0", VInt 12
      "match [1; 2; 3] with [a; b] -> a * 10 + b | _ -> 0", VInt 0
      "match (1, 0) with (0, _) -> \"a\" | (_, 0) -> \"b\" | _ -> \"c\"", VString "b"
      "match (0, 0) with (0, _) -> \"a\" | (_, 0) -> \"b\" | _ -> \"c\"", VString "a"
      "match [(1, \"a\"); (2, \"b\")] with (n, s) :: _ -> s | [] -> \"\"", VString "a"
      "match [[1]] with [[x]] -> x | _ -> 0", VInt 1
      // 위에서부터 처음 일치하는 분기
      "match 1 with x -> \"first\" | 1 -> \"second\"", VString "first"
      // 리터럴 패턴은 타입이 다르면 불일치
      "match \"1\" with 1 -> \"int\" | _ -> \"other\"", VString "other"
      "match (1, 2) with (a, b, c) -> 0 | _ -> 1", VInt 1
      // when 가드
      "match 5 with n when n > 3 -> \"big\" | n -> \"small\"", VString "big"
      "match 2 with n when n > 3 -> \"big\" | n -> \"small\"", VString "small"
      "match (3, 4) with (a, b) when a > b -> a | (a, b) -> b", VInt 4
      // 패턴 변수는 바깥 이름을 가리고, 분기 밖에서는 사라짐
      "let x = 100 in (match 1 with x -> x) + x", VInt 101
      "let rec zip xs ys = match (xs, ys) with (x :: xt, y :: yt) -> (x, y) :: zip xt yt | _ -> [] in zip [1; 2] [\"a\"; \"b\"; \"c\"]",
          VList [ VTuple [ VInt 1; VString "a" ]; VTuple [ VInt 2; VString "b" ] ]
      "let rec last xs = match xs with [x] -> x | _ :: t -> last t | [] -> 0 in last [1; 2; 3]", VInt 3
      "let f _ = 1 in f 2", VInt 1 ]

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
            expectError "(fun x -> x) = (fun x -> x)" "Type error: expected int, bool, string, list or tuple but got function"
            expectError "(fun x -> x) + 1" "Type error: expected int or string but got function"
            // let (rec 아님) 으로 정의한 함수는 자기 자신을 볼 수 없음
            expectError "let f x = f x in f 1" "Unbound variable: f"
        ]

        testList "리스트 오류" [
            expectError "1 :: 2" "Type error: expected list but got int"
            // 패턴은 타입이 달라도 오류가 아니라 불일치
            expectError "match 1 with [] -> 0 | h :: t -> h" "Match failure: 1"
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
            expectError "length = length" "Type error: expected int, bool, string, list or tuple but got function"
            // 인자를 다 받기 전에는 실행하지 않음 (오류도 나중에)
            expectError "let f = substr 1 in f 2 3" "Type error: expected string but got int"
        ]

        testList "리스트 내장 함수 오류" [
            expectError "head []" "head: empty list"
            expectError "tail []" "tail: empty list"
            expectError "head 1" "Type error: expected list but got int"
            expectError "nth [10] 1" "nth: index out of range (length 1, index 1)"
            expectError "nth [10] (-1)" "nth: index out of range (length 1, index -1)"
            expectError "nth [] 0" "nth: index out of range (length 0, index 0)"
            expectError "append [1] 2" "Type error: expected list but got int"
            expectError "range 1 true" "Type error: expected int but got bool"
            expectError "sum [1; true]" "Type error: expected int but got bool"
            expectError "sum [\"a\"]" "Type error: expected int but got string"
            // List.sortWith 가 감싼 예외가 아니라 원래 타입 오류
            expectError "sort [1; \"a\"]" "Type error: expected int but got string"
            expectError "sort [\"a\"; 1]" "Type error: expected string but got int"
            expectError "sort [true]" "Type error: expected int or string but got bool"
            expectError "sort [[1]; [2]]" "Type error: expected int or string but got list"
            expectError "map 1 [1]" "Type error: expected function but got int"
            expectError "map (fun x -> x) 1" "Type error: expected list but got int"
            expectError "filter (fun x -> x) [1]" "Type error: expected bool but got int"
            expectError "exists (fun x -> 1) [1]" "Type error: expected bool but got int"
            expectError "forall (fun x -> \"a\") [1]" "Type error: expected bool but got string"
            expectError "fold (fun acc x -> acc + x) 0 [true]" "Type error: expected int but got bool"
        ]

        test "exists 와 forall 은 결과가 정해지면 멈춤" {
            Expect.equal (calc "exists (fun x -> 10 / x = 10) [1; 0]") (VBool true) ""
            Expect.equal (calc "forall (fun x -> 10 / x = 5) [1; 0]") (VBool false) ""
        }

        test "map 은 원소 순서대로 함수를 호출" {
            // 두 번째 원소에서 오류가 나면 그 오류가 보임
            Expect.throwsT<DivideByZeroException>
                (fun () -> calc "map (fun x -> 10 / x) [1; 0; 2]" |> ignore) ""
        }

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

        testList "패턴 매칭 오류" [
            expectError "match 1 with 2 -> 0" "Match failure: 1"
            expectError "match [1] with [] -> 0" "Match failure: [1]"
            expectError "match (1, \"a\") with (2, _) -> 0" "Match failure: (1, \"a\")"
            expectError "let (a, b) = (1, 2, 3) in a" "Match failure: (1, 2, 3)"
            expectError "let (a, b) = 1 in a" "Match failure: 1"
            expectError "match 1 with x when 1 -> x" "Type error: expected bool but got int"
            expectError "fst (1, 2, 3)" "Type error: expected pair but got tuple"
            expectError "fst 1" "Type error: expected pair but got int"
            expectError "(1, 2) + 1" "Type error: expected int or string but got tuple"
            expectError "(1, 2) < (1, 3)" "Type error: expected int or string but got tuple"
            expectError "(1, 2) = 1" "Type error: expected tuple but got int"
            expectError "[1] = [(1, 2)]" "Type error: expected int but got tuple"
        ]

        test "튜플 값 출력" {
            Expect.equal (formatValue (calc "(1, \"a\", [true])")) "(1, \"a\", [true])" ""
            Expect.equal (typeName (calc "(1, 2)")) "tuple" ""
            Expect.equal (calc "toString (1, 2)") (VString "(1, 2)") ""
        }

        test "가드가 false 면 다음 분기로, 선택되지 않은 분기 본문은 평가하지 않음" {
            Expect.equal (calc "match 1 with x when false -> 1 / 0 | _ -> 7") (VInt 7) ""
            Expect.equal (calc "match 1 with 2 -> 1 / 0 | _ -> 7") (VInt 7) ""
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

            testProperty "range a b 는 F# [a .. b] 와 같음" <| fun (a: int) (b: int) ->
                let a, b = a % 50, b % 50
                calc (sprintf "range (%d) (%d)" a b) = VList ([ a .. b ] |> List.map VInt)

            testProperty "sum (range 1 n) = n(n+1)/2" <| fun (NonNegativeInt n) ->
                calc (sprintf "sum (range 1 %d)" n) = VInt (n * (n + 1) / 2)

            testProperty "reverse (reverse xs) = xs" <| fun (xs: NonNegativeInt list) ->
                let src = "[" + (xs |> List.map (fun (NonNegativeInt n) -> string n) |> String.concat "; ") + "]"
                calc (sprintf "reverse (reverse %s)" src) = calc src

            testProperty "sort 는 F# List.sort 와 같음" <| fun (xs: NonNegativeInt list) ->
                let ns = xs |> List.map (fun (NonNegativeInt n) -> n)
                let src = "[" + (ns |> List.map string |> String.concat "; ") + "]"
                calc (sprintf "sort %s" src) = VList (List.sort ns |> List.map VInt)

            testProperty "fold 로 만든 length 는 length 와 같음" <| fun (xs: NonNegativeInt list) ->
                let src = "[" + (xs |> List.map (fun (NonNegativeInt n) -> string n) |> String.concat "; ") + "]"
                calc (sprintf "fold (fun acc x -> acc + 1) 0 %s" src) = calc (sprintf "length %s" src)

            testProperty "let (a, b) = (x, y) in (b, a) 는 순서를 바꿈" <| fun (NonNegativeInt x) (NonNegativeInt y) ->
                calc (sprintf "let (a, b) = (%d, %d) in (b, a)" x y) = VTuple [ VInt y; VInt x ]

            testProperty "정수 리터럴 패턴은 = 와 같음" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                calc (sprintf "match %d with %d -> true | _ -> false" a b) = VBool (a = b)

            testProperty "if 는 조건에 따라 분기" <| fun (c: bool) (NonNegativeInt a) (NonNegativeInt b) ->
                let cond = if c then "true" else "false"
                calc (sprintf "if %s then %d else %d" cond a b) = VInt (if c then a else b)
        ]
    ]
