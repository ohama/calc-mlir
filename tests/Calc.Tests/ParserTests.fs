module ParserTests

open Expecto
open Ast

let parse = Program.parse

/// parse 가 SyntaxError 를 내고, 메시지와 위치(1부터)가 맞는지 확인
let expectParseError input message line column =
    Expect.throwsC
        (fun () -> parse input |> ignore)
        (fun ex ->
            match ex with
            | Errors.SyntaxError (m, l, c) -> Expect.equal (m, l, c) (message, line, column) input
            | _ -> failtestf "SyntaxError 가 아님: %s" (ex.GetType().Name))

[<Tests>]
let tests =
    testList "Parser" [
        test "정수" {
            Expect.equal (parse "7") (Number 7) ""
        }
        test "*가 +보다 우선" {
            Expect.equal
                (parse "2 + 3 * 4")
                (Add (Number 2, Multiply (Number 3, Number 4)))
                ""
        }
        test "/가 -보다 우선" {
            Expect.equal
                (parse "8 - 6 / 2")
                (Subtract (Number 8, Divide (Number 6, Number 2)))
                ""
        }
        test "괄호" {
            Expect.equal
                (parse "(2 + 3) * 4")
                (Multiply (Add (Number 2, Number 3), Number 4))
                ""
        }
        test "- 는 왼쪽 결합" {
            Expect.equal
                (parse "10 - 3 - 2")
                (Subtract (Subtract (Number 10, Number 3), Number 2))
                ""
        }
        test "/ 는 왼쪽 결합" {
            Expect.equal
                (parse "8 / 4 / 2")
                (Divide (Divide (Number 8, Number 4), Number 2))
                ""
        }
        test "단항 마이너스는 이항 연산보다 우선" {
            Expect.equal
                (parse "-5 + 2")
                (Add (Negate (Number 5), Number 2))
                ""
        }
        test "중첩 단항 마이너스" {
            Expect.equal (parse "--3") (Negate (Negate (Number 3))) ""
        }
        test "변수" {
            Expect.equal (parse "x") (Var "x") ""
        }
        test "let 식" {
            Expect.equal (parse "let x = 5 in x") (Let ("x", Number 5, Var "x")) ""
        }
        test "let 본문은 오른쪽으로 최대한 확장" {
            Expect.equal
                (parse "let x = 1 in x + 2")
                (Let ("x", Number 1, Add (Var "x", Number 2)))
                ""
        }
        test "let 바인딩 값에 식" {
            Expect.equal
                (parse "let x = 1 + 2 in x")
                (Let ("x", Add (Number 1, Number 2), Var "x"))
                ""
        }
        test "중첩 let" {
            Expect.equal
                (parse "let x = 1 in let y = 2 in x + y")
                (Let ("x", Number 1, Let ("y", Number 2, Add (Var "x", Var "y"))))
                ""
        }
        test "괄호로 let 범위 제한" {
            Expect.equal
                (parse "(let x = 1 in x) + 10")
                (Add (Let ("x", Number 1, Var "x"), Number 10))
                ""
        }
        test "이항 연산 오른쪽의 let" {
            Expect.equal
                (parse "1 + let x = 2 in x")
                (Add (Number 1, Let ("x", Number 2, Var "x")))
                ""
        }
        test "불리언 리터럴" {
            Expect.equal (parse "true") (Bool true) ""
            Expect.equal (parse "false") (Bool false) ""
        }
        test "산술이 비교보다 우선" {
            Expect.equal
                (parse "1 + 2 < 4")
                (Less (Add (Number 1, Number 2), Number 4))
                ""
        }
        test "비교 연산자 6종" {
            Expect.equal (parse "a = b") (Equal (Var "a", Var "b")) ""
            Expect.equal (parse "a <> b") (NotEqual (Var "a", Var "b")) ""
            Expect.equal (parse "a < b") (Less (Var "a", Var "b")) ""
            Expect.equal (parse "a > b") (Greater (Var "a", Var "b")) ""
            Expect.equal (parse "a <= b") (LessEqual (Var "a", Var "b")) ""
            Expect.equal (parse "a >= b") (GreaterEqual (Var "a", Var "b")) ""
        }
        test "비교는 왼쪽 결합" {
            Expect.equal
                (parse "1 < 2 < 3")
                (Less (Less (Number 1, Number 2), Number 3))
                ""
        }
        test "비교가 && 보다 우선" {
            Expect.equal
                (parse "a < b && c")
                (And (Less (Var "a", Var "b"), Var "c"))
                ""
        }
        test "&& 가 || 보다 우선" {
            Expect.equal
                (parse "a || b && c")
                (Or (Var "a", And (Var "b", Var "c")))
                ""
        }
        test "&&, || 는 왼쪽 결합" {
            Expect.equal (parse "a && b && c") (And (And (Var "a", Var "b"), Var "c")) ""
            Expect.equal (parse "a || b || c") (Or (Or (Var "a", Var "b"), Var "c")) ""
        }
        test "not 은 비교보다 우선" {
            Expect.equal
                (parse "not a = b")
                (Equal (Not (Var "a"), Var "b"))
                ""
        }
        test "if-then-else" {
            Expect.equal
                (parse "if c then 1 else 2")
                (If (Var "c", Number 1, Number 2))
                ""
        }
        test "else 분기는 오른쪽으로 최대한 확장" {
            Expect.equal
                (parse "if c then 1 else 2 + 3")
                (If (Var "c", Number 1, Add (Number 2, Number 3)))
                ""
        }
        test "then 분기 안의 if" {
            Expect.equal
                (parse "if a then if b then 1 else 2 else 3")
                (If (Var "a", If (Var "b", Number 1, Number 2), Number 3))
                ""
        }
        test "괄호로 if 범위 제한" {
            Expect.equal
                (parse "(if c then 1 else 2) + 10")
                (Add (If (Var "c", Number 1, Number 2), Number 10))
                ""
        }
        test "let 값으로 비교식" {
            Expect.equal
                (parse "let x = 1 = 1 in x")
                (Let ("x", Equal (Number 1, Number 1), Var "x"))
                ""
        }
        test "함수 호출" {
            Expect.equal (parse "f x") (App (Var "f", Var "x")) ""
        }
        test "함수 호출은 왼쪽 결합 (커링)" {
            Expect.equal (parse "f x y") (App (App (Var "f", Var "x"), Var "y")) ""
        }
        test "함수 호출이 이항 연산보다 우선" {
            Expect.equal
                (parse "f x + g y")
                (Add (App (Var "f", Var "x"), App (Var "g", Var "y")))
                ""
        }
        test "함수 호출이 단항 마이너스보다 우선" {
            Expect.equal (parse "-f x") (Negate (App (Var "f", Var "x"))) ""
        }
        test "f -1 은 뺄셈" {
            Expect.equal (parse "f -1") (Subtract (Var "f", Number 1)) ""
        }
        test "괄호로 인자 묶기" {
            Expect.equal
                (parse "f (g x)")
                (App (Var "f", App (Var "g", Var "x")))
                ""
            Expect.equal (parse "f (-1)") (App (Var "f", Negate (Number 1))) ""
        }
        test "람다" {
            Expect.equal (parse "fun x -> x") (Lambda ("x", Var "x")) ""
        }
        test "람다 본문은 오른쪽으로 최대한 확장" {
            Expect.equal
                (parse "fun x -> x + 1")
                (Lambda ("x", Add (Var "x", Number 1)))
                ""
        }
        test "여러 인자 람다는 중첩 람다" {
            Expect.equal
                (parse "fun x y -> x")
                (Lambda ("x", Lambda ("y", Var "x")))
                ""
        }
        test "람다 즉시 호출" {
            Expect.equal
                (parse "(fun x -> x) 1")
                (App (Lambda ("x", Var "x"), Number 1))
                ""
        }
        test "let 함수 정의는 람다의 문법 설탕" {
            Expect.equal
                (parse "let f x y = x + y in f 1 2")
                (Let ("f",
                      Lambda ("x", Lambda ("y", Add (Var "x", Var "y"))),
                      App (App (Var "f", Number 1), Number 2)))
                ""
        }
        test "let rec" {
            Expect.equal
                (parse "let rec f n = f n in f 1")
                (LetRec ("f", "n", App (Var "f", Var "n"), App (Var "f", Number 1)))
                ""
        }
        test "let rec 여러 인자" {
            Expect.equal
                (parse "let rec f a b = a in f")
                (LetRec ("f", "a", Lambda ("b", Var "a"), Var "f"))
                ""
        }
        test "문자열 리터럴" {
            Expect.equal (parse "\"a\"") (Str "a") ""
        }
        test "문자열 연결은 Add" {
            Expect.equal (parse "\"a\" + s") (Add (Str "a", Var "s")) ""
        }
        test "문자열은 함수 인자가 될 수 있음 (Atom)" {
            Expect.equal (parse "f \"a\" \"b\"") (App (App (Var "f", Str "a"), Str "b")) ""
        }
        test "빈 리스트" {
            Expect.equal (parse "[]") Nil ""
        }
        test "리스트 리터럴은 Cons 로 바뀜" {
            Expect.equal
                (parse "[1; 2]")
                (Cons (Number 1, Cons (Number 2, Nil)))
                ""
        }
        test ":: 는 오른쪽 결합" {
            Expect.equal
                (parse "1 :: 2 :: []")
                (Cons (Number 1, Cons (Number 2, Nil)))
                ""
        }
        test ":: 는 + 보다 약함" {
            Expect.equal
                (parse "1 + 2 :: xs")
                (Cons (Add (Number 1, Number 2), Var "xs"))
                ""
        }
        test ":: 는 비교보다 강함" {
            Expect.equal
                (parse "x :: xs = ys")
                (Equal (Cons (Var "x", Var "xs"), Var "ys"))
                ""
        }
        test "함수 호출이 :: 보다 우선" {
            Expect.equal
                (parse "f x :: g y")
                (Cons (App (Var "f", Var "x"), App (Var "g", Var "y")))
                ""
        }
        test "리스트는 함수 인자가 될 수 있음 (Atom)" {
            Expect.equal (parse "f [1]") (App (Var "f", Cons (Number 1, Nil))) ""
        }
        test "리스트 원소는 식" {
            Expect.equal
                (parse "[1 + 2; f x]")
                (Cons (Add (Number 1, Number 2), Cons (App (Var "f", Var "x"), Nil)))
                ""
        }
        test "match" {
            Expect.equal (parse "match xs with [] -> 0 | h :: t -> h") (Match (Var "xs", [ (PNil, None, Number 0); (PCons (PVar "h", PVar "t"), None, Var "h") ])) ""
        }
        test "match: 첫 | 생략 가능" {
            Expect.equal (parse "match xs with | [] -> 0 | h :: t -> h") (Match (Var "xs", [ (PNil, None, Number 0); (PCons (PVar "h", PVar "t"), None, Var "h") ])) ""
        }
        test "match: 분기 순서를 그대로 유지" {
            Expect.equal
                (parse "match xs with h :: t -> h | [] -> 0")
                (Match (Var "xs", [ (PCons (PVar "h", PVar "t"), None, Var "h"); (PNil, None, Number 0) ]))
                ""
        }
        test "match: 분기 하나, 분기 여러 개" {
            Expect.equal (parse "match x with _ -> 0") (Match (Var "x", [ (PWildcard, None, Number 0) ])) ""
            Expect.equal
                (parse "match x with 1 -> 10 | 2 -> 20 | _ -> 0")
                (Match (Var "x", [ (PInt 1, None, Number 10); (PInt 2, None, Number 20); (PWildcard, None, Number 0) ]))
                ""
        }
        test "match 마지막 분기 본문은 오른쪽으로 최대한 확장" {
            Expect.equal
                (parse "match xs with [] -> 0 | h :: t -> h + 1")
                (Match (Var "xs", [ (PNil, None, Number 0); (PCons (PVar "h", PVar "t"), None, Add (Var "h", Number 1)) ]))
                ""
        }
        test "중첩 match: 뒤의 분기는 모두 안쪽 match 것 (F# 와 같음)" {
            Expect.equal
                (parse "match a with [] -> match b with [] -> 1 | x :: y -> 2 | h :: t -> 3")
                (Match (Var "a",
                        [ (PNil, None,
                           Match (Var "b",
                                  [ (PNil, None, Number 1)
                                    (PCons (PVar "x", PVar "y"), None, Number 2)
                                    (PCons (PVar "h", PVar "t"), None, Number 3) ])) ]))
                ""
        }
        test "중첩 match: 괄호로 감싸면 바깥 분기" {
            Expect.equal
                (parse "match a with [] -> (match b with _ -> 1) | _ -> 2")
                (Match (Var "a", [ (PNil, None, Match (Var "b", [ (PWildcard, None, Number 1) ])); (PWildcard, None, Number 2) ]))
                ""
        }
        test "when 가드" {
            Expect.equal
                (parse "match n with x when x > 0 -> x | _ -> 0")
                (Match (Var "n", [ (PVar "x", Some (Greater (Var "x", Number 0)), Var "x"); (PWildcard, None, Number 0) ]))
                ""
        }
        test "패턴: 리터럴" {
            let arm p = match parse (sprintf "match x with %s -> 0" p) with
                        | Match (_, [ (pat, _, _) ]) -> pat
                        | e -> failtestf "%A" e
            Expect.equal (arm "42") (PInt 42) ""
            Expect.equal (arm "-1") (PInt -1) ""
            Expect.equal (arm "true") (PBool true) ""
            Expect.equal (arm "\"a\"") (PString "a") ""
            Expect.equal (arm "[]") PNil ""
            Expect.equal (arm "_") PWildcard ""
        }
        test "패턴: :: 는 오른쪽 결합, 리스트 패턴은 PCons 로 바뀜" {
            let arm p = match parse (sprintf "match x with %s -> 0" p) with
                        | Match (_, [ (pat, _, _) ]) -> pat
                        | e -> failtestf "%A" e
            Expect.equal (arm "a :: b :: t") (PCons (PVar "a", PCons (PVar "b", PVar "t"))) ""
            Expect.equal (arm "[a; b]") (PCons (PVar "a", PCons (PVar "b", PNil))) ""
            Expect.equal (arm "[x]") (PCons (PVar "x", PNil)) ""
            Expect.equal (arm "(a, _)") (PTuple [ PVar "a"; PWildcard ]) ""
            Expect.equal (arm "(a, (b, c))") (PTuple [ PVar "a"; PTuple [ PVar "b"; PVar "c" ] ]) ""
            Expect.equal (arm "(h :: t)") (PCons (PVar "h", PVar "t")) ""
            Expect.equal (arm "(1, x) :: _") (PCons (PTuple [ PInt 1; PVar "x" ], PWildcard)) ""
        }
        test "패턴 안의 같은 이름은 오류 (위치 포함)" {
            expectParseError "match p with (a, a) -> a" "Duplicate variable in pattern: a" 1 14
            expectParseError "match p with | _ -> 0 | x :: x -> 1" "Duplicate variable in pattern: x" 1 25
            expectParseError "let (x, x) = p in x" "Duplicate variable in pattern: x" 1 5
        }
        test "생성자 식" {
            Expect.equal (parse "None") (Ctor "None") ""
            Expect.equal (parse "Some 1") (App (Ctor "Some", Number 1)) ""
            Expect.equal (parse "Some (1, 2)") (App (Ctor "Some", Tuple [ Number 1; Number 2 ])) ""
            Expect.equal (parse "map Some xs") (App (App (Var "map", Ctor "Some"), Var "xs")) ""
        }
        test "생성자 패턴" {
            let arm p = match parse (sprintf "match x with %s -> 0" p) with
                        | Match (_, [ (pat, _, _) ]) -> pat
                        | e -> failtestf "%A" e
            Expect.equal (arm "None") (PCtor ("None", None)) ""
            Expect.equal (arm "Some x") (PCtor ("Some", Some (PVar "x"))) ""
            Expect.equal (arm "Some (a, b)") (PCtor ("Some", Some (PTuple [ PVar "a"; PVar "b" ]))) ""
            Expect.equal (arm "Some (Some _)") (PCtor ("Some", Some (PCtor ("Some", Some PWildcard)))) ""
            // 생성자 패턴이 :: 보다 강함
            Expect.equal (arm "Some x :: t") (PCons (PCtor ("Some", Some (PVar "x")), PVar "t")) ""
            Expect.equal (arm "[None]") (PCons (PCtor ("None", None), PNil)) ""
        }
        test "생성자 패턴 안의 같은 이름도 오류" {
            expectParseError "match p with Some (a, a) -> a" "Duplicate variable in pattern: a" 1 14
        }
        test "type 선언" {
            Expect.equal
                (parse "type color = Red | Green in Red")
                (TypeDecl ("color", [], [ ("Red", None); ("Green", None) ], Ctor "Red"))
                ""
            Expect.equal
                (parse "type shape = | Circle of int | Rect of int * int in 0")
                (TypeDecl ("shape", [],
                           [ ("Circle", Some (TName ("int", [])))
                             ("Rect", Some (TTuple [ TName ("int", []); TName ("int", []) ])) ],
                           Number 0))
                ""
        }
        test "type 선언: 타입 변수와 타입 식" {
            Expect.equal
                (parse "type 'a tree = Leaf | Node of 'a tree * 'a * 'a tree in 0")
                (TypeDecl ("tree", [ "a" ],
                           [ ("Leaf", None)
                             ("Node", Some (TTuple [ TName ("tree", [ TVar "a" ]); TVar "a"; TName ("tree", [ TVar "a" ]) ])) ],
                           Number 0))
                ""
            let ctorType src =
                match parse (sprintf "type t = A of %s in 0" src) with
                | TypeDecl (_, _, [ (_, Some t) ], _) -> t
                | e -> failtestf "%A" e
            Expect.equal (ctorType "int list option") (TName ("option", [ TName ("list", [ TName ("int", []) ]) ])) ""
            Expect.equal (ctorType "int -> int -> bool") (TFun (TName ("int", []), TFun (TName ("int", []), TName ("bool", [])))) ""
            Expect.equal (ctorType "int * string -> bool") (TFun (TTuple [ TName ("int", []); TName ("string", []) ], TName ("bool", []))) ""
            // 괄호로 묶은 튜플 타입은 원소 하나
            Expect.equal (ctorType "int * (int * int)") (TTuple [ TName ("int", []); TTuple [ TName ("int", []); TName ("int", []) ] ]) ""
            match parse "type ('k, 'v) m = M of 'k * 'v in 0" with
            | TypeDecl (_, ps, _, _) -> Expect.equal ps [ "k"; "v" ] ""
            | e -> failtestf "%A" e
        }
        test "type 본문은 오른쪽 끝까지 이어짐" {
            Expect.equal
                (parse "type t = A in A = A")
                (TypeDecl ("t", [], [ ("A", None) ], Equal (Ctor "A", Ctor "A")))
                ""
        }
        test "같은 생성자 이름 두 번은 오류" {
            expectParseError "type c = Red | Red in 1" "Duplicate constructor: Red" 1 10
        }
        test "대문자 이름은 변수로 묶을 수 없음" {
            expectParseError "let X = 1 in X" "parse error: unexpected 'X'" 1 5
            expectParseError "fun Some -> 1" "parse error: unexpected 'Some'" 1 5
        }
        test "튜플" {
            Expect.equal (parse "(1, 2)") (Tuple [ Number 1; Number 2 ]) ""
            Expect.equal (parse "(1, x + 1, \"a\")") (Tuple [ Number 1; Add (Var "x", Number 1); Str "a" ]) ""
            Expect.equal (parse "((1, 2), 3)") (Tuple [ Tuple [ Number 1; Number 2 ]; Number 3 ]) ""
        }
        test "괄호 하나짜리는 튜플이 아님" {
            Expect.equal (parse "(1)") (Number 1) ""
        }
        test "튜플은 함수 인자가 될 수 있음 (Atom)" {
            Expect.equal (parse "f (1, 2)") (App (Var "f", Tuple [ Number 1; Number 2 ])) ""
        }
        test "let 튜플 분해" {
            Expect.equal
                (parse "let (a, b) = p in a")
                (LetPattern (PTuple [ PVar "a"; PVar "b" ], Var "p", Var "a"))
                ""
        }
        test "_ 인자" {
            Expect.equal (parse "fun _ -> 1") (Lambda ("_", Number 1)) ""
            Expect.equal (parse "let f _ x = x in f") (Let ("f", Lambda ("_", Lambda ("x", Var "x")), Var "f")) ""
        }
        test "_ 는 식에 쓸 수 없음" {
            expectParseError "_ + 1" "parse error: unexpected '_'" 1 1
        }
        test "괄호 없는 튜플은 지원하지 않음" {
            expectParseError "1, 2" "parse error: unexpected ','" 1 2
        }
        test "match 분기가 없으면 parse error" {
            Expect.throws (fun () -> parse "match xs with" |> ignore) ""
        }
        test "리스트 끝 ; 는 parse error" {
            Expect.throws (fun () -> parse "[1;]" |> ignore) ""
        }
        test "인자 없는 let rec 은 parse error" {
            Expect.throws (fun () -> parse "let rec f = 1 in f" |> ignore) ""
        }
        test "인자 없는 fun 은 parse error" {
            Expect.throws (fun () -> parse "fun -> 1" |> ignore) ""
        }
        test "else 없는 if 는 parse error" {
            Expect.throws (fun () -> parse "if true then 1" |> ignore) ""
        }
        test "in 없는 let 은 최상위 선언 (뒤가 없으면 결과는 ())" {
            Expect.equal (parse "let x = 1") (Let ("x", Number 1, Unit)) ""
        }
        test "키워드는 변수 이름이 될 수 없음" {
            Expect.throws (fun () -> parse "let in = 1 in 2" |> ignore) ""
        }
        test "불완전한 식은 parse error" {
            Expect.throwsC
                (fun () -> parse "1 +" |> ignore)
                (fun ex -> Expect.equal ex.Message "parse error: unexpected end of input (line 1, column 4)" "")
        }
        testList "parse error 위치" [
            test "예상하지 못한 토큰" {
                expectParseError "1 + + 2" "parse error: unexpected '+'" 1 5
                expectParseError "1 2 )" "parse error: unexpected ')'" 1 5
                expectParseError "[1;]" "parse error: unexpected ']'" 1 4
            }
            test "여러 글자 토큰은 전체를 보여 줌" {
                expectParseError "let in = 1 in 2" "parse error: unexpected 'in'" 1 5
                expectParseError "1 -> 2" "parse error: unexpected '->'" 1 3
            }
            test "문자열 토큰은 여는 \" 부터" {
                expectParseError "let \"ab\" = 1" "parse error: unexpected '\"ab\"'" 1 5
            }
            test "입력 끝은 마지막 글자 바로 뒤" {
                expectParseError "1 +" "parse error: unexpected end of input" 1 4
                expectParseError "(1" "parse error: unexpected end of input" 1 3
                expectParseError "let x = 1 in" "parse error: unexpected end of input" 1 13
            }
            test "입력 끝: 뒤의 공백과 줄바꿈은 건너뜀" {
                expectParseError "1 +  \n\n" "parse error: unexpected end of input" 1 4
            }
            test "식이 와야 할 곳의 입력 끝" {
                expectParseError "let x =" "parse error: unexpected end of input" 1 8
                expectParseError "  \n (" "parse error: unexpected end of input" 2 3
            }
            test "여러 줄" {
                expectParseError "let x = 1 in\n  let y = 2\n  x + y" "parse error: unexpected end of input" 3 8
                expectParseError "1 +\n\n  )" "parse error: unexpected ')'" 3 3
            }
            test "렉서 오류도 위치와 함께 전달" {
                expectParseError "1 + $" "Unexpected character: $" 1 5
            }
        ]
        test "오류 위치 표시" {
            Expect.equal
                (Program.errorLocation "1 + + 2" 1 5)
                [ "  1 | 1 + + 2"; "    |     ^" ]
                ""
            Expect.equal
                (Program.errorLocation "a\nbc\r\nd" 2 3)
                [ "  2 | bc"; "    |   ^" ]
                ""
            // 탭은 그대로 두어 ^ 가 같은 열에 오게 함
            Expect.equal
                (Program.errorLocation "\tx )" 1 4)
                [ "  1 | \tx )"; "    | \t  ^" ]
                ""
            Expect.equal (Program.errorLocation "1" 5 1) [] ""
        }
        test "닫히지 않은 괄호는 parse error" {
            Expect.throws (fun () -> parse "(1 + 2" |> ignore) ""
        }
        testList "최상위 선언과 ;;" [
            test "선언을 차례로 쓰고 ;; 뒤에 식: let ... in 사슬로" {
                Expect.equal
                    (parse "let x = 1\nlet y = 2\n;; x + y")
                    (Let ("x", Number 1, Let ("y", Number 2, Add (Var "x", Var "y"))))
                    ""
            }
            test "let rec, type, 패턴 선언" {
                Expect.equal
                    (parse "let rec f n = n\ntype t = A | B of int\nlet (a, b) = (1, 2)\nlet () = ()\nlet _ = 3")
                    (LetRec ("f", "n", Var "n",
                        TypeDecl ("t", [], [ "A", None; "B", Some (TName ("int", [])) ],
                            LetPattern (PTuple [ PVar "a"; PVar "b" ], Tuple [ Number 1; Number 2 ],
                                LetPattern (PUnit, Unit,
                                    LetPattern (PWildcard, Number 3, Unit))))))
                    "끝이 선언이면 결과는 ()"
            }
            test "식 사이의 ;; 는 순서 실행" {
                Expect.equal (parse "1;; 2;;") (Seq (Number 1, Number 2)) "끝의 ;; 도 됨"
            }
            test "선언 바로 뒤의 식은 앞 선언에 이어짐 (OCaml 과 같음, ;; 가 필요)" {
                Expect.equal (parse "let x = f\ng") (Let ("x", App (Var "f", Var "g"), Unit)) ""
            }
            test "식 하나만 있는 프로그램은 예전과 같음" {
                Expect.equal (parse "let x = 1 in x") (Let ("x", Number 1, Var "x")) ""
            }
        ]

        testList "unit 과 ;" [
            test "() 는 Unit, 패턴 () 는 PUnit" {
                Expect.equal (parse "()") Unit ""
                Expect.equal (parse "match () with () -> 1") (Match (Unit, [ PUnit, None, Number 1 ])) ""
            }
            test "매개변수 () 는 unit 패턴으로 확인" {
                Expect.equal
                    (parse "fun () -> 1")
                    (Lambda ("()", Match (Var "()", [ PUnit, None, Number 1 ])))
                    ""
            }
            test "e1; e2; e3 는 오른쪽으로" {
                Expect.equal (parse "a; b; c") (Seq (Var "a", Seq (Var "b", Var "c"))) ""
            }
            test "let 본문, fun 본문, match 분기는 ; 까지 포함" {
                Expect.equal (parse "let x = 1 in a; b") (Let ("x", Number 1, Seq (Var "a", Var "b"))) "let"
                Expect.equal (parse "fun x -> a; b") (Lambda ("x", Seq (Var "a", Var "b"))) "fun"
                Expect.equal
                    (parse "match x with A -> a; b | B -> c")
                    (Match (Var "x", [ PCtor ("A", None), None, Seq (Var "a", Var "b"); PCtor ("B", None), None, Var "c" ]))
                    "분기"
            }
            test "if 의 else 분기는 ; 앞에서 끝남 (OCaml 과 같음)" {
                Expect.equal (parse "if c then a else b; d") (Seq (If (Var "c", Var "a", Var "b"), Var "d")) ""
            }
            test "리스트 안의 ; 는 구분자, 괄호 안은 순서 실행" {
                Expect.equal (parse "[a; b]") (Cons (Var "a", Cons (Var "b", Nil))) ""
                Expect.equal (parse "[(a; b)]") (Cons (Seq (Var "a", Var "b"), Nil)) ""
                Expect.equal (parse "(a; b)") (Seq (Var "a", Var "b")) ""
            }
        ]

        testList "작은 문법: %, |>, >>, function, or 패턴, let rec ... and" [
            test "% 는 * / 와 같은 자리" {
                Expect.equal (parse "1 + 2 * 3 % 4") (Add (Number 1, Modulo (Multiply (Number 2, Number 3), Number 4))) ""
            }
            test "x |> f 는 x 를 먼저 계산 (let $pipe = x in f $pipe), 비교와 같은 자리에서 왼쪽부터" {
                Expect.equal (parse "x |> f") (Let ("$pipe", Var "x", App (Var "f", Var "$pipe"))) ""
                Expect.equal
                    (parse "a + 1 |> f |> g")
                    (Let ("$pipe", Let ("$pipe", Add (Var "a", Number 1), App (Var "f", Var "$pipe")), App (Var "g", Var "$pipe")))
                    "+ 가 더 강하고, |> 는 왼쪽부터"
            }
            test "f >> g" {
                Expect.equal
                    (parse "f >> g")
                    (Let ("$f", Var "f", Let ("$g", Var "g", Lambda ("$x", App (Var "$g", App (Var "$f", Var "$x"))))))
                    ""
            }
            test "function 은 인자 하나를 받는 match" {
                Expect.equal
                    (parse "function 0 -> a | _ -> b")
                    (Lambda ("$function", Match (Var "$function", [ PInt 0, None, Var "a"; PWildcard, None, Var "b" ])))
                    ""
            }
            test "or 패턴: | 가 패턴에서 가장 약함, 괄호 안에서도" {
                Expect.equal
                    (parse "match x with 1 | 2 -> a | _ -> b")
                    (Match (Var "x", [ POr (PInt 1, PInt 2), None, Var "a"; PWildcard, None, Var "b" ]))
                    "분기 구분자와 섞이지 않음"
                Expect.equal (parse "match x with _ :: _ | [] -> 0") (Match (Var "x", [ POr (PCons (PWildcard, PWildcard), PNil), None, Number 0 ])) ""
                Expect.equal (parse "match x with Some (1 | 2) -> 0") (Match (Var "x", [ PCtor ("Some", Some (POr (PInt 1, PInt 2))), None, Number 0 ])) ""
            }
            test "or 패턴의 두 쪽은 같은 이름을 묶어야 함" {
                expectParseError "match o with Some x | None -> 0" "Variable x must occur on both sides of this | pattern" 1 14
            }
            test "let rec ... and ... 는 LetRecs (하나면 예전처럼 LetRec)" {
                Expect.equal
                    (parse "let rec f x = g x and g y = y in f 1")
                    (LetRecs ([ "f", "x", App (Var "g", Var "x"); "g", "y", Var "y" ], App (Var "f", Number 1)))
                    ""
                Expect.equal (parse "let rec f x = x and g y = y") (LetRecs ([ "f", "x", Var "x"; "g", "y", Var "y" ], Unit)) "최상위"
                expectParseError "let rec f x = 1 and f y = 2 in 0" "Variable f is bound several times in this let rec" 1 1
            }
            test "and 는 키워드" {
                expectParseError "let and = 1 in and" "parse error: unexpected 'and'" 1 5
            }
        ]

        testList "타입 표기" [
            test "(e : t) 는 Annot, 괄호가 필요함" {
                Expect.equal (parse "(x : int)") (Annot (Var "x", TName ("int", []))) ""
                Expect.equal
                    (parse "(f : int -> int list)")
                    (Annot (Var "f", TFun (TName ("int", []), TName ("list", [ TName ("int", []) ]))))
                    ""
                Expect.equal (parse "(p : (int, 'a) pair)") (Annot (Var "p", TName ("pair", [ TName ("int", []); TVar "a" ]))) "타입 인자 여럿"
                Expect.equal
                    (parse "(p : (int, string) pair list)")
                    (Annot (Var "p", TName ("list", [ TName ("pair", [ TName ("int", []); TName ("string", []) ]) ])))
                    ""
            }
            test "매개변수 (x: t) 는 본문 앞에서 let x = (x : t)" {
                Expect.equal
                    (parse "fun (x: int) y -> x")
                    (Lambda ("x", Let ("x", Annot (Var "x", TName ("int", [])), Lambda ("y", Var "x"))))
                    ""
                Expect.equal (parse "fun (_: int) -> 0") (Lambda ("_", Let ("_", Annot (Var "_", TName ("int", [])), Number 0))) ""
            }
            test "반환 타입과 값의 타입" {
                Expect.equal
                    (parse "let f x : int = x in f")
                    (Let ("f", Lambda ("x", Annot (Var "x", TName ("int", []))), Var "f"))
                    ""
                Expect.equal (parse "let x : int = 1 in x") (Let ("x", Annot (Number 1, TName ("int", [])), Var "x")) ""
                Expect.equal (parse "let x : int = 1") (Let ("x", Annot (Number 1, TName ("int", [])), Unit)) "최상위"
                Expect.equal
                    (parse "let rec f (n: int) : int = n in f")
                    (LetRec ("f", "n", Let ("n", Annot (Var "n", TName ("int", [])), Annot (Var "n", TName ("int", []))), Var "f"))
                    ""
                Expect.equal
                    (parse "let rec f x : int = g x and g y : int = y in f")
                    (LetRecs ([ "f", "x", Annot (App (Var "g", Var "x"), TName ("int", [])); "g", "y", Annot (Var "y", TName ("int", [])) ], Var "f"))
                    ""
            }
            test "괄호 없는 e : t 는 오류 (OCaml 과 같음)" {
                expectParseError "1 : int" "parse error: unexpected ':'" 1 3
            }
        ]

        testList "주석" [
            test "// 줄 주석과 (* *) 블록 주석 (중첩 가능)" {
                Expect.equal (parse "1 // 끝까지\n+ (* 가운데 (* 중첩 *) *) 2") (Add (Number 1, Number 2)) ""
            }
            test "닫히지 않은 블록 주석은 여는 위치를 가리킴" {
                expectParseError "1 +\n  (* 열림 (* *)" "Unterminated comment" 2 3
            }
        ]

        test "빈 입력은 () (OCaml, F# 의 빈 파일처럼)" {
            Expect.equal (parse "") Unit ""
            Expect.equal (parse "  \n // 주석만\n (* 블록 *) ") Unit ""
        }
    ]
