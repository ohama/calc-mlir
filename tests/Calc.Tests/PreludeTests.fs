/// Calc 로 쓴 표준 라이브러리 (src/Calc/prelude.calc, Prelude.wrap)
module PreludeTests

open Expecto
open Ast

/// wrap 이 앞에 붙인 prelude 정의의 이름 (바깥부터)
let rec private attached (expr: Expr) =
    match expr with
    | Let (name, _, body) | LetRec (name, _, _, body) -> name :: attached body
    | _ -> []

[<Tests>]
let tests =
    testList "Prelude" [
        test "쓰는 정의만 붙임 (다른 정의가 쓰는 정의도)" {
            Expect.equal (Prelude.wrap (Program.parse "1")) (Number 1) "prelude 를 쓰지 않으면 그대로"
            Expect.equal (attached (Program.load "map f xs")) [ "fold"; "reverse"; "map" ] "map 은 fold 와 reverse 를 씀"
            Expect.equal (attached (Program.load "sum (range 1 3)")) [ "fold"; "range"; "sum" ] "prelude 안의 순서"
        }

        test "prelude 전체: 경고 없이 타입 검사를 통과" {
            let _, warnings = Infer.typecheckWithWarnings (Prelude.chain (Prelude.items ()) Unit)
            Expect.isEmpty warnings ""
        }

        test "타입은 예전 내장 함수와 같음" {
            for name, expected in
                [ "fst", "'a * 'b -> 'a"
                  "snd", "'a * 'b -> 'b"
                  "ignore", "'a -> unit"
                  "isEmpty", "'a list -> bool"
                  "fold", "('a -> 'b -> 'a) -> 'a -> 'b list -> 'a"
                  "reverse", "'a list -> 'a list"
                  "append", "'a list -> 'a list -> 'a list"
                  "range", "int -> int -> int list"
                  "sum", "int list -> int"
                  "map", "('a -> 'b) -> 'a list -> 'b list"
                  "filter", "('a -> bool) -> 'a list -> 'a list"
                  "exists", "('a -> bool) -> 'a list -> bool"
                  "forall", "('a -> bool) -> 'a list -> bool"
                  "tryHead", "'a list -> 'a option"
                  "tryFind", "('a -> bool) -> 'a list -> 'a option"
                  "defaultValue", "'a -> 'a option -> 'a" ] do
                Expect.equal (InferTests.typeOf name) expected name
        }

        test "prelude 의 이름은 내장 함수가 아님 (F#, C 에 따로 없음)" {
            for item in Prelude.items () do
                match item with
                | TopLet (name, _, _) | TopLetRec (name, _, _, _) ->
                    Expect.isFalse (Eval.builtinArity.ContainsKey name) name
                | _ -> failtest "prelude 에는 let 과 let rec 만"
        }

        test "사용자가 같은 이름을 정의하면 그쪽이 가림" {
            Expect.equal (EvalTests.calc "let map = 5 in map") (Eval.VInt 5) ""
            Expect.equal (InferTests.typeOf "let map = 5 in map") "int" ""
        }

        test "긴 리스트도 스택이 자라지 않음 (인터프리터의 tail call)" {
            Expect.equal (EvalTests.calc "length (range 1 1000000)") (Eval.VInt 1000000) ""
        }
    ]
