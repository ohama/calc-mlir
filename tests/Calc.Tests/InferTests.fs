module InferTests

open Expecto
open FsCheck
open Eval
open Infer

let typeOf input = typeToString (typecheck (Program.load input))

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
    | VData ("exn", _, _), TyCon ("exn", []) -> true
    | VData (typeName, _, _), TyCon (name, _) -> name.StartsWith(typeName + "@")
    | VTuple vs, TyTuple ts -> List.length vs = List.length ts && List.forall2 valueMatches vs ts
    | VRef cell, TyCon ("ref", [ e ]) -> valueMatches cell.Value e
    | VRecord (typeName, _), TyCon (name, _) -> name.StartsWith(typeName + "@")
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
      // 타입 표기
      "(fun x -> x : int -> int)", "int -> int"
      "fun (x: string) -> x", "string -> string"
      "let f (x: int) y : string = y in f", "int -> string -> string"
      "let x : int list = [] in x", "int list"
      "let id (x: 'a) : 'a = x in (id 1, id \"a\")", "int * string"
      "fun (x: 'a) (y: 'a) -> (x, y)", "'a -> 'a -> 'a * 'a"
      "let rec len (xs: 'a list) : int = match xs with [] -> 0 | _ :: t -> 1 + len t in len", "'a list -> int"
      "let f (x: 'a) = let g (y: 'a) = (x, y) in g in f", "'a -> 'a -> 'a * 'a"
      "let f (x: 'a) = let id (y: 'b) = y in (id 1, id true, x) in f", "'a -> int * bool * 'a"
      "type ('a, 'b) pair = P of 'a * 'b in fun (p: (int, 'b) pair) -> p", "(int, 'a) pair -> (int, 'a) pair"
      "(None : int option)", "int option"
      // 예외
      "raise", "exn -> 'a"
      "failwith", "string -> 'a"
      "Failure", "string -> exn"
      "Not_found", "exn"
      "fun x -> try x with Not_found -> 0", "int -> int"
      "exception E of int * string in E", "int * string -> exn"
      "fun f -> try f () with Failure m -> m", "(unit -> string) -> string"
      "let f n = if n > 0 then n else failwith \"neg\" in f", "int -> int"
      // 포맷: 포맷 문자열 리터럴이 타입을 정함
      "sprintf \"%d is %s\"", "int -> string -> string"
      "sprintf \"%b %A %A\"", "bool -> 'a -> 'b -> string"
      "sprintf \"100%%\"", "string"
      "printf \"%d\"", "int -> unit"
      "printfn \"hi\"", "unit"
      "map (sprintf \"<%d>\")", "int list -> string list"
      "let sprintf x = x + 1 in sprintf 2", "int" // 가린 sprintf 는 보통 함수
      "fun x -> $\"a{x}b\"", "'a -> string"
      "fun x -> $\"%d{x}\"", "int -> string"
      "(fun (x: 'a) -> x + 1)", "int -> int"
      // 가변 참조와 반복문
      "ref", "'a -> 'a ref"
      "fun r -> !r", "'a ref -> 'a"
      "fun r v -> r := v", "'a ref -> 'a -> unit"
      "incr", "int ref -> unit"
      "ref [1]", "int list ref"
      "(ref 1 : int ref)", "int ref"
      "fun r -> r := !r + 1", "int ref -> unit"
      "let r = ref [] in r := [1]; !r", "int list"
      "while true do () done", "unit"
      "for i = 1 to 3 do () done", "unit"
      "fun n -> for i = 1 to n do println i done", "int -> unit"
      // value restriction: 함수 호출의 결과는 generalize 하지 않음 (첫 사용이 타입을 정함). 값(fun, 생성자, 리터럴)은 generalize
      "let id x = x in let f = id id in f 1", "int"
      "let f () = ref [] in let a = f () in let b = f () in a := [1]; b := [\"x\"]; (!a, !b)", "int list * string list"
      "let xs = [] in (1 :: xs, \"a\" :: xs)", "int list * string list"
      "let p = (None, []) in let (a, b) = p in (Some 1 = a, Some \"x\" = a, 1 :: b, \"a\" :: b)", "bool * bool * int list * string list"
      "let mk = fun () -> let r = ref [] in r in let a = mk () in a := [1]; let b = mk () in b := [true]; (!a, !b)", "int list * bool list"
      // 모듈
      "module M = struct type 'a t = Leaf | Node of 'a t * 'a end;; M.Node (M.Leaf, 1)", "int M.t"
      "module M = struct type t = A end;; fun (x: M.t) -> x", "M.t -> M.t"
      "module M = struct let id x = x end;; M.id", "'a -> 'a"
      // 레코드: 필드 이름이 타입을 정함
      "type p = { x: int; y: int } in { x = 1; y = 2 }", "p"
      "type p = { x: int; y: int } in fun r -> r.x + r.y", "p -> int"
      "type p = { x: int; y: int } in fun r -> { r with y = 0 }", "p -> p"
      "type 'a box = { value: 'a } in fun b -> b.value", "'a box -> 'a"
      "type 'a box = { value: 'a } in let b = { value = 1 } in let c = { value = \"a\" } in (b, c)", "int box * string box"
      "type ('k, 'v) entry = { key: 'k; value: 'v } in fun k v -> { key = k; value = v }", "'a -> 'b -> ('a, 'b) entry"
      "type p = { x: int } in type q = { x: string } in fun r -> r.x", "q -> string" // 뒤의 타입
      "type p = { x: int } in (fun (r: p) -> r)", "p -> p"
      "type p = { x: int } in let mk = { x = 1 } in mk", "p"
      "type 'a box = { value: 'a } in let empty = { value = [] } in (1 :: empty.value, \"a\" :: empty.value)", "int list * string list" // 값이라 generalize
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
            // 타입 표기
            expectTypeError "(\"a\" : int)" "Type mismatch: expected int but got string"
            expectTypeError "let f (x: int) : string = x in f" "Type mismatch: expected string but got int"
            expectTypeError "fun (x: int) -> x + \"a\"" "Type mismatch: expected int but got string"
            expectTypeError "let f (x: 'a) (y: 'a) = (x, y) in f 1 \"a\"" "Type mismatch: expected int but got string"
            expectTypeError "(1 : foo)" "Unknown type: foo"
            expectTypeError "([] : list)" "Type list expects 1 type argument(s) but got 0"
            expectTypeError "(1 : (int, int) option)" "Type option expects 1 type argument(s) but got 2"
            // 예외
            expectTypeError "try 1 with Not_found -> \"a\"" "Type mismatch: expected int but got string"
            expectTypeError "try 1 with Some x -> x" "Type mismatch: expected exn but got 'a option"
            expectTypeError "raise 1" "Type mismatch: expected exn but got int"
            expectTypeError "exception E of 'a in 1" "Unbound type variable: 'a"
            expectTypeError "exception E of foo in 1" "Unknown type: foo"
            expectTypeError "exception E in exception E in 1" "Exception E is already defined"
            // 범위가 겹치지 않아도: 실행 중에는 이름으로 구분하므로 v 가 안쪽 E s 와 맞아 버림
            expectTypeError "let v = (exception E of int in E 1) in exception E of string in match v with E s -> s | _ -> \"\"" "Exception E is already defined"
            expectTypeError "exception Failure in 1" "Exception Failure is already defined"
            // 포맷
            expectTypeError "sprintf" "The format string of sprintf must be a string literal"
            expectTypeError "let f = printfn in f \"%d\" 1" "The format string of printfn must be a string literal"
            expectTypeError "sprintf \"%d\" \"x\"" "Type mismatch: expected int but got string"
            expectTypeError "sprintf \"%q\" 1" "Unknown format specifier: %q"
            expectTypeError "sprintf \"50%\"" "Missing format specifier after %"
            expectTypeError "$\"%s{1}\"" "Type mismatch: expected string but got int"
            // 모듈
            expectTypeError "module M = struct let f x = x end;; M.g" "Unbound variable: M.g"
            expectTypeError "module M = struct let f x = x end;; f" "Unbound variable: f"
            expectTypeError "module M = struct type t = A end;; A" "Unknown constructor: A"
            expectTypeError "module M = struct type t = A end;; fun (x: t) -> x" "Unknown type: t"
            expectTypeError "module M = struct let f x = x + 1 end;; M.f true" "Type mismatch: expected int but got bool"
            // 레코드
            expectTypeError "fun r -> r.x" "Unknown record field: x"
            expectTypeError "type p = { x: int; y: int } in { x = 1 }" "Missing record field: y"
            expectTypeError "type p = { x: int; y: int; z: int } in { y = 1 }" "Missing record field: x, z"
            expectTypeError "type p = { x: int } in { x = 1; y = 2 }" "The record field y does not belong to type p"
            expectTypeError "type p = { x: int } in type q = { y: int } in { x = 1; y = 2 }" "The record field y does not belong to type p"
            expectTypeError "type p = { x: int } in type q = { y: int } in fun (a: p) -> { a with y = 1 }" "Type mismatch: expected q but got p"
            expectTypeError "type p = { x: int; y: string } in { x = 1; y = 2 }" "Type mismatch: expected string but got int"
            expectTypeError "type p = { x: int } in (1).x" "Type mismatch: expected p but got int"
            expectTypeError "type p = { x: int } in { x = 1 } < { x = 2 }" "Type mismatch: expected int or string but got p"
            expectTypeError "type p = { x: 'a } in 1" "Unbound type variable: 'a"
            expectTypeError "type p = { x: foo } in 1" "Unknown type: foo"
            // 가변 참조와 반복문
            expectTypeError "!5" "Type mismatch: expected 'a ref but got int"
            expectTypeError "let r = ref 0 in r := \"a\"" "Type mismatch: expected int but got string"
            expectTypeError "ref 1 < ref 2" "Type mismatch: expected int or string but got int ref"
            expectTypeError "while 1 do () done" "Type mismatch: expected bool but got int"
            expectTypeError "for i = \"a\" to 3 do () done" "Type mismatch: expected int but got string"
            expectTypeError "for i = 1 to 3 do () done + 1" "Type mismatch: expected int or string but got unit"
            // value restriction: ref [] 를 generalize 하면 서로 다른 타입의 리스트를 넣고 꺼낼 수 있음
            expectTypeError "let r = ref [] in r := [1]; r := [\"a\"]; !r" "Type mismatch: expected int list but got string list"
            expectTypeError "let r = ref [] in let f x = r := [x]; x in (f 1, f \"a\")" "Type mismatch: expected int but got string"
            expectTypeError "let id x = x in let f = id id in (f 1, f \"a\")" "Type mismatch: expected int but got string"
            expectTypeError "let (r, n) = (ref [], 0) in r := [1]; r := [true]" "Type mismatch: expected int list but got bool list"
        ]

        test "모든 내장 함수와 생성자에 타입이 있음 (sprintf, printf, printfn 은 포맷 문자열을 주면, ! 와 := 는 연산자로)" {
            for name in builtins |> Map.keys do
                let input =
                    match name with
                    | "!" -> "fun r -> !r"
                    | ":=" -> "fun r v -> r := v"
                    | _ when Format.kinds.Contains name -> name + " \"\""
                    | _ -> name
                Expect.isNotEmpty (typeOf input) name
        }

        test "건전성: 타입이 맞는 식은 실행 중 타입 오류 없이 그 타입의 값이 됨" {
            let mutable checkedCount = 0
            for input, _ in EvalTests.cases do
                let ast = Program.load input
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
                        typecheck (Program.load input) |> ignore
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
