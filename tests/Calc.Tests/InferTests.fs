module InferTests

open Expecto
open FsCheck
open Eval
open Infer

let typeOf input = typeToString (typecheck (Program.parse input))

/// 정적 타입 오류 메시지가 정확히 일치하는지 확인
let expectTypeError input message =
    test input {
        Expect.throwsC
            (fun () -> typeOf input |> ignore)
            (fun ex ->
                match ex with
                | TypeError (m, _) -> Expect.equal m message input
                | _ -> failtestf "TypeError 가 아님: %s: %s" (ex.GetType().Name) ex.Message)
    }

/// 실행 결과 값이 추론한 타입의 모양과 맞는지 (타입 변수는 무엇이든 허용)
let rec valueMatches value t =
    match value, prune t with
    | _, TyVar _ -> true
    | VInt _, TyCon ("int", [])
    | VBool _, TyCon ("bool", [])
    | VString _, TyCon ("string", [])
    | VUnit, TyCon ("unit", []) -> true
    | VList items, TyCon ("list", [ e ]) -> items |> List.forall (fun v -> valueMatches v e)
    | VData ("option", _, payload), TyCon ("option", [ e ]) ->
        payload |> Option.forall (fun v -> valueMatches v e)
    | VData (typeName, _, _), TyCon (name, _) -> name.StartsWith(typeName + "@")
    | VTuple vs, TyTuple ts -> List.length vs = List.length ts && List.forall2 valueMatches vs ts
    | (VClosure _ | VRecClosure _ | VRecGroup _ | VBuiltin _ | VConstructor _), TyFun _ -> true
    | _ -> false

let inferred =
    [ // 작은 문법
      "7 % 2", "int"
      "fun x -> x |> toString", "'a -> string"
      "fun f g -> f >> g", "('a -> 'b) -> ('b -> 'c) -> 'a -> 'c"
      "function Some x -> x | None -> 0", "int option -> int"
      "fun p -> match p with (x, 1) | (1, x) -> x | _ -> 0", "int * int -> int"
      "let rec even n = if n = 0 then true else odd (n - 1) and odd n = even n in even", "int -> bool"
      "let rec len xs = match xs with [] -> 0 | _ :: t -> 1 + len2 t and len2 ys = len ys in (len [1], len [\"a\"])", "int * int"
      // unit, ;, 출력
      "()", "unit"
      "fun () -> 1", "unit -> int"
      "println", "'a -> unit"
      "ignore", "'a -> unit"
      "println 1; \"a\"", "string"
      "let x = 1", "unit"
      // 기본
      "1 + 2", "int"
      "\"a\" + \"b\"", "string"
      "1 < 2", "bool"
      "[]", "'a list"
      "[[]]", "'a list list"
      "(1, \"a\", [true])", "int * string * bool list"
      // 함수와 let 다형성
      "fun x -> x", "'a -> 'a"
      "fun x y -> x", "'a -> 'b -> 'a"
      "fun f x -> f (f x)", "('a -> 'a) -> 'a -> 'a"
      "fun f g x -> g (f x)", "('a -> 'b) -> ('b -> 'c) -> 'a -> 'c"
      "fun x -> (x, x)", "'a -> 'a * 'a"
      "let id x = x in (id 1, id true)", "int * bool"
      "let f x = x in let g = f f in g 3", "int"
      "let pair x y = (x, y) in pair 1", "'a -> int * 'a"
      "let (a, b) = (fun x -> x, 2) in (a 1, a true)", "int * bool"
      // 재귀
      "let rec fact n = if n = 0 then 1 else n * fact (n - 1) in fact", "int -> int"
      "let rec len xs = match xs with [] -> 0 | _ :: t -> 1 + len t in len", "'a list -> int"
      "let rec map f xs = match xs with [] -> [] | h :: t -> f h :: map f t in map", "('a -> 'b) -> 'a list -> 'b list"
      // 패턴
      "fun xs -> match xs with [] -> 0 | [x] -> x | x :: y :: _ -> x + y", "int list -> int"
      "fun p -> match p with (a, b) -> (b, a)", "'a * 'b -> 'b * 'a"
      "fun o -> match o with Some x -> x | None -> 0", "int option -> int"
      // +, < 의 첫 사용이 타입을 정함, 정해지지 않으면 int
      "fun x y -> x + y", "int -> int -> int"
      "fun x y -> x < y", "int -> int -> bool"
      "fun x -> x + \"!\"", "string -> string"
      "let add x y = x + y in add \"a\" \"b\"", "string"
      // 내장 함수
      "map", "('a -> 'b) -> 'a list -> 'b list"
      "fold", "('a -> 'b -> 'a) -> 'a -> 'b list -> 'a"
      "fst", "'a * 'b -> 'a"
      "toString", "'a -> string"
      "length", "string -> int"
      "length [1]", "int"
      "length \"ab\"", "int"
      "sort [\"b\"; \"a\"]", "string list"
      "Some", "'a -> 'a option"
      "None", "'a option"
      "tryHead [1]", "int option"
      "defaultValue 0", "int option -> int"
      "fun p -> fst p + snd p", "int * int -> int"
      // 사용자 정의 타입
      "type 'a tree = Leaf | Node of 'a tree * 'a * 'a tree in Node", "'a tree * 'a * 'a tree -> 'a tree"
      "type 'a tree = Leaf | Node of 'a tree * 'a * 'a tree in let rec sum t = match t with Leaf -> 0 | Node (l, x, r) -> sum l + x + sum r in sum",
          "int tree -> int"
      "type shape = Circle of int | Rect of int * int in Rect", "int * int -> shape"
      "type t = A of int list list in A", "int list list -> t"
      "type t = F of int -> int in F", "(int -> int) -> t"
      "type ('k, 'v) entry = E of 'k * 'v in E (1, \"a\")", "(int, string) entry"
      "type 'a box = Box of 'a in fun b -> match b with Box x -> x", "'a box -> 'a"
      // 같은 이름의 다른 type 은 구분됨 (실패하는 경우는 아래 오류 목록)
      "type t = A in let v = A in type t = B in v", "t"
      // = 는 모든 타입 (함수 비교는 실행 중 오류로 남음, OCaml 과 같음)
      "(fun x -> x) = (fun x -> x)", "bool" ]

[<Tests>]
let tests =
    testList "Infer" [
        testList "추론한 타입" [
            for input, expected in inferred ->
                test input { Expect.equal (typeOf input) expected input }
        ]

        testList "타입 오류" [
            expectTypeError "[1; true]" "Type mismatch: expected int list but got bool list"
            expectTypeError "if true then 1 else false" "Type mismatch: expected int but got bool"
            expectTypeError "if 1 then 2 else 3" "Type mismatch: expected bool but got int"
            expectTypeError "1 + true" "Type mismatch: expected int but got bool"
            expectTypeError "true + 1" "Type mismatch: expected int or string but got bool"
            expectTypeError "[1] < [2]" "Type mismatch: expected int or string but got int list"
            expectTypeError "1 2" "Type mismatch: expected function but got int"
            expectTypeError "fun x -> x x" "Type mismatch: cannot construct infinite type 'a = 'a -> 'b"
            expectTypeError "fun f -> (f 1, f true)" "Type mismatch: expected int but got bool"
            expectTypeError "let add x y = x + y in (add 1 2, add \"a\" \"b\")" "Type mismatch: expected int but got string"
            expectTypeError "y + 1" "Unbound variable: y"
            expectTypeError "Nome" "Unknown constructor: Nome"
            expectTypeError "1 :: 2" "Type mismatch: expected int list but got int"
            expectTypeError "match Some 1 with Some \"a\" -> 1 | _ -> 0" "Type mismatch: expected int option but got string option"
            expectTypeError "match 1 with x when x -> 1" "Type mismatch: expected bool but got int"
            expectTypeError "match (1, 2) with (x, 1) | (1, x) -> x | (\"a\", _) -> 0" "Type mismatch: expected int * int but got string * 'a"
            expectTypeError "fun p -> match p with (x, 1) | (true, x) -> 0" "Type mismatch: expected bool but got int"
            expectTypeError "match 1 with 1 -> \"a\" | _ -> 2" "Type mismatch: expected string but got int"
            expectTypeError "match Some 1 with Some -> 0 | _ -> 1" "Constructor Some requires an argument"
            expectTypeError "match None with None 1 -> 0 | _ -> 1" "Constructor None takes no argument"
            expectTypeError "match Some 1 with Nome -> 0 | _ -> 1" "Unknown constructor: Nome"
            expectTypeError "type shape = Circle of int | Rect of int * int in Rect 1" "Type mismatch: expected int * int but got int"
            expectTypeError "type t = A of foo in 1" "Unknown type: foo"
            expectTypeError "type t = A of 'b in 1" "Unbound type variable: 'b"
            expectTypeError "type ('a, 'a) t = A in 1" "Duplicate type variable: 'a"
            expectTypeError "type t = A of list in 1" "Type list expects 1 type argument(s) but got 0"
            expectTypeError "type t = A in let v = A in type t = A in v = A" "Type mismatch: expected t/1 but got t/2"
            expectTypeError "let (a, b) = (1, 2, 3) in a" "Type mismatch: expected 'a * 'b but got int * int * int"
            expectTypeError "head 1" "Type mismatch: expected 'a list but got int"
            expectTypeError "sort [true]" "Type mismatch: expected int or string but got bool"
            expectTypeError "length 1" "Type mismatch: expected string or list but got int"
            expectTypeError "fst (1, 2, 3)" "Type mismatch: expected 'a * 'b but got int * int * int"
            expectTypeError "Some 1 = None 1" "Type mismatch: expected function but got 'a option"
            expectTypeError "defaultValue \"a\" (Some 1)" "Type mismatch: expected string option but got int option"
        ]

        test "모든 내장 함수와 생성자에 타입이 있음" {
            for name in builtins |> Map.keys do
                Expect.isNotEmpty (typeOf name) name
        }

        test "건전성: 타입이 맞는 식은 실행 중 타입 오류 없이 그 타입의 값이 됨" {
            let mutable checkedCount = 0
            for input, _ in EvalTests.cases do
                let ast = Program.parse input
                match (try Some (typecheck ast) with TypeError _ -> None) with
                | None -> ()
                | Some t ->
                    checkedCount <- checkedCount + 1
                    let value =
                        try eval ast
                        with ex -> failtestf "%s: 타입 %s 인데 실행 오류 %s" input (typeToString t) ex.Message
                    Expect.isTrue (valueMatches value t) (sprintf "%s: 값 %s 이 타입 %s 와 맞지 않음" input (formatValue value) (typeToString t))
            // 대부분의 예제가 타입 검사를 통과해야 의미 있는 테스트
            Expect.isGreaterThan checkedCount 250 "타입 검사를 통과한 예제 수"
        }

        test "동적 평가 예제 중 타입 검사가 거부하는 것은 정말 타입이 맞지 않는 것뿐" {
            let rejected =
                EvalTests.cases
                |> List.map fst
                |> List.filter (fun input ->
                    try
                        typecheck (Program.parse input) |> ignore
                        false
                    with TypeError _ -> true)
            Expect.equal
                rejected
                [ "(1, 2) = (1, 2, 3)"                                                  // 원소 수가 다른 튜플
                  "match \"1\" with 1 -> \"int\" | _ -> \"other\""                      // string 에 int 패턴
                  "match (1, 2) with (a, b, c) -> 0 | _ -> 1"                           // 원소 수가 다른 튜플 패턴
                  "type t = A in let v = A in type u = A in match v with A -> 1 | _ -> 2" ] // 다른 타입의 생성자
                ""
        }

        testList "속성" [
            testProperty "정수 식은 int" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
                typeOf (sprintf "%d + %d * %d" a b a) = "int"

            testProperty "let 다형성: id 를 여러 타입에 적용" <| fun (NonNegativeInt n) (b: bool) ->
                typeOf (sprintf "let id x = x in (id %d, id %b, id \"s\")" n b) = "int * bool * string"

            testProperty "리스트 리터럴의 타입은 원소 타입의 리스트" <| fun (xs: NonNegativeInt list) ->
                let src = "[" + (xs |> List.map (fun (NonNegativeInt n) -> string n) |> String.concat "; ") + "]"
                typeOf src = (if List.isEmpty xs then "'a list" else "int list")
        ]
    ]
