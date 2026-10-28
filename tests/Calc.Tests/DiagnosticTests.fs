/// 타입 오류의 위치와 빠진 match 분기 경고 (Infer.typecheckWithWarnings)
module DiagnosticTests

open Expecto
open Infer

/// 타입 오류의 메시지와 위치 (줄, 열)
let private errorAt (input: string) =
    try
        typecheck (Program.load input) |> ignore
        failtestf "타입 오류가 나야 함: %s" input
    with TypeError (message, position) ->
        message, position

let private warnings (input: string) =
    snd (typecheckWithWarnings (Program.load input)) |> List.map (fun w -> w.Message, w.Position)

let private notExhaustive example = sprintf "this match is not exhaustive. Example of a value not matched: %s" example

[<Tests>]
let tests =
    testList "Diagnostics" [
        testList "타입 오류 위치: 문제가 된 식을 가리킴" [
            for input, message, position in
                [ "let f x = x + 1 in\n  f true", "Type mismatch: expected int but got bool", (2, 5)
                  "if 1 then 2 else 3", "Type mismatch: expected bool but got int", (1, 4)
                  "if true then 1 else \"a\"", "Type mismatch: expected int but got string", (1, 21)
                  "1 + true", "Type mismatch: expected int but got bool", (1, 5)
                  "not 3", "Type mismatch: expected bool but got int", (1, 5)
                  "[1; true]", "Type mismatch: expected int list but got bool list", (1, 5)
                  "1 :: 2", "Type mismatch: expected int list but got int", (1, 6)
                  "1 2", "Type mismatch: expected function but got int", (1, 1)
                  "let x = 5 in\nlet y = x + \"a\" in\ny", "Type mismatch: expected int but got string", (2, 13)
                  "match Some 1 with | Some \"a\" -> 1 | _ -> 0", "Type mismatch: expected int option but got string option", (1, 21)
                  "match 1 with x when x -> 1", "Type mismatch: expected bool but got int", (1, 21)
                  "match 1 with 1 -> \"a\" | _ -> 2", "Type mismatch: expected string but got int", (1, 30)
                  "y + 1", "Unbound variable: y", (1, 1)
                  "(\"a\" : int)", "Type mismatch: expected int but got string", (1, 2)
                  "let f (x: int) : string =\n  x in f", "Type mismatch: expected string but got int", (2, 3)
                  "(1 : foo)", "Unknown type: foo", (1, 6)
                  "(1, Nome)", "Unknown constructor: Nome", (1, 5) ] ->
                test (input.Replace("\n", "\\n")) {
                    Expect.equal (errorAt input) (message, Some position) ""
                }
        ]

        test "오류 메시지(Message)에는 위치를 넣지 않음 (calc 가 출력할 때 붙임)" {
            try
                typecheck (Program.parse "1 + true") |> ignore
            with ex ->
                Expect.equal ex.Message "Type mismatch: expected int but got bool" ""
        }

        testList "빠진 match 분기" [
            for input, expected in
                [ "match Some 1 with | Some x -> x", [ notExhaustive "None", Some (1, 1) ]
                  "match [1] with | x :: _ -> x", [ notExhaustive "[]", Some (1, 1) ]
                  "match true with | true -> 1", [ notExhaustive "false", Some (1, 1) ]
                  "match (true, 1) with | (true, _) -> 1 | (false, 0) -> 2", [ notExhaustive "(false, 1)", Some (1, 1) ]
                  "match [1; 2] with | [a; b] -> a | [] -> 0", [ notExhaustive "[_]", Some (1, 1) ]
                  "match [Some 1] with | [] -> 0 | Some _ :: _ -> 1", [ notExhaustive "None :: _", Some (1, 1) ]
                  "fun x -> match x with | Some (Some _) -> 1 | None -> 0", [ notExhaustive "Some None", Some (1, 10) ]
                  "type c = R | G | B in fun x -> match x with | R -> 1", [ notExhaustive "G", Some (1, 32) ]
                  "type 'a t = Leaf | Node of 'a t * 'a in fun x -> match x with | Node (Leaf, _) -> 0 | Leaf -> 1",
                  [ notExhaustive "Node (Node _, _)", Some (1, 50) ]
                  "match 3 with | 0 -> 0 | 1 -> 1", [ notExhaustive "2", Some (1, 1) ]
                  "match \"x\" with | \"\" -> 0", [ notExhaustive "\"a\"", Some (1, 1) ]
                  "let (a, 1) = (5, 2) in a", [ "this let pattern is not exhaustive. Example of a value not matched: (_, 0)", Some (1, 1) ] ] ->
                test input { Expect.equal (warnings input) expected "" }
        ]

        test "or 패턴과 function" {
            Expect.isEmpty (warnings "match true with true | false -> 1") "or 패턴이 모두 덮음"
            Expect.isEmpty (warnings "type c = R | G | B in fun x -> match x with R | G -> 1 | B -> 2") ""
            Expect.equal (warnings "match 1 with 1 | 2 -> 0") [ notExhaustive "0", Some (1, 1) ] ""
            Expect.equal (warnings "let f = function Some x -> x in f") [ notExhaustive "None", Some (1, 9) ] "function 의 위치"
        }

        test "가드가 있는 분기는 덮는 데 세지 않음 (OCaml, F# 와 같음)" {
            Expect.equal
                (warnings "match 1 with | x when x > 0 -> 1")
                [ notExhaustive "_", Some (1, 1) ]
                ""
            Expect.isEmpty (warnings "match 1 with | x when x > 0 -> 1 | _ -> 0") "_ 가 있으면 덮음"
        }

        test "버리는 값이 unit 이 아니면 경고 (F# FS0020)" {
            Expect.equal
                (warnings "1; 2")
                [ "this expression has type int and its value is ignored (use ignore or let _ = ...)", Some (1, 1) ]
                ""
            Expect.isEmpty (warnings "println 1; ignore 2; ()") "unit 이면 경고 없음"
            Expect.equal (warnings "match () with () -> 1") [] "() 패턴은 unit 을 모두 덮음"
        }

        testList "쓰이지 않는 분기 (OCaml Warning 11, F# FS0026)" [
            let unused = "this match case is unused"
            for input, expected in
                [ "match true with true -> 1 | false -> 2 | _ -> 3", [ unused, Some (1, 42) ]
                  "match 1 with _ -> 0 | 1 -> 1", [ unused, Some (1, 23) ]
                  "match [1] with [] -> 0 | x :: _ -> x | [y] -> y", [ unused, Some (1, 40) ]
                  "match Some 1 with Some _ -> 1 | Some 2 -> 2 | None -> 0", [ unused, Some (1, 33) ]
                  "match (1, true) with (_, true) -> 1 | (1, false) -> 2 | (_, false) -> 3 | (2, _) -> 4", [ unused, Some (1, 75) ]
                  "match 1 with 1 | 2 -> 0 | 2 -> 1 | _ -> 9", [ unused, Some (1, 27) ]
                  "match \"a\" with \"a\" -> 1 | \"a\" -> 2 | _ -> 3", [ unused, Some (1, 27) ]
                  "type c = R | G in match R with R -> 1 | G -> 2 | R -> 3", [ unused, Some (1, 50) ]
                  "function 0 -> 1 | _ -> 2 | 3 -> 4", [ unused, Some (1, 28) ] ] do
                yield test input { Expect.equal (warnings input) expected "" }
            yield test "가드가 있는 앞 분기는 덮는 데 세지 않음" {
                Expect.isEmpty (warnings "match 1 with x when x > 0 -> 1 | 1 -> 2 | _ -> 3") ""
            }
            yield test "가드가 있는 분기도 앞 분기가 덮으면 쓰이지 않음" {
                Expect.equal (warnings "match 1 with _ -> 0 | x when x > 0 -> 1") [ unused, Some (1, 23) ] ""
            }
            yield test "빠진 분기와 쓰이지 않는 분기가 함께" {
                Expect.equal
                    (warnings "match Some 1 with Some _ -> 1 | Some 3 -> 3")
                    [ notExhaustive "None", Some (1, 1); unused, Some (1, 33) ]
                    "위치 순서"
            }
        ]

        testList "타입 변수가 다른 타입으로 정해짐 (F# FS0064)" [
            let constrained name what = sprintf "the type variable '%s has been constrained to be type %s" name what
            for input, expected in
                [ "let f (x: 'a) = x + 1 in f", [ constrained "a" "int", Some (1, 11) ]
                  "fun (x: 'a) (y: 'b) -> if true then x else 1", [ constrained "a" "int", Some (1, 9) ]
                  "let f (x: 'a) : 'a list = [x; 1] in f", [ constrained "a" "int", Some (1, 11) ]
                  "let f (x: 'a) (y: 'a) = x < y in f", [ constrained "a" "int", Some (1, 11) ] // 결과 타입이 int 로 정해짐
                  "let f (x: 'a) (y: 'a) = x < y in ()", [ constrained "a" "int or string", Some (1, 11) ]
                  "let id (x: 'a) : 'a = x in id 1", []
                  "let f (x: 'a) (y: 'b) = (x, y) in f 1 true", [] ] do
                yield test input { Expect.equal (warnings input) expected "" }
        ]

        test "모두 덮으면 경고 없음" {
            for input in
                [ "match Some 1 with | Some x -> x | None -> 0"
                  "match [1] with | [] -> 0 | x :: _ -> x"
                  "match (true, false) with | (true, _) -> 1 | (_, true) -> 2 | (false, false) -> 3"
                  "match (1, 2) with | (a, b) -> a"
                  "let (a, b) = (1, 2) in a"
                  "type 'a t = Leaf | Node of 'a t * 'a in fun x -> match x with | Node (_, v) -> 1 | Leaf -> 0"
                  "match 5 with | 0 -> 1 | n -> n" ] do
                Expect.isEmpty (warnings input) input
        }

        test "중첩 match 는 위치 순서로" {
            let input = "match Some [1] with\n| Some xs -> (match xs with | [] -> 0)\n| None -> 1"
            Expect.equal (warnings input) [ notExhaustive "_ :: _", Some (2, 15) ] "안쪽 match 만"
        }

        test "평가 예제: 실행 중 Match failure 가 나는 프로그램은 모두 경고를 받음" {
            // 빠진 분기 검사의 건전성: 경고가 없으면 Match failure 가 나지 않아야 함
            let inputs = (EvalTests.cases |> List.map fst) @ [ "match [1] with | [] -> 0"; "let (a, 1) = (5, 2) in a"; "match None with | Some x -> x" ]
            for input in inputs do
                let ast = Program.load input
                match (try Some (typecheckWithWarnings ast) with TypeError _ -> None) with
                | None -> ()
                | Some (_, ws) ->
                    let failed =
                        try
                            Eval.eval ast |> ignore
                            false
                        with ex -> ex.Message.StartsWith "Match failure"
                    if failed then Expect.isNonEmpty ws input
        }
    ]
