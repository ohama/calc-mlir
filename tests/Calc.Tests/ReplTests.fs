/// REPL: 입력마다 결과와 출력, 정의가 다음 입력에 남는지 (Repl.submit, Repl.run)
module ReplTests

open System.IO
open Expecto

/// 입력들을 차례로 넣고, 입력마다 나온 출력
let private session (inputs: string list) =
    let mutable state = Repl.initial
    [ for input in inputs do
          use out = new StringWriter()
          state <- Repl.submit state input out
          yield out.ToString().Replace("\r\n", "\n") ]

let private expectSession (pairs: (string * string) list) =
    let outputs = session (List.map fst pairs)
    for (input, expected), actual in List.zip pairs outputs do
        Expect.equal actual expected input

[<Tests>]
let tests =
    testList "Repl" [
        test "식은 - : 타입 = 값, 선언은 val 이름 : 타입 = 값 (OCaml 과 같음)" {
            expectSession
                [ "1 + 2;;", "- : int = 3\n"
                  "let x = 10;;", "val x : int = 10\n"
                  "let add a b = a + b;;", "val add : int -> int -> int = <fun>\n"
                  "add x 5;;", "- : int = 15\n" ]
        }
        test "let rec, type, 패턴 선언" {
            expectSession
                [ "let rec fact n = if n = 0 then 1 else n * fact (n - 1);;", "val fact : int -> int = <fun>\n"
                  "fact 10;;", "- : int = 3628800\n"
                  "type 'a tree = Leaf | Node of 'a tree * 'a * 'a tree;;", "type 'a tree\n"
                  "Node (Leaf, 1, Leaf);;", "- : int tree = Node (Leaf, 1, Leaf)\n"
                  "let (a, b) = (1, \"s\");;", "val a : int = 1\nval b : string = \"s\"\n"
                  "let _ = a + 1;;", "- : int = 2\n"
                  "let () = ();;", "" ]
        }
        test "let rec ... and ... 는 함수마다 val" {
            expectSession
                [ "let rec even n = if n = 0 then true else odd (n - 1)\nand odd n = if n = 0 then false else even (n - 1);;",
                  "val even : int -> bool = <fun>\nval odd : int -> bool = <fun>\n"
                  "(even 4, odd 4);;", "- : bool * bool = (true, false)\n" ]
        }
        test "let 다형성이 입력을 넘어 유지됨" {
            expectSession
                [ "let id y = y;;", "val id : 'a -> 'a = <fun>\n"
                  "(id 1, id true);;", "- : int * bool = (1, true)\n" ]
        }
        test "print 출력이 결과 줄보다 먼저, 앞 입력의 print 는 되풀이되지 않음" {
            expectSession
                [ "let x = println \"once\"; 5;;", "once\nval x : int = 5\n"
                  "x + 1;;", "- : int = 6\n" ]
        }
        test "같은 이름을 다시 정의하면 가림" {
            expectSession
                [ "let x = 1;;", "val x : int = 1\n"
                  "let x = \"s\";;", "val x : string = \"s\"\n"
                  "x;;", "- : string = \"s\"\n" ]
        }
        test "오류는 알리고 계속, 실패한 입력의 정의는 남지 않음" {
            expectSession
                [ "let x = 1;;", "val x : int = 1\n"
                  "x + true;;", "Error: Type mismatch: expected int but got bool (line 1, column 5)\n  1 | x + true;;\n    |     ^\n"
                  "let y = head [];;", "Error: head: empty list\n"
                  "y;;", "Error: Unbound variable: y (line 1, column 1)\n  1 | y;;\n    | ^\n"
                  "1 +;;", "Error: parse error: unexpected ';;' (line 1, column 4)\n  1 | 1 +;;\n    |    ^\n"
                  "x;;", "- : int = 1\n" ]
        }
        test "빠진 분기 경고는 그 선언을 넣을 때 한 번만" {
            let outputs =
                session [ "let f o = match o with Some v -> v;;"; "f (Some 1);;"; "let g o = match o with Some v -> v;;" ]
            Expect.stringStarts outputs[0] "Warning: this match is not exhaustive. Example of a value not matched: None" ""
            Expect.equal outputs[1] "- : int = 1\n" "앞 선언의 경고를 되풀이하지 않음"
            Expect.stringStarts outputs[2] "Warning: this match is not exhaustive" "같은 모양의 새 선언은 다시 경고"
        }
        test "쓰이지 않는 분기 경고" {
            expectSession
                [ "match 1 with _ -> 0 | 1 -> 1;;",
                  "Warning: this match case is unused (line 1, column 23)\n  1 | match 1 with _ -> 0 | 1 -> 1;;\n    |                       ^\n- : int = 0\n" ]
        }
        test "입력 하나에 항목 여럿: 차례로 처리" {
            expectSession [ "let a = 1\nlet b = a + 1\n;; a + b;;", "val a : int = 1\nval b : int = 2\n- : int = 3\n" ]
        }
        test "run: 여러 줄 입력, #quit;;, 입력 끝" {
            use out = new StringWriter()
            let input = "let rec f n =\n  if n = 0 then 0 else n + f (n - 1);;\nf 100;;\n#quit;;\nf 1;;\n"
            Repl.run "banner" (new StringReader(input)) out
            Expect.equal
                (out.ToString().Replace("\r\n", "\n"))
                "banner\n#   val f : int -> int = <fun>\n# - : int = 5050\n# "
                "#quit;; 뒤는 처리하지 않음"
        }
        test "run: ;; 없이 끝난 마지막 입력도 처리" {
            use out = new StringWriter()
            Repl.run "" (new StringReader("1 + 1")) out
            Expect.equal (out.ToString().Replace("\r\n", "\n")) "\n#   - : int = 2\n\n" "이어 쓰기 프롬프트 다음 입력 끝"
        }
    ]
