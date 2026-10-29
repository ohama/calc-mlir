/// indentation 문법 (LayoutFilter): 기존 입력의 뜻이 그대로인지, 들여쓴 프로그램이 같은 verbose 프로그램과 같은 AST 인지
module LayoutTests

open System.IO
open Expecto

/// 파싱 결과 또는 오류 메시지 (두 방법을 비교)
let private result (parse: string -> Ast.Expr) (input: string) =
    try
        Ok (parse input)
    with ex ->
        Error ex.Message

/// light 로 쓴 프로그램과 같은 뜻의 verbose 프로그램
let private same (light: string) (verbose: string) =
    test (light.Replace("\n", "⏎")) {
        Expect.equal (result Frontend.parse light) (result Frontend.parseVerbose verbose) verbose
    }

let private expectError (input: string) (message: string) (line: int) (column: int) =
    test (input.Replace("\n", "⏎")) {
        Expect.throwsC
            (fun () -> Frontend.parse input |> ignore)
            (fun ex ->
                match ex with
                | Errors.SyntaxError (m, l, c) -> Expect.equal (m, l, c) (message, line, column) input
                | _ -> failtestf "SyntaxError 가 아님: %s" ex.Message)
    }

[<Tests>]
let tests =
    testList "Layout" [
        // prelude 와 examples 는 0.39.0 부터 indentation 문법으로 씀 (prelude 는 verbose 로 쓴 예전 것과 AST 가 같음을 확인했음)
        test "기존 입력은 indentation 문법을 켜도 AST 가 같음 (EvalTests, InferTests)" {
            let inputs = (EvalTests.cases |> List.map fst) @ (InferTests.inferred |> List.map fst)
            for input in inputs do
                Expect.equal (result Frontend.parse input) (result Frontend.parseVerbose input) input
        }

        testList "블록과 구분자" [
            same "let f x =\n    let y = x + 1\n    y * 2\nf 1" "let f x = let y = x + 1 in y * 2;; f 1"
            same "let x = 1\nlet y = 2\nx + y" "let x = 1 let y = 2;; x + y"
            same "println 1\nprintln 2" "println 1;; println 2"
            same "let f () =\n    println 1\n    println 2\n    3" "let f () = println 1; println 2; 3"
            same "let a =\n    let b = 1\n    let c = 2\n    b + c" "let a = let b = 1 in let c = 2 in b + c"
            same "let x =\n    1 +\n    2" "let x = 1 + 2"
            same "let total =\n    xs\n    |> map f\n    |> sum" "let total = xs |> map f |> sum"
            same "let x =\n    f a\n      b" "let x = f a b"
            same "let r =\n    1\n  + 2" "let r = 1 + 2" // 줄 머리의 연산자는 블록보다 왼쪽이어도 이어짐
            same "let x = 1 in\nx + 1" "let x = 1 in x + 1"
            same "let nth xs i =\n    let go = 1 in\n    let h = 2 in\n    go + h" "let nth xs i = let go = 1 in let h = 2 in go + h"
            same "let rec even n =\n    if n = 0 then true else odd (n - 1)\nand odd n =\n    if n = 0 then false else even (n - 1)" "let rec even n = if n = 0 then true else odd (n - 1) and odd n = if n = 0 then false else even (n - 1)"
        ]

        testList "if, match, try, fun" [
            same "if c then\n    a\n    b\nelse\n    d" "if c then (a; b) else d"
            same "if a then 1\nelse if b then 2\nelse 3" "if a then 1 else if b then 2 else 3"
            same "let f x =\n    match x with\n    | A -> 1\n    | B -> 2\nf A" "let f x = match x with A -> 1 | B -> 2;; f A"
            same "match a with\n| A ->\n    match b with\n    | X -> 1\n    | Y -> 2\n| B -> 3" "match a with A -> (match b with X -> 1 | Y -> 2) | B -> 3"
            same "match a with A -> match b with X -> 1 | Y -> 2" "match a with A -> match b with X -> 1 | Y -> 2" // 한 줄의 중첩 match 는 지금처럼 안쪽
            same "let f x = match x with\n    | A -> 1\n    | B -> 2" "let f x = match x with A -> 1 | B -> 2" // | 는 match 가 있는 줄의 머리까지 왼쪽에 둘 수 있음
            same "match x with\n| A ->\n    let y = 1\n    y\n| B -> 0" "match x with A -> (let y = 1 in y) | B -> 0"
            same "try\n    risky ()\nwith\n| Failure m -> m" "try risky () with Failure m -> m"
            same "let g = function\n    | 0 -> \"zero\"\n    | _ -> \"many\"" "let g = function 0 -> \"zero\" | _ -> \"many\""
            same "xs |> map (fun x ->\n    let y = x + 1\n    y * 2)" "xs |> map (fun x -> let y = x + 1 in y * 2)"
            same "let f =\n    fun x ->\n        x + 1" "let f = fun x -> x + 1"
            same "if c then a else b; d" "if c then a else b; d" // then, else 의 몸은 같은 줄의 ; 에서 끝남
            same "(fun x -> x : int -> int)" "(fun x -> x : int -> int)"
        ]

        testList "괄호, 리스트, 레코드" [
            same "let xs =\n    [ 1\n      2\n      3 ]" "let xs = [ 1; 2; 3 ]"
            same "type p = {\n    x: int\n    y: int }\nlet a =\n    { x = 1\n      y = 2 }" "type p = { x: int; y: int } let a = { x = 1; y = 2 }"
            same "(a\n b)" "(a; b)"
            same "let a =\n    [| 1\n       2 |]\na.[0] <- 3" "let a = [| 1; 2 |];; a.[0] <- 3"
            same "(1,\n 2)" "(1, 2)"
        ]

        testList "모듈과 반복문" [
            same "module M =\n    let x = 1\n    let f y = y + x\nM.f 2" "module M = struct let x = 1 let f y = y + x end;; M.f 2"
            same "module M =\n    module N =\n        let g = 1\n    let h = N.g\nM.h" "module M = struct module N = struct let g = 1 end let h = N.g end;; M.h"
            same "module M = struct\n    let x = 1\nend\nM.x" "module M = struct let x = 1 end;; M.x"
            same "let n = ref 0\nwhile !n < 3 do\n    incr n\n    println !n\n!n" "let n = ref 0;; while !n < 3 do incr n; println !n done;; !n"
            same "for i = 1 to 3 do\n    print i\nprintln \"\"" "for i = 1 to 3 do print i done;; println \"\""
            same "while c do a done" "while c do a done"
        ]

        testList "오류" [
            expectError "let f x =\n\tx" "Tabs are not allowed in indentation" 2 2
            expectError "let f x =\n    let y = 1\nf 2" "The block following this let is unfinished" 2 5
            expectError "let x =\n    (let y = 1)" "The block following this let is unfinished" 2 6
        ]

        test "목표 프로그램 (docs/indentation-design.md 1장)" {
            let light =
                String.concat "\n"
                    [ "type shape = Circle of int | Rect of int * int"
                      "let rec fact n ="
                      "    if n = 0 then 1"
                      "    else n * fact (n - 1)"
                      ""
                      "let area shape ="
                      "    let pi = 3"
                      "    match shape with"
                      "    | Circle r -> pi * r * r"
                      "    | Rect (w, h) -> w * h"
                      ""
                      "println (fact 5)"
                      "println (area (Rect (2, 3)))" ]
            use writer = new StringWriter()
            Eval.evalWithOutput writer (Program.load light) |> ignore
            Expect.equal (writer.ToString().Replace("\r\n", "\n")) "120\n6\n" ""
        }
    ]
