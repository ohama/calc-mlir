/// 컴파일러 차등 테스트: 같은 식을 인터프리터와 컴파일한 실행 파일로 실행해서 결과를 비교 (docs/compiler-design.md 9장)
module CompileTests

open System
open System.Diagnostics
open System.IO
open Expecto

let private tools = Toolchain.find ()

/// 도구가 없으면 테스트를 건너뜀 (실패가 아님)
let private requireTools () =
    match tools with
    | Ok t -> t
    | Error message -> skiptestf "%s" message

let private runProcess (file: string) (args: string list) =
    let psi = ProcessStartInfo(file)
    for a in args do
        psi.ArgumentList.Add a
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Process.Start psi
    let stderrTask = p.StandardError.ReadToEndAsync()
    let stdout = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    stdout, stderrTask.Result, p.ExitCode

/// 식을 컴파일하고 실행해서 (표준 출력, 표준 에러, 종료 코드)
let rec compileAndRun (input: string) = compileAndRunWith id input

/// 도구 설정을 바꿔서 (예: 생성한 코드의 최적화 수준)
and compileAndRunWith (configure: Toolchain.Tools -> Toolchain.Tools) (input: string) =
    let t = configure (requireTools ())
    let ast = Program.load input
    Infer.typecheck ast |> ignore
    let mlir = Codegen.emitProgram ast
    let exe = Path.Combine(Path.GetTempPath(), "calc-test-" + Guid.NewGuid().ToString("N"))
    try
        Toolchain.build t mlir exe false |> ignore
        runProcess exe []
    finally
        File.Delete exe

/// 빌드하고 GC 설정 환경 변수 env 로 실행 (CALC_GC_NURSERY_KB, CALC_GC_STRESS)
let private compileAndRunGc (env: (string * string) list) (input: string) =
    let t = requireTools ()
    let ast = Program.load input
    Infer.typecheck ast |> ignore
    let exe = Path.Combine(Path.GetTempPath(), "calc-test-gc-" + Guid.NewGuid().ToString("N"))
    try
        Toolchain.build t (Codegen.emitProgram ast) exe false |> ignore
        let psi = ProcessStartInfo(exe)
        for k, v in env do
            psi.Environment[k] <- v
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        use p = Process.Start psi
        let stderrTask = p.StandardError.ReadToEndAsync()
        let stdout = p.StandardOutput.ReadToEnd()
        p.WaitForExit()
        stdout, stderrTask.Result, p.ExitCode
    finally
        File.Delete exe

/// GC 설정을 바꿔 실행하는 프로그램: 수거가 여러 번 일어나고, old 객체에 young 객체를 쓰는 곳(write barrier)을 지남
let private gcCases =
    [ "let r = ref [] in for i = 1 to 300000 do r := i :: (if i % 1000 = 0 then [] else !r) done; println (length !r, head !r)", ("(1, 300000)\n", "", 0), "ref 에 새 리스트를 거듭 씀 (:=)"
      "let a = Array.create 200000 [] in let junk = map (fun x -> [x]) (range 1 100000) in for k = 1 to 3 do for i = 0 to 199999 do a.[i] <- [i + k] done done; println (fold (fun s l -> (s + head l) % 1000003) 0 (Array.toList a), length junk)", ("(440000, 100000)\n", "", 0), "minor GC 사이에 서로 다른 칸 20만 개에 씀 (remembered set 이 커짐)"
      "let a = Array.create 1000 [] in for i = 0 to 299999 do a.[i % 1000] <- i :: (if i % 7 = 0 then [] else a.[i % 1000]) done; println (fold (fun s l -> s + length l) 0 (Array.toList a))", ("3997\n", "", 0), "큰 배열(old space 에 바로)의 원소에 새 리스트 (a.[i] <- v)"
      "let xs = range 1 300000 in println (length (append xs [0]), length (filter (fun x -> x % 3 = 0) (map (fun x -> x * 2) xs)), nth (split (join \",\" [\"a\"; \"b\"]) \",\") 1)", ("(300001, 100000, \"b\")\n", "", 0), "런타임이 앞에서부터 만드는 리스트 (append 의 tail 고치기)"
      "let rec mymap f xs = match xs with [] -> [] | x :: rest -> f x :: mymap f rest in println (length (mymap (fun x -> [x]) (range 1 300000)))", ("300000\n", "", 0), "tail call 이 아닌 재귀 30만 단계의 stack"
      "let fs = map (fun i -> fun a b -> a + b + i) (range 1 200000) in let junk = map (fun x -> (x, x)) (range 1 200000) in println (fold (fun s f -> (s + f 1 2) % 1000003) 0 fs, length junk)", ("(640000, 200000)\n", "", 0), "인자 수를 헤더에 적은 클로저가 수거 뒤에도 그대로"
      "let rec even n acc = if n = 0 then acc else odd (n - 1) (n :: acc) and odd n acc = if n = 0 then acc else even (n - 1) acc in let g = even in println (length (g 300000 []))", ("150000\n", "", 0), "let rec … and … 의 클로저"
      "let s = ref \"\" in for i = 1 to 3000 do s := !s + toString (i % 10) done; println (length !s, length (sprintf \"%s-%d\" !s 7))", ("(3000, 3002)\n", "", 0), "커지는 문자열 (2KB 가 넘으면 큰 객체)"
      "exception E of int list in println (try (let xs = range 1 200000 in raise (E (map (fun x -> x + 1) xs))) with E l -> length l)", ("200000\n", "", 0), "raise 한 예외 값 (런타임 전역 root)"
      "let xs = map (fun i -> (i * 7919) % 100003) (range 1 200000) in let ys = sort xs in println (head ys, nth ys 199999, length (sort (map toString (range 1 20000))))", ("(0, 100002, 20000)\n", "", 0), "sort 의 임시 버퍼"
      "type t = Leaf | Node of t * int * t in let rec ins t x = match t with Leaf -> Node (Leaf, x, Leaf) | Node (l, v, r) -> if x < v then Node (ins l x, v, r) else if x > v then Node (l, v, ins r x) else t in let rec build i t = if i = 0 then t else build (i - 1) (ins t ((i * 7919) % 100003)) in let rec size t = match t with Leaf -> 0 | Node (l, _, r) -> size l + 1 + size r in println (size (build 100000 Leaf))", ("100000\n", "", 0), "영속 트리 (생성자 블록)"
      "let b = Array.create 3000000 true in let a = Array.init 300000 (fun i -> Some i) in b.[5] <- false; println (b.[5], b.[6], a.[299999], Array.length a)", ("(false, true, Some 299999, 300000)\n", "", 0), "큰 bool 배열과 option 배열" ]

/// 컴파일러가 지원하고 타입 검사를 통과하는 식인지
let private compilable (input: string) =
    try
        let ast = Program.load input
        Infer.typecheck ast |> ignore
        Codegen.emitProgram ast |> ignore
        true
    with
    | Core.NotSupported _
    | Infer.TypeError _ -> false

/// 인터프리터로 실행했을 때 calc prog.calc 가 내는 것과 같은 (표준 출력, 표준 에러, 종료 코드):
/// print 의 출력, 그다음 결과 값 (() 면 없음). 오류가 나면 그때까지의 출력과 오류 메시지
let private interpret (input: string) =
    use output = new StringWriter()
    try
        match Eval.evalWithOutput output (Program.load input) with
        | Eval.VUnit -> output.ToString(), "", 0
        | value -> output.ToString() + Eval.formatValue value + "\n", "", 0
    with ex ->
        output.ToString(), "Error: " + ex.Message + "\n", 1

/// 저장소의 examples/*.calc (이름, 내용)
let private examples =
    let dir = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples")
    Directory.GetFiles(dir, "*.calc") |> Array.sort |> Array.map (fun f -> Path.GetFileName f, File.ReadAllText f) |> List.ofArray

/// 실행 중 오류가 나는 예제 (EvalTests.cases 에는 성공하는 예제만 있음)
let private runtimeErrorCases =
    [ "1 / 0"
      "10 / (5 - 5)"
      "(-2147483647 - 1) / -1"
      "true && 1 / 0 = 0"
      "if 1 / 0 = 0 then 1 else 2"
      "1 + 2 / 0"
      "let x = 1 / 0 in 5"
      "let d = 5 - 5 in if d = 0 then 10 / d else 1"
      "let f x = 10 / x in f 0"
      "let rec down n = if n = 0 then 1 / n else down (n - 1) in down 100"
      "(fun x -> x) = (fun x -> x)"
      "match [1; 2] with | [] -> 0"
      "match [] with | x :: _ -> x"
      "match (1, \"a\") with | (2, _) -> 0 | (_, \"b\") -> 1"
      "match None with | Some x -> x"
      "Some (fun x -> x) = Some (fun x -> x)"
      "type t = A of int | B in match B with | A n -> n"
      "tail []"
      "toInt \"2147483648\""
      "toInt \"\""
      "nth [1; 2] (0 - 1)"
      "map head [[1]; []]"
      "fold (fun a x -> a / x) 10 [2; 0]" ]

/// 실행 테스트: 이름, [입력, (표준 출력, 표준 에러, 종료 코드), 메시지]. 차등 테스트 예제와 함께 실행 파일 하나로 묶어 빌드함 (batch)
let private runCases =
    [
      "42",
        [
          "42", ("42\n", "", 0), ""
        ]
      "int 범위 끝",
        [
          "2147483647", ("2147483647\n", "", 0), ""
          "0", ("0\n", "", 0), ""
        ]
      "bool",
        [
          "true", ("true\n", "", 0), ""
          "false", ("false\n", "", 0), ""
        ]
      "산술",
        [
          "1 + 2 * 3", ("7\n", "", 0), ""
          "(1 + 2) * 3 - 10", ("-1\n", "", 0), ""
          "-(4 - 6)", ("2\n", "", 0), ""
        ]
      "나눗셈은 0 쪽으로 버림",
        [
          "7 / -2", ("-3\n", "", 0), ""
          "-7 / 2", ("-3\n", "", 0), ""
        ]
      "32비트에서 한 바퀴 돎 (인터프리터와 같음)",
        [
          "2147483647 + 1", ("-2147483648\n", "", 0), "+ (런타임)"
          "-2147483647 - 2", ("2147483647\n", "", 0), "-"
          "65536 * 65536", ("0\n", "", 0), "*"
          "-(-2147483647 - 1)", ("-2147483648\n", "", 0), "단항 -"
        ]
      "비교와 논리",
        [
          "1 < 2 && 2 <= 2 && 3 > 2 && 3 >= 4 = false", ("true\n", "", 0), ""
          "1 = 1 && true <> false && not (2 = 3)", ("true\n", "", 0), ""
          "-1 < 0", ("true\n", "", 0), "음수 비교"
        ]
      "if",
        [
          "if 1 < 2 then 10 else 20", ("10\n", "", 0), ""
          "if false then true else if 1 = 1 then false else true", ("false\n", "", 0), ""
        ]
      "0으로 나누기는 오류",
        [
          "1 / 0", ("", "Error: Attempted to divide by zero.\n", 1), ""
        ]
      "MinValue / -1 은 오버플로 오류",
        [
          "(-2147483647 - 1) / -1", ("", "Error: Arithmetic operation resulted in an overflow.\n", 1), ""
          "(-2147483647 - 1) / 1", ("-2147483648\n", "", 0), "-1 이 아니면 정상"
        ]
      "let 과 섀도잉",
        [
          "let x = 5 in if x > 3 then x * 2 else 0", ("10\n", "", 0), ""
          "let x = 1 in let x = x + 10 in x", ("11\n", "", 0), "섀도잉"
          "let x = 1 in (let x = 2 in x) + x", ("3\n", "", 0), "안쪽 let 이 끝나면 바깥 x"
        ]
      "if 분기 안의 let",
        [
          "let a = 3 in let b = if a > 2 then (let c = a * a in c + 1) else 0 in a + b", ("13\n", "", 0), ""
        ]
      "let 의 값은 쓰이지 않아도 계산 (인터프리터와 같음)",
        [
          "let x = 1 / 0 in 5", ("", "Error: Attempted to divide by zero.\n", 1), ""
        ]
      "클로저: 정적 범위",
        [
          "let k = 10 in let add = fun x -> x + k in add 32", ("42\n", "", 0), ""
          "let x = 1 in let f = fun y -> x + y in let x = 100 in f 1", ("2\n", "", 0), "정의할 때의 x"
        ]
      "커링과 고차 함수",
        [
          "let add x y = x + y in let inc = add 1 in inc 41", ("42\n", "", 0), "부분 적용"
          "let twice f x = f (f x) in twice (fun x -> x * 3) 7", ("63\n", "", 0), ""
          "let compose f g = fun x -> f (g x) in let inc x = x + 1 in let dbl x = x * 2 in (compose inc dbl) 5", ("11\n", "", 0), ""
        ]
      "let rec",
        [
          "let rec fact n = if n = 0 then 1 else n * fact (n - 1) in fact 10", ("3628800\n", "", 0), ""
          "let rec fib n = if n < 2 then n else fib (n - 1) + fib (n - 2) in fib 20", ("6765\n", "", 0), ""
          "let rec f f = f in f 5", ("5\n", "", 0), "인자가 자기 자신을 가림"
        ]
      "재귀 깊이 100만 (512MB 스택)",
        [
          "let rec f n = if n = 0 then 0 else 1 + f (n - 1) in f 1000000", ("1000000\n", "", 0), ""
        ]
      "함수 값",
        [
          "fun x -> x", ("<fun>\n", "", 0), "인터프리터와 같은 출력"
          "(fun x -> x) = (fun x -> x)", ("", "Error: Type error: expected int, bool, string, list, tuple or data but got function\n", 1), "함수끼리 비교는 실행 중 오류"
        ]
      "문자열 출력: 인터프리터와 같은 따옴표와 이스케이프",
        [
          "\"hello\"", ("\"hello\"\n", "", 0), ""
          "\"\"", ("\"\"\n", "", 0), "빈 문자열"
          "\"q\\\" b\\\\ n\\n t\\t r\\r\"", ("\"q\\\" b\\\\ n\\n t\\t r\\r\"\n", "", 0), ""
        ]
      "문자열 출력: UTF-16 을 UTF-8 로 (대리 쌍 포함)",
        [
          "\"한글 😀\"", ("\"한글 😀\"\n", "", 0), ""
        ]
      "문자열 연결",
        [
          "\"a\" + \"b\" + \"\"", ("\"ab\"\n", "", 0), ""
          "let greet name = \"Hello, \" + name + \"!\" in greet \"Calc\"", ("\"Hello, Calc!\"\n", "", 0), ""
          "let rec rep s n = if n = 0 then \"\" else s + rep s (n - 1) in rep \"ab\" 5", ("\"ababababab\"\n", "", 0), ""
        ]
      "문자열 비교: 내용 비교와 ordinal 순서",
        [
          "\"abc\" = \"abc\" && \"abc\" <> \"abd\" && \"\" = \"\"", ("true\n", "", 0), ""
          "\"ab\" < \"abc\" && \"B\" < \"a\" && \"Z\" < \"é\"", ("true\n", "", 0), "대문자가 소문자보다 앞 (문화권 순서가 아님)"
          // UTF-16 코드 단위 순서: 😀 는 D83D DE00 이라 U+FFFF 보다 앞 (코드 포인트 순서와 반대)
          "\"😀\" < \"\uFFFF\"", ("true\n", "", 0), ""
        ]
      "문자열 연결 10만 번",
        [
          "let rec loop n = if n = 0 then \"done\" else (let s = \"abc\" + \"def\" in if s = \"abcdef\" then loop (n - 1) else \"bad\") in loop 100000", ("\"done\"\n", "", 0), ""
        ]
      "리스트와 튜플 출력",
        [
          "[]", ("[]\n", "", 0), ""
          "[1; 2; 3]", ("[1; 2; 3]\n", "", 0), ""
          "(1, \"a\", true)", ("(1, \"a\", true)\n", "", 0), ""
          "[(1, [2]); (3, [])]", ("[(1, [2]); (3, [])]\n", "", 0), "중첩"
          "[fun x -> x]", ("[<fun>]\n", "", 0), ""
        ]
      "리스트 재귀 함수",
        [
          "let rec map f xs = match xs with | [] -> [] | x :: rest -> f x :: map f rest in map (fun x -> x * x) [1; 2; 3]", ("[1; 4; 9]\n", "", 0), ""
          "let rec rev acc xs = match xs with | [] -> acc | x :: r -> rev (x :: acc) r in rev [] [1; 2; 3]", ("[3; 2; 1]\n", "", 0), ""
        ]
      "긴 리스트 (10만 개)",
        [
          "let rec upto n acc = if n = 0 then acc else upto (n - 1) (n :: acc) in let rec sum xs = match xs with | [] -> 0 | x :: r -> x + sum r in sum (upto 100000 [])", ("705082704\n", "", 0), "합은 32비트에서 한 바퀴 돎"
        ]
      "match: 상수, 중첩, 가드",
        [
          "match (1, 2) with | (0, y) -> y | (x, y) -> x * 10 + y", ("12\n", "", 0), ""
          "match [1; 2] with | [x; y] -> x + y | _ -> 0", ("3\n", "", 0), ""
          "match \"hi\" with | \"hello\" -> 1 | \"hi\" -> 2 | _ -> 3", ("2\n", "", 0), ""
          "match [1; 2; 3] with | x :: y :: _ when x < y -> (x, y) | _ -> (0, 0)", ("(1, 2)\n", "", 0), ""
          "match 2 with | n when n > 3 -> \"big\" | _ -> \"small\"", ("\"small\"\n", "", 0), "가드가 거짓이면 다음 분기"
        ]
      "ADT 와 option: 출력",
        [
          "None", ("None\n", "", 0), ""
          "[Some 1; None; Some (-1)]", ("[Some 1; None; Some (-1)]\n", "", 0), "음수는 괄호"
          "Some (Some 1)", ("Some (Some 1)\n", "", 0), "인자 있는 생성자 값은 괄호"
          "Some None", ("Some None\n", "", 0), "인자 없는 생성자는 괄호 없음"
          "type shape = Circle of int | Rect of int * int | Dot in [Rect (2, 3); Circle 1; Dot]", ("[Rect (2, 3); Circle 1; Dot]\n", "", 0), ""
          "Some", ("<fun>\n", "", 0), "인자 있는 생성자는 함수 값"
        ]
      "ADT 와 option: 패턴과 비교",
        [
          "type shape = Circle of int | Rect of int * int | Dot in let area s = match s with | Circle r -> 3 * r * r | Rect (w, h) -> w * h | Dot -> 0 in area (Rect (2, 3)) + area (Circle 2) + area Dot", ("18\n", "", 0), ""
          "match Some 5 with | Some x when x > 3 -> x | Some _ -> 0 | None -> -1", ("5\n", "", 0), ""
          "let f = Some in f 3", ("Some 3\n", "", 0), "생성자를 값으로 넘김"
          "Some 1 = Some 1 && Some 1 <> Some 2 && Some 1 <> None && None = None", ("true\n", "", 0), ""
          "Some (fun x -> x) = None", ("false\n", "", 0), "생성자가 다르면 인자를 보지 않음"
          "type t = A in (type t = B in B)", ("B\n", "", 0), "안쪽 선언"
          "type 'a tree = Leaf | Node of 'a tree * 'a * 'a tree in let rec insert x t = match t with | Leaf -> Node (Leaf, x, Leaf) | Node (l, v, r) -> if x < v then Node (insert x l, v, r) else Node (l, v, insert x r) in insert 2 (insert 3 (insert 1 Leaf))", ("Node (Leaf, 1, Node (Node (Leaf, 2, Leaf), 3, Leaf))\n", "", 0), "재귀 타입"
          "match Some 1 with | None -> 0", ("", "Error: Match failure: Some 1\n", 1), ""
        ]
      "내장 함수: 문자열",
        [
          "(length \"한글😀\", substr \"hello\" 1 3, indexOf \"hello\" \"l\", indexOf \"abc\" \"\")", ("(4, \"ell\", 2, 0)\n", "", 0), "UTF-16 코드 단위"
          "(upper \"abc é ß ǅ 𐐨\", lower \"ÀÉ Σ İ\")", ("(\"ABC É ß Ǆ 𐐀\", \"àé σ İ\")\n", "", 0), "Unicode 대소문자 (.NET 표). ß 와 İ 는 invariant 에서 그대로"
          "trim \"\u00A0 \\t x y \u3000\"", ("\"x y\"\n", "", 0), "Unicode 공백"
          "(contains \"hello\" \"ell\", startsWith \"hello\" \"he\", endsWith \"hello\" \"lo\", contains \"a\" \"\")", ("(true, true, true, true)\n", "", 0), ""
          "(replace \"aaa\" \"aa\" \"b\", replace \"a-b-c\" \"-\" \"\")", ("(\"ba\", \"abc\")\n", "", 0), "왼쪽부터 겹치지 않게"
          "(split \"a,b,,c\" \",\", split \"\" \",\", split \"ab\" \"\")", ("([\"a\"; \"b\"; \"\"; \"c\"], [\"\"], [\"a\"; \"b\"])\n", "", 0), "빈 조각도 남김"
          "join \", \" [\"a\"; \"b\"; \"c\"]", ("\"a, b, c\"\n", "", 0), ""
          "(toString 42, toString \"s\", toString [Some (-1)], toString (fun x -> x))", ("(\"42\", \"s\", \"[Some (-1)]\", \"<fun>\")\n", "", 0), "문자열은 따옴표 없이"
          "(toInt \"+7\", toInt \"-2147483648\", toInt \"007\")", ("(7, -2147483648, 7)\n", "", 0), ""
        ]
      "내장 함수: 리스트, 튜플, option",
        [
          "(head [1; 2], tail [1; 2], isEmpty [], nth [10; 20; 30] 2)", ("(1, [2], true, 30)\n", "", 0), ""
          "(reverse [1; 2; 3], append [1] [2; 3], range 3 5, range 5 3, sum [2147483647; 1])", ("([3; 2; 1], [1; 2; 3], [3; 4; 5], [], -2147483648)\n", "", 0), "합은 한 바퀴 돎"
          "(sort [3; 1; 2], sort [\"b\"; \"B\"; \"a\"])", ("([1; 2; 3], [\"B\"; \"a\"; \"b\"])\n", "", 0), "ordinal 순서"
          "let k = 10 in (map (fun x -> x + k) [1; 2], filter (fun x -> x > 1) [1; 2; 3], fold (fun acc x -> acc * 10 + x) 0 [1; 2; 3])", ("([11; 12], [2; 3], 123)\n", "", 0), "클로저를 런타임이 호출"
          "(exists (fun x -> 10 / x > 1) [1; 0], forall (fun x -> 10 / x > 1) [20; 0])", ("(true, false)\n", "", 0), "결과가 정해지면 멈춤 (0 으로 나누지 않음)"
          "(fst (1, \"a\"), snd (1, \"a\"), tryHead [], tryHead [5], tryFind (fun x -> x > 1) [1; 2; 3])", ("(1, \"a\", None, Some 5, Some 2)\n", "", 0), ""
          "(defaultValue 0 (tryFind (fun x -> x > 5) [1]), match tryHead [7] with | Some x -> x | None -> 0)", ("(0, 7)\n", "", 0), "런타임이 만든 Some 과 컴파일한 패턴"
        ]
      "내장 함수를 값으로",
        [
          "map length [\"a\"; \"bb\"; \"\"]", ("[1; 2; 0]\n", "", 0), ""
          "let f = substr \"hello\" in (f 1 3, f 0 2)", ("(\"ell\", \"he\")\n", "", 0), "부분 적용"
          "let length = fun x -> 42 in length [1]", ("42\n", "", 0), "let 으로 가림"
          "(head, fst (fun x -> x + 1, 0) 41)", ("(<fun>, 42)\n", "", 0), "남는 인자"
          "map (fold (fun a b -> a + b) 0) [[1; 2]; [3]]", ("[3; 3]\n", "", 0), ""
        ]
      "내장 함수 오류",
        [
          "substr \"abc\" 2 5", ("", "Error: substr: index out of range (length 3, start 2, count 5)\n", 1), ""
          "head []", ("", "Error: head: empty list\n", 1), ""
          "nth [1] 3", ("", "Error: nth: index out of range (length 1, index 3)\n", 1), ""
          "toInt \" 1\\n\"", ("", "Error: toInt: invalid integer: \" 1\\n\"\n", 1), "따옴표와 이스케이프"
          "replace \"a\" \"\" \"b\"", ("", "Error: replace: empty search string\n", 1), ""
        ]
      "unit, ;, print",
        [
          "()", ("", "", 0), "결과가 () 면 아무것도 출력하지 않음"
          "print \"a\"; println 1; println [Some \"b\"]; 7", ("a1\n[Some \"b\"]\n7\n", "", 0), "문자열은 따옴표 없이"
          "let f () = println \"hi\" in f (); f ()", ("hi\nhi\n", "", 0), ""
          "println 1; 1 / 0", ("1\n", "Error: Attempted to divide by zero.\n", 1), "오류 전의 출력은 남음 (stdout 을 먼저 비움)"
          "let x = 2\nlet _ = println x\n;; ignore (x + 1); x * 10", ("2\n20\n", "", 0), "최상위 선언"
          "(() = (), [()], Some ())", ("(true, [()], Some ())\n", "", 0), "() 값"
          "match () with () -> \"u\"", ("\"u\"\n", "", 0), ""
        ]
      "작은 문법: %, |>, >>, function, or 패턴, let rec ... and",
        [
          "(7 % 3, -7 % 3, 7 % -3, -7 % -3)", ("(1, -1, 1, -1)\n", "", 0), "부호는 왼쪽을 따름"
          "5 % 0", ("", "Error: Attempted to divide by zero.\n", 1), ""
          "(-2147483647 - 1) % -1", ("", "Error: Arithmetic operation resulted in an overflow.\n", 1), "srem 은 이 경우 정의되지 않은 동작이라 미리 검사"
          "[1; 2; 3] |> map (fun x -> x * x) |> sum", ("14\n", "", 0), ""
          "let inc x = x + 1 in let dbl x = x * 2 in ((inc >> dbl) 5, (dbl >> inc) 5)", ("(12, 11)\n", "", 0), ""
          "map (function Some x -> x | None -> 0) [Some 1; None; Some 3]", ("[1; 0; 3]\n", "", 0), ""
          "match [Some 3] with | [Some x] | [Some x; _] -> x | _ -> 0", ("3\n", "", 0), "or 패턴: 맞은 쪽에서 이름을 꺼냄"
          "match (2, 3) with | (1, y) | (y, 3) when y > 5 -> y | _ -> -1", ("-1\n", "", 0), "가드가 실패하면 다음 분기"
          "type c = Red | Green | Blue in let warm k = match k with Red | Green -> true | Blue -> false in (warm Red, warm Blue)", ("(true, false)\n", "", 0), ""
          "let k = 100 in let rec f x = if x = 0 then k else g (x - 1) and g y = f y + 1 in f 5", ("105\n", "", 0), "서로 부르는 클로저, 바깥 변수 캡처"
          "let rec even n = if n = 0 then true else odd (n - 1) and odd n = if n = 0 then false else even (n - 1) in even 1000000", ("true\n", "", 0), "깊이 100만"
          "let rec f x = g x and g x = x + 1 in f = g", ("", "Error: Type error: expected int, bool, string, list, tuple or data but got function\n", 1), ""
        ]
      "prelude (Calc 로 쓴 표준 라이브러리)",
        [
          "sum (range 1 1000000)", ("1784293664\n", "", 0), "긴 리스트: tail recursion, 합은 32비트에서 한 바퀴 돎"
          "length (filter (fun x -> x % 2 = 0) (map (fun x -> x * 3) (range 1 1000000)))", ("500000\n", "", 0), ""
          "ignore (map (fun x -> print x) [1; 2; 3]); filter (fun x -> print x; x > 1) [4; 5]", ("12345[4; 5]\n", "", 0), "f 는 앞 원소부터"
          "exists (fun x -> print x; x = 2) [1; 2; 3]", ("12true\n", "", 0), "결과가 정해지면 멈춤"
          "let reverse xs = xs in map (fun x -> x) [1; 2]", ("[1; 2]\n", "", 0), "사용자 정의는 prelude 안의 이름을 바꾸지 않음"
          "(fst (1, \"a\"), snd (1, \"a\"), tryHead [7], tryFind (fun x -> x > 5) [1], defaultValue 0 None, append [1] [2], isEmpty [])",
              ("(1, \"a\", Some 7, None, 0, [1; 2], true)\n", "", 0), ""
        ]
      "예외 (calc_try 의 _setjmp, raise 의 _longjmp)",
        [
          "failwith \"fatal\"", ("", "Error: fatal\n", 1), "Failure 는 메시지만"
          "print \"a\"; raise Not_found", ("a", "Error: exception Not_found\n", 1), "출력을 먼저 비움"
          "exception E of int * string in raise (E (1, \"x\"))", ("", "Error: exception E (1, \"x\")\n", 1), ""
          "try raise Not_found with Failure _ -> 1", ("", "Error: exception Not_found\n", 1), "맞는 분기가 없으면 다시 raise"
          "try 1 / 0 with _ -> 0", ("", "Error: Attempted to divide by zero.\n", 1), "실행 중 오류는 예외가 아님"
          "exception E in let rec f n = if n = 0 then raise E else 1 + f (n - 1) in try f 100000 with E -> -1", ("-1\n", "", 0), "깊은 재귀에서 _longjmp"
          "exception E of int in let rec go n = try (if n = 0 then raise (E 0) else go (n - 1)) with E k -> raise (E (k + 1)) in try go 1000 with E k -> k", ("1001\n", "", 0), "try 1000개가 쌓였다가 하나씩 다시 raise"
          "let f () = try raise Not_found with Not_found -> 1 in (f (), f (), try f () + raise Not_found with Not_found -> 7)", ("(1, 1, 7)\n", "", 0), "끝난 try 는 handler 목록에서 빠짐"
        ]
      "문자열 포맷",
        [
          "printf \"%d-\" 1; printfn \"%s\" \"a\"; printfn \"%A\" [1]; printf \"end\"", ("1-a\n[1]\nend", "", 0), "출력 순서"
          "let p = printfn \"%d\" in ignore (map p [1; 2])", ("1\n2\n", "", 0), "partial application"
          "$\"π={3}, s={\"x\"}, t={(true, [\"y\"])}\"", ("\"π=3, s=x, t=(true, [\\\"y\\\"])\"\n", "", 0), "결과 문자열은 따옴표와 이스케이프를 붙여 출력"
        ]
      "알려진 함수의 direct 호출",
        [
          "let f a b = a * 10 + b in (f 1 2, f 3 4)", ("(12, 34)\n", "", 0), "인자를 모두 주면 direct 호출"
          "let f a b = a * 10 + b in let g = f 1 in (g 2, map (f 2) [3])", ("(12, [23])\n", "", 0), "partial application 은 클로저"
          "let id x = x in id (fun y -> y + 1) 2", ("3\n", "", 0), "남는 인자는 결과에 적용"
          "let f a b = a + b in let f = fun x -> x * 2 in f 5", ("10\n", "", 0), "다시 묶은 이름은 보통 클로저"
          "let f a b = a + b in (fun f -> f 1 2) (fun a b -> a - b)", ("-1\n", "", 0), "인자로 가린 이름"
          "let k = 5 in let f a b = a + b + k in f 1 2", ("8\n", "", 0), "캡처한 값은 바깥 클로저에서"
          "let f a b = a - b in f (print 1; 10) (print 2; 3)", ("127\n", "", 0), "인자는 왼쪽부터 계산"
          "let f (a: int) (b: int) : int = a + b in f 1 2", ("3\n", "", 0), "인자 표기"
          "let rec f f = f + 1 in f 1", ("2\n", "", 0), "인자가 자기 이름을 가림"
          "let rec even n k = if n = 0 then k true else odd (n - 1) k and odd n k = if n = 0 then k false else even (n - 1) k in even 7 (fun b -> b)", ("false\n", "", 0), "and 그룹"
        ]
      "가변 참조와 반복문",
        [
          "let r = ref 1 in r := !r + 41; !r", ("42\n", "", 0), ""
          "ref (Some (ref (-1)))", ("ref (Some (ref (-1)))\n", "", 0), "결과 출력은 다시 입력할 수 있는 모양"
          "println (ref \"a\"); toString (ref [1])", ("ref \"a\"\n\"ref [1]\"\n", "", 0), "toString, println"
          "(ref [1] = ref [1], ref (1, 2) = ref (1, 3))", ("(true, false)\n", "", 0), "= 는 내용을 비교"
          "for i = 1 to 3 do print i done; for i = 2 downto 1 do print i done", ("12321", "", 0), ""
          "let n = ref 0 in while !n < 3 do print !n; n := !n + 1 done", ("012", "", 0), ""
          "let r = ref (fun x -> x) in r := (fun x -> x * 2); map !r [1; 2]", ("[2; 4]\n", "", 0), "내용이 함수"
          "map ref [1; 2]", ("[ref 1; ref 2]\n", "", 0), "ref 를 값으로 (클로저)"
        ]
      "indentation 문법",
        [
          "let area x =\n    let pi = 3\n    pi * x\n\nlet rec loop i acc =\n    if i = 0 then acc\n    else\n        let next = acc + i\n        loop (i - 1) next\n\nprintln (map area [ 1\n                    2 ])\nloop 1000000 0", ("[3; 6]\n1784293664\n", "", 0), "블록, 같은 열의 let, 줄바꿈 원소, tail call"
          "module M =\n    let x = 1\n    let f y = y + x\nlet n = ref 0\nwhile !n < 3 do\n    incr n\nM.f !n", ("4\n", "", 0), "모듈, done 없는 while"
        ]
      "int 빠른 경로와 런타임 경로",
        [
          "println (1 + 2, \"a\" + \"b\", 2147483647 + 1, 3 < 4, \"b\" < \"a\", -1 <= -1, 2 >= 3, 5 > -5)", ("(3, \"ab\", -2147483648, true, false, true, false, true)\n", "", 0), "+ 와 비교: int 는 생성 코드, string 은 런타임"
          "println (1 = 1, true = false, [] = [], () = (), [1] = [1], \"x\" = \"x\", Some 1 = None, (1, [2]) = (1, [2]))", ("(true, false, true, true, true, true, false, true)\n", "", 0), "= : 즉시값은 비트, 블록은 런타임"
          "let rec fib n = if n < 2 then n else fib (n - 1) + fib (n - 2) in fib 25", ("75025\n", "", 0), ""
        ]
      "할당",
        [
          "let rec build n acc = if n = 0 then acc else build (n - 1) ((n, [n], Some n) :: acc) in let xs = build 300000 [] in println (length xs, head xs, nth xs 299999)", ("(300000, (1, [1], Some 1), (300000, [300000], Some 300000))\n", "", 0), "작은 블록 여러 크기, 수거가 일어나도 값이 그대로"
          "let t = (1, 2, 3, 4, 5, 6, 7, 8, 9) in let a = Array.create 20 7 in println (t, Array.length a, a.[19], [||] = [||])", ("((1, 2, 3, 4, 5, 6, 7, 8, 9), 20, 7, true)\n", "", 0), "8 단어보다 큰 블록과 빈 배열"
        ]
      "반복문 (지금 함수 안의 scf.while)",
        [
          "let s = ref 0 in for i = 1 to 10 do s := !s + i done; for i = 3 downto 1 do print i done; for i = 5 to 4 do print \"x\" done; for i = 4 downto 5 do print \"y\" done; println !s", ("32155\n", "", 0), "for: 위로, 아래로, 빈 범위"
          "for k = 2147483646 to 2147483647 do print k done; for k = -2147483647 downto -2147483647 - 1 do print k done; println \"\"", ("21474836462147483647-2147483647-2147483648\n", "", 0), "끝값이 int 의 끝: i ± 1 을 계산하지 않고 끝남"
          "let a = ref 1 in let b = ref 2 in let f () = (a := !a + 1; !a) in for i = (print \"s\"; f ()) to (print \"e\"; f () + 1) do print i done; println (!a, !b)", ("se234(3, 2)\n", "", 0), "start, stop 은 한 번씩 왼쪽부터"
          "let fs = ref [] in for i = 1 to 3 do fs := (fun () -> i * 10) :: !fs done; println (map (fun f -> f ()) !fs)", ("[30; 20; 10]\n", "", 0), "몸체의 클로저가 반복마다의 i 를 붙잡음"
          "let s = ref 0 in for i = 1 to 300 do for j = 1 to 300 do s := (!s + i * j) % 1000 done done; println !s", ("500\n", "", 0), "겹친 for"
          "let i = ref 0 in let n = ref 0 in while (i := !i + 1; !i <= 5) do (while !n < !i * 2 do n := !n + 1 done) done; println (!i, !n)", ("(6, 10)\n", "", 0), "조건에 부수 효과, 겹친 while"
          "exception Stop of int in let r = ref 0 in (try while true do (r := !r + 1; if !r = 5 then raise (Stop !r) else ()) done with Stop k -> println k)", ("5\n", "", 0), "반복 안에서 raise"
          "let i = ref 0 in while !i < 1000000 do i := !i + 1 done; println !i", ("1000000\n", "", 0), "긴 반복 (스택이 자라지 않음)"
        ]
      "bool 배열 (원소마다 바이트 하나)",
        [
          "let a = Array.create 5 false in a.[1] <- true; a.[4] <- true; println (a, a.[0], a.[1], Array.length a, Array.toList a)", ("([|false; true; false; false; true|], false, true, 5, [false; true; false; false; true])\n", "", 0), "Array.create 로 만든 bool 배열: 읽기, 쓰기, 길이, 리스트로"
          "let a = [| true; false; true |] in let b = Array.ofList [true; false; true] in println (a = b, a = [| true; true; true |], Array.create 0 true = Array.ofList [], Array.create 0 true)", ("(true, false, true, [||])\n", "", 0), "리터럴과 ofList, = (빈 배열은 종류가 달라도 같음)"
          "let a = Array.init 6 (fun i -> i % 3 = 0) in let b = Array.map (fun x -> not x) a in println (a, b, Array.copy b, Array.fold (fun n x -> if x then n + 1 else n) 0 a)", ("([|true; false; false; true; false; false|], [|false; true; true; false; true; true|], [|false; true; true; false; true; true|], 2)\n", "", 0), "prelude 의 Array 함수"
          "let a = Array.create 3 true in let s = ref 0 in Array.iter (fun x -> if x then s := !s + 1 else ()) a; let f (x: bool array) i = x.[i] in println (!s, f a 2, [a; a])", ("(3, true, [[|true; true; true|]; [|true; true; true|]])\n", "", 0), "iter, 함수 안의 접근, 리스트 안의 배열"
          "let a = Array.create 3 true in println a.[3]", ("", "Error: Index was outside the bounds of the array.\n", 1), "bool 배열의 범위 밖 읽기"
          "let a = Array.create 3 false in a.[-1] <- true", ("", "Error: Index was outside the bounds of the array.\n", 1), "bool 배열의 음수 번호 쓰기"
          "let a = Array.create 4 (Some true) in a.[0] <- None; println (a, Array.create 2 [true], Array.create 2 (true, 1))", ("([|None; Some true; Some true; Some true|], [|[true]; [true]|], [|(true, 1); (true, 1)|])\n", "", 0), "bool 이 아닌 원소는 보통 배열"
          "let a = Array.create 300000 true in let junk = map (fun x -> (x, x)) (range 1 300000) in a.[299999] <- false; println (a.[0], a.[299999], length junk)", ("(true, false, 300000)\n", "", 0), "GC 가 훑지 않는 bool 배열이 수거 뒤에도 그대로"
        ]
      "튜플을 돌려주는 함수 (direct 함수가 struct 로 돌려줌)",
        [
          "let divmod a b = (a / b, a % b) in println (divmod 17 5, (match divmod 9 2 with (q, r) -> q * 10 + r), map (divmod 7) [1; 2; 3])", ("((3, 2), 41, [(7, 0); (3, 1); (2, 1)])\n", "", 0), "결과를 바로 match 하는 곳과 값으로 쓰는 곳"
          "let rec sumPairs xs s n = match xs with [] -> (s, n) | x :: rest -> sumPairs rest (s + x) (n + 1) in println (sumPairs (range 1 1000000) 0 0)", ("(1784293664, 1000000)\n", "", 0), "끝이 자기 재귀인 함수 (백만 번, tail call)"
          "let rec fib2 n = if n = 0 then (0, 1) else (match fib2 (n - 1) with (a, b) -> (b, (a + b) % 1000003)) in let (a, b) = fib2 30 in println (a, b, fib2 5)", ("(832040, 346266, (5, 8))\n", "", 0), "재귀 호출의 결과를 match 한 뒤 튜플, let (a, b) = …"
          "let tri x = (x, x * 2, x * 3) in let quad x = (x, x, x, x) in println ((match tri 3 with (a, b, c) -> a + b + c), (match quad 2 with (a, _, _, d) -> a + d), tri 1, quad 0)", ("(18, 4, (1, 2, 3), (0, 0, 0, 0))\n", "", 0), "원소 셋과 넷, _ 패턴"
          "let five x = (x, x, x, x, x) in println ((match five 1 with (a, _, _, _, e) -> a + e), five 2)", ("(2, (2, 2, 2, 2, 2))\n", "", 0), "원소 다섯은 struct 로 돌려주지 않음"
          "let f x = if x > 0 then (x, \"pos\") else (0 - x, \"neg\") in println (f 3, f (-4), (match f 5 with (n, s) -> s + toString n))", ("((3, \"pos\"), (4, \"neg\"), \"pos5\")\n", "", 0), "if 의 두 끝, 문자열 원소"
          "let k = 10 in let g x = (print x; (x + k, x - k)) in println (match g 1 with (a, b) -> a * b)", ("1-99\n", "", 0), "캡처한 값과 부수 효과의 순서"
          "let rec even n acc = if n = 0 then (true, acc) else odd (n - 1) (acc + 1) and odd n acc = if n = 0 then (false, acc) else even (n - 1) (acc + 1) in println (even 1000001 0, odd 4 0)", ("((false, 1000001), (false, 4))\n", "", 0), "서로 부르는 함수는 struct 로 돌려주지 않음 (백만 번, tail call)"
          "let p = (1, 2) in let id2 x = if x then p else (3, 4) in println (id2 true, id2 false)", ("((1, 2), (3, 4))\n", "", 0), "변수로 끝나는 함수는 그대로"
          "let rec f n = if n = 0 then (0, 0) else let f = fun m -> (m, m) in f n in println (f 3, f 0)", ("((3, 3), (0, 0))\n", "", 0), "자기 이름을 가린 안쪽 함수"
          "let swap p = match p with (a, b) -> (b, a) in let r = ref (1, 2) in for i = 1 to 5 do r := swap !r done; println (!r, (match swap (swap (3, 4)) with (a, b) -> a - b))", ("((2, 1), -1)\n", "", 0), "튜플 인자 펴기와 함께, ref 에 넣는 결과"
          "let pair a b = (a, b) in let apply2 h x y = h x y in println (apply2 pair 1 \"x\", fold (fun acc p -> match p with (a, b) -> acc + a * b) 0 (map (fun i -> pair i i) [1; 2; 3]), (pair 1) 2)", ("((1, \"x\"), 14, (1, 2))\n", "", 0), "값으로 넘긴 함수, fold, partial application"
          "let m x = match x with 0 -> (0, \"zero\") | n when n < 0 -> (n, \"neg\") | n -> (n, \"pos\") in println (m 0, m (-1), m 2, (match m 7 with (_, s) -> s))", ("((0, \"zero\"), (-1, \"neg\"), (2, \"pos\"), \"pos\")\n", "", 0), "가드와 분기 셋"
          "let g x = try (x / 1, x) with _ -> (0, 0) in println (match g 5 with (a, b) -> a + b)", ("10\n", "", 0), "try 로 끝나는 함수는 그대로"
          "let h x = let t = (x, x) in t in println (match h 4 with (a, b) -> a * b)", ("16\n", "", 0), "let 으로 묶은 튜플로 끝나는 함수는 그대로"
          "let rec loop i acc = if i = 0 then acc else (match acc with (a, b) -> loop (i - 1) (b, a + b)) in println (loop 10 (0, 1))", ("(55, 89)\n", "", 0), "튜플 인자를 match 한 뒤 재귀"
          "let mk n = (n, [n], Some n) in println ((match mk 2 with (a, xs, o) -> (a, xs, o)), mk 3)", ("((2, [2], Some 2), (3, [3], Some 3))\n", "", 0), "원소가 리스트와 option"
          "let rec walk n = if n = 0 then (1, 2) else walk (n - 1) in println (walk 1000000)", ("(1, 2)\n", "", 0), "끝이 자기 재귀뿐인 함수 (백만 번)"
          "let rec w4 n a = if n = 0 then (a, n, a + 1, 7) else w4 (n - 1) (a + 1) in let rec w3 n = if n = 0 then (1, 2, 3) else w3 (n - 1) in println (w4 3000000 0, w3 3000000, (match w4 10 0 with (a, b, c, d) -> a + b + c + d))", ("((3000000, 0, 3000001, 7), (1, 2, 3), 28)\n", "", 0), "원소 셋과 넷을 돌려주는 자기 재귀 (3백만 번)"
        ]
      "튜플 인자 펴기 (direct 함수가 원소를 따로 받음)",
        [
          "let rec go i p = match p with (a, b) -> if i = 0 then a else go (i - 1) (b, (a + b) % 1000003) in println (go 1000000 (0, 1))", ("3\n", "", 0), "재귀 호출에 튜플 리터럴: 튜플을 만들지 않음"
          "let dist p q = match p with (x1, y1) -> (match q with (x2, y2) -> (x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2)) in let t = (3, 4) in println (dist (0, 0) t, dist t t, map (dist (1, 1)) [(1, 2); (4, 5)])", ("(25, 0, [1; 25])\n", "", 0), "인자 둘을 차례로 match, 변수로 넘긴 튜플, partial application"
          "let f p = match p with (a, _, c) -> a + c in let g = f in println (f (1, 2, 3), g (4, 5, 6), map f [(1, 1, 1)], fold (fun acc t -> acc + f t) 0 [(1, 0, 1); (2, 0, 2)])", ("(4, 10, [2], 6)\n", "", 0), "원소 셋과 _, 값으로 쓴 함수 (map, fold)"
          "let f p = match p with (a, b) -> (print a; b) in println (f ((print \"x\"; 1), (print \"y\"; 2)), f (let t = (print \"z\"; (3, 4)) in t))", ("xy1z3(2, 4)\n", "", 0), "원소의 계산 순서, 튜플이 아닌 식을 넘김"
          "let f x p = match p with (a, b) -> (p, a + b + x) in println (f 1 (2, 3), f 10 (if true then (1, 1) else (0, 0)))", ("(((2, 3), 6), ((1, 1), 12))\n", "", 0), "본문이 튜플 인자 자체를 씀 (다시 만듦)"
          "let k = 100 in let f p = match p with (a, b) -> a + b + k in println (f (1, 2), (fun g -> g (3, 4)) f)", ("(103, 107)\n", "", 0), "캡처한 값, 고차 함수에 넘김"
          "let rec even p = match p with (n, acc) -> if n = 0 then acc else odd (n - 1, acc + 1) and odd p = match p with (n, acc) -> if n = 0 then acc else even (n - 1, acc + 2) in println (even (10, 0), odd (1000001, 0))", ("(15, 1500002)\n", "", 0), "let rec ... and ... (상호 재귀)"
          "let f p = match p with (a, b) -> a + b in let h a b = (print a; print b; f (a, b)) in println (h 1 2)", ("123\n", "", 0), "인자 둘을 튜플로 묶어 부름"
          "let f x p = match p with (x, y) -> x + y in println (f 100 (1, 2))", ("3\n", "", 0), "원소 이름이 다른 인자와 겹치면 펴지 않음"
          "let f p = match p with (a, b) when a > 0 -> a | _ -> 0 in println (f (1, 2), f (-1, 2))", ("(1, 0)\n", "", 0), "가드나 분기가 둘이면 펴지 않음"
          "let f p = match p with ((a, b), c) -> a + b + c in println (f ((1, 2), 3))", ("6\n", "", 0), "겹친 튜플 패턴은 펴지 않음"
          "let f (p: int * string) = match p with (n, s) -> s + toString n in println (f (1, \"a\"), (f) (2, \"b\"))", ("(\"a1\", \"b2\")\n", "", 0), "타입 표기한 튜플 인자"
          "let swap p = match p with (a, b) -> (b, a) in let r = ref (1, 2) in for i = 1 to 5 do r := swap !r done; println (!r, swap (swap (3, 4)))", ("((2, 1), (3, 4))\n", "", 0), "튜플을 돌려주는 함수"
          "let rec sumPairs xs acc = match acc with (s, n) -> (match xs with [] -> (s, n) | x :: rest -> sumPairs rest (s + x, n + 1)) in println (sumPairs (range 1 100000) (0, 0))", ("(705082704, 100000)\n", "", 0), "튜플 누산기 10만 번"
          "let step acc p = match p with (a, b) -> acc + a * b in println (fold step 0 [(1, 2); (3, 4)], fold step 10 [])", ("(14, 10)\n", "", 0), "편 함수를 fold 에 넘김 (튜플을 받는 함수로 한 번에)"
          "let k = 7 in let f x p = match p with (a, b) -> x * 100 + a * 10 + b + k in let g h = h 1 (2, 3) in println (g f, map (f 1) [(0, 0); (1, 1)])", ("(130, [107; 118])\n", "", 0), "인자 둘을 주는 간접 호출, 캡처, partial application"
          "let f x p = match p with (a, b) -> (print x; a + b) in let g h = h (print \"a\"; 1) (print \"b\"; (2, 3)) in println (g f)", ("ab15\n", "", 0), "간접 호출의 인자 계산 순서"
          "let rec walk n p = match p with (a, b) -> if n = 0 then a + b else walk (n - 1) (b, a) in let apply2 h x y = h x y in println (apply2 walk 1000001 (1, 2), apply2 walk 3 (5, 6))", ("(3, 11)\n", "", 0), "편 let rec 함수를 간접 호출 (tail call 이 이어짐)"
          "let f p q = match p with (a, b) -> (match q with (c, d) -> a * b + c * d) in let g h = h (1, 2) (3, 4) in println (g f, (fun h -> h (5, 5)) (f (1, 1)))", ("(14, 26)\n", "", 0), "튜플 인자 둘인 함수를 간접 호출과 partial application 으로"
          "let f x p = match p with (a, b) -> fun y -> x + a + b + y in let g h = h 1 (2, 3) 4 in println (g f, f 1 (1, 1) 1)", ("(10, 4)\n", "", 0), "인자 수보다 많이 줌: 함수를 돌려주는 편 함수"
        ]
      "문자열 연결 (string 으로 정해진 + 연쇄와 포맷을 한 번에 할당)",
        [
          "let s = \"a\" + \"b\" + \"c\" in println (s, \"x\" + (\"y\" + \"z\"), \"\" + \"\" + \"\", toString 1 + \"-\" + toString 2)", ("(\"abc\", \"xyz\", \"\", \"1-2\")\n", "", 0), "string 연쇄: 왼쪽 결합, 오른쪽 괄호, 빈 문자열, toString 조각"
          "let f a b c = a + b + c in let g a b c = a + (b + c) in println (f \"p\" \"q\" \"r\", g \"x\" \"y\" \"z\")", ("(\"pqr\", \"xyz\")\n", "", 0), "함수 인자의 연쇄 (a + b + c, a + (b + c))"
          "let g (a: string) b = a + b in println (g \"1\" \"2\", sprintf \"%d-%s-%b-%A\" 3 \"s\" true [1; 2], sprintf \"%s\" \"only\", sprintf \"plain\")", ("(\"12\", \"3-s-true-[1; 2]\", \"only\", \"plain\")\n", "", 0), "포맷: %d %s %b %A, 조각 하나, 지정자 없음"
          "println (\"1\" + \"2\" + \"3\" + \"4\" + \"5\" + \"6\" + \"7\" + \"8\" + \"9\" + \"10\" + \"11\" + \"12\" + \"13\" + \"14\" + \"15\" + \"16\" + \"17\" + \"18\")", ("123456789101112131415161718\n", "", 0), "조각 18개 (8개씩 나눠 이음)"
          "let t = (print \"a\"; \"x\") + (print \"b\"; \"y\") + ((print \"c\"; \"z\") + (print \"d\"; \"w\")) in println t", ("abcdxyzw\n", "", 0), "조각을 왼쪽부터 계산 (괄호 안의 연쇄도)"
          "println (\"😀\" + \"é\" + \"\\n\" + \"\\t\" + \"end\", length (\"ab\" + \"cd\" + \"e\"), \"é\" + \"😀\" = \"é😀\")", ("(\"😀é\\n\\tend\", 5, true)\n", "", 0), "code unit 이 BMP 밖인 글자와 이스케이프"
          "let total = ref 0 in for i = 1 to 200000 do total := !total + length (sprintf \"%d:%d\" i (i * 2) + \"!\" + toString i) done; println !total", ("3722240\n", "", 0), "반복 안의 sprintf 와 연쇄 20만 번"
          "printfn \"%d and %s\" 5 \"x\"; printf \"%A|\" (Some \"q\"); println \"\"", ("5 and x\nSome \"q\"|\n", "", 0), "printfn, printf"
          "exception E of string in println (try raise (E (\"a\" + \"b\" + \"c\")) with E s -> s + \"!\" + s)", ("abc!abc\n", "", 0), "예외 인자와 handler 의 연쇄"
          "let names = map (fun i -> \"n\" + toString i + \";\") (range 1 5) in println (names, fold (fun acc s -> acc + s) \"\" names)", ("([\"n1;\"; \"n2;\"; \"n3;\"; \"n4;\"; \"n5;\"], \"n1;n2;n3;n4;n5;\")\n", "", 0), "map 의 함수와 fold 로 모으기"
        ]
      "런타임의 빠른 경로 (int 의 toString 과 출력, int sort, prelude 의 Array 반복문)",
        [
          "println (toString 0, toString (-2147483647 - 1), toString 2147483647, toString (-5), toString 10, sprintf \"%d|%d\" (-12) 340, $\"{-7}\")", ("(\"0\", \"-2147483648\", \"2147483647\", \"-5\", \"10\", \"-12|340\", \"-7\")\n", "", 0), "toString 과 sprintf 의 int: 0, 음수, Int32 의 끝"
          "print (-2147483647 - 1); print \" \"; print 2147483647; print \" \"; print 0; println (-9)", ("-2147483648 2147483647 0-9\n", "", 0), "print 의 int"
          "println (toString [1; -2], toString (Some (-3)), toString (1, true), toString \"s\", toString ())", ("(\"[1; -2]\", \"Some (-3)\", \"(1, true)\", \"s\", \"()\")\n", "", 0), "int 가 아닌 값의 toString 은 그대로"
          "println (sort [], sort [5], sort [3; -1; 3; 0; -2147483647 - 1; 2147483647; -1], sort [2; 1; 3; 0; 4], sort [\"b\"; \"a\"; \"c\"; \"a\"])", ("([], [5], [-2147483648; -1; -1; 0; 3; 3; 2147483647], [0; 1; 2; 3; 4], [\"a\"; \"a\"; \"b\"; \"c\"])\n", "", 0), "sort: 빈 리스트, 같은 값, 음수, 문자열"
          "let rec ok xs = match xs with [] -> true | a :: rest -> (match rest with [] -> true | b :: _ -> a <= b && ok rest) in let xs = sort (map (fun i -> (i * 7919) % 10007 - 5000) (range 1 100001)) in println (ok xs, length xs, head xs, nth xs 100000)", ("(true, 100001, -5000, 5006)\n", "", 0), "int 10만 개 sort (merge 단계마다 홀수 길이)"
          "println (Array.init 0 (fun i -> i), Array.init (-3) (fun i -> i), Array.map (fun x -> x + 1) (Array.ofList []), Array.copy (Array.ofList []), Array.fold (fun a x -> a + x) 7 (Array.ofList []))", ("([||], [||], [||], [||], 7)\n", "", 0), "prelude 의 Array 함수: 빈 배열과 음수 길이"
          "let a = Array.init 4 (fun i -> print i; i * i) in let b = Array.map (fun x -> print x; x + 1) a in println (a, b, Array.fold (fun acc x -> acc + toString x) \"\" b)", ("01230149([|0; 1; 4; 9|], [|1; 2; 5; 10|], \"12510\")\n", "", 0), "Array.init, map 은 f 를 앞 원소부터 부름"
          "let a = [| 1; 2; 3 |] in let c = Array.copy a in c.[0] <- 9; println (a, c, Array.init 3 (fun i -> i = 1), Array.map (fun x -> x > 1) a = [| false; true; true |])", ("([|1; 2; 3|], [|9; 2; 3|], [|false; true; false|], true)\n", "", 0), "copy 는 따로, init 과 map 의 bool 배열"
          "println (try Array.init 3 (fun i -> if i = 1 then failwith \"init\" else i) with Failure m -> (print m; [| |]))", ("init[||]\n", "", 0), "Array.init 의 f 에서 raise"
          "let a = Array.init 300000 (fun i -> (i, [i])) in let junk = map (fun x -> Some x) (range 1 300000) in println (Array.fold (fun n p -> n + fst p % 7) 0 (Array.map (fun p -> p) a), a.[299999], length junk)", ("(899997, (299999, [299999]), 300000)\n", "", 0), "Array.init 30만 개: 수거 뒤에도 그대로"
        ]
      "생성자 값 (이름 칸 없는 블록, 인자 없는 생성자는 즉시값)",
        [
          "type t = A | B of int | C of int * int in println (A = B 1, B 1 = A, B 1 = B 1, A = A, B 1 = B 2, C (1, 2) = C (1, 2), C (1, 2) = A)", ("(false, false, true, true, false, true, false)\n", "", 0), "= : 즉시값과 블록, 헤더 비교"
          "let f = Some in println (f (1, 2), (match f (1, 2) with Some (a, b) -> a + b | None -> 0), (match Some (3, 4) with Some p -> fst p | None -> 0))", ("(Some (1, 2), 3, 3)\n", "", 0), "실행 중에 편 튜플 인자 (calc_make_data, calc_data_payload)"
          "type t = A | B of int in println (Some None, [None; Some (Some 1)], Some (B 1), B (-1), [A; B 2], (A, Some A))", ("(Some None, [None; Some (Some 1)], Some (B 1), B (-1), [A; B 2], (A, Some A))\n", "", 0), "출력: 즉시값 생성자와 괄호"
          "type u = X | Y of string in type v = P | Q of u in println (match Q (Y \"s\") with Q (Y s) -> s | Q X -> \"x\" | P -> \"p\", [P; Q X; Q (Y \"a\")])", ("(\"s\", [P; Q X; Q (Y \"a\")])\n", "", 0), "겹친 생성자 패턴"
          "exception E in exception F of int * string in println ((try raise E with E -> 1), (try raise (F (2, \"b\")) with F (n, s) -> n), (try raise Not_found with E -> 0 | Not_found -> 3))", ("(1, 2, 3)\n", "", 0), "예외: 인자 없음, 튜플 인자, Not_found"
          "type tree = Leaf | Node of tree * int * tree in let rec build n t = if n = 0 then t else build (n - 1) (Node (t, n, Leaf)) in let rec sum t acc = match t with Leaf -> acc | Node (l, v, _) -> sum l (acc + v % 10) in let t = build 300000 Leaf in let junk = map (fun x -> Some (x, x)) (range 1 300000) in println (sum t 0, length junk)", ("(1350000, 300000)\n", "", 0), "수거가 여러 번 일어나도 32바이트 칸이 살아 있음"
          "exception F of int * string in raise (F (1, \"x\"))", ("", "Error: exception F (1, \"x\")\n", 1), "받지 않은 예외의 출력"
          "raise Not_found", ("", "Error: exception Not_found\n", 1), "받지 않은 인자 없는 예외"
          "failwith \"boom\"", ("", "Error: boom\n", 1), "받지 않은 Failure 는 메시지만"
        ]
      "인자가 여럿인 간접 호출 (클로저의 direct 함수)",
        [
          "let g h = h (print 1; 1) (print 2; 2) in println (g (fun a b -> a * 10 + b))", ("1212\n", "", 0), "인자 둘을 주는 간접 호출: 인자를 왼쪽부터"
          "let f = fun a -> (print a; fun b -> a + b) in let g h = h 1 (print 2; 3) in println (g f)", ("124\n", "", 0), "인자 하나짜리 함수: f a 를 먼저 적용한 뒤 다음 인자"
          "let g h = h 1 2 3 in println (g (fun a b -> let k = a + b in fun c -> k * c))", ("9\n", "", 0), "인자 수보다 많이 줌: 하나씩 적용"
          "let g h = h 1 in println ((g (fun a b -> a + b)) 5)", ("6\n", "", 0), "인자 수보다 적게 줌: 부분 적용"
          "let k = 100 in let g h = h 1 2 in println (g (fun a b -> a + b + k))", ("103\n", "", 0), "캡처한 값"
          "let add a b c = a * 100 + b * 10 + c in let g h = h 1 2 3 in println (g add)", ("123\n", "", 0), "이름에 묶은 함수를 값으로"
          "let rec sumTo acc x = if x = 0 then acc else sumTo (acc + 1) (x - 1) in println (fold sumTo 0 [1; 2; 3])", ("6\n", "", 0), "let rec 함수를 fold 에"
          "let apply2 f a b = f a b in let rec count n acc = if n = 0 then acc else apply2 count (n - 1) (acc + 1) in println (count 1000000 0)", ("1000000\n", "", 0), "tail position 의 간접 호출 (스택이 자라지 않음)"
          "let apply2 f a b = f a b in println (apply2 (fun a -> let t = a * 2 in fun b -> t + b) 3 4, apply2 append [1] [2])", ("(10, [1; 2])\n", "", 0), "인자 하나짜리 함수와 내장 함수를 tail position 에서"
          "let pick b = if b then (fun x y -> x) else (fun x y -> y) in println (pick true 1 2, pick false 1 2, map (fun f -> f 7 8) [pick true; pick false])", ("(1, 2, [7; 8])\n", "", 0), "고른 함수를 부름"
        ]
      "타입이 int 인 연산",
        [
          "println (2147483647 + 1, -2147483647 - 1 - 1, 5 - 7, 100 + -3, 0 - 0, 1073741824 + 1073741824, -1073741824 - 1073741825)", ("(-2147483648, 2147483647, -2, 97, 0, -2147483648, 2147483647)\n", "", 0), "태그를 떼지 않는 +, -: 32비트에서 한 바퀴"
          "let f a b = (a + b, a - b) in println (f 2147483647 2147483647, f (-2147483647 - 1) 1)", ("((-2, 0), (-2147483647, 2147483647))\n", "", 0), "변수끼리"
          "let rec fib n = if n < 2 then n else fib (n - 1) + fib (n - 2) in println (fib 20, 2147483647 + 1, -3 < 2, 5 >= 5, 4 <> 4)", ("(6765, -2147483648, true, true, false)\n", "", 0), "int 로 정해진 연산"
          "let eq x y = x = y in let lt x y = x < y in let lts x y = x < y in println (eq 1 1, eq \"a\" \"b\", eq [1] [1], lt 1 2, lts \"b\" \"a\")", ("(true, false, true, true, false)\n", "", 0), "다형 함수 안의 = 와, 쓰인 곳에서 int 또는 string 으로 정해지는 <"
          "let max a b = if a > b then a else b in println (max 3 7, max 10 (-1))", ("(7, 10)\n", "", 0), "함수 안의 int 비교"
          "let join3 a b c = a + b + c in println (join3 \"x\" \"y\" \"z\", \"a\" < \"b\", \"b\" <= \"a\", [1; 2] = [1; 2], (1, \"a\") = (1, \"a\"), Some 1 <> None)", ("(\"xyz\", true, false, true, true, true)\n", "", 0), "string 과 블록은 런타임"
        ]
      "리스트 칸 (헤더 없는 cons, 주소 | 2)",
        [
          "println ([[]], [[1]; []], [] = [], [1] = [], [1; 2] = [1; 2], [[1]] = [[1]], Some [], ([1], [2]))", ("([[]], [[1]; []], true, false, true, true, Some [], ([1], [2]))\n", "", 0), "빈 리스트, 중첩, =, 다른 값 안의 리스트"
          "let r = ref [1] in r := 2 :: !r; println (!r, Array.ofList [3; 4], Array.toList [| [5] |])", ("([2; 1], [|3; 4|], [[5]])\n", "", 0), "ref, 배열과 리스트"
          "println (sort [3; 1; 2], sort [\"b\"; \"a\"], split \"a,b\" \",\", join \"-\" [\"x\"; \"y\"], length [1; 2; 3])", ("([1; 2; 3], [\"a\"; \"b\"], [\"a\"; \"b\"], \"x-y\", 3)\n", "", 0), "sort, split, join, length (C 런타임의 리스트 함수)"
          "exception E of int list in try raise (E [1; 2]) with E xs -> println xs", ("[1; 2]\n", "", 0), "예외의 리스트 인자"
          "let f = fun xs -> fun x -> x :: xs in let g = f [9] in println (g 1, g 2)", ("([1; 9], [2; 9])\n", "", 0), "클로저가 붙잡은 리스트"
          "let rec build n acc = if n = 0 then acc else build (n - 1) ([n] :: acc) in let xs = build 300000 [] in println (length xs, head xs, nth xs 299999, length (map (fun l -> 0 :: l) xs))", ("(300000, [1], [300000], 300000)\n", "", 0), "리스트 30만 개 안의 리스트: 수거가 여러 번 일어나도 그대로"
          "let xs = range 1 500000 in let ys = map (fun x -> (x, [x])) xs in println (length ys, nth ys 499999)", ("(500000, (500000, [500000]))\n", "", 0), "튜플 안의 리스트 50만 개"
          "type t = N of int list * t | E in let rec mk n = if n = 0 then E else N ([n; n], mk (n - 1)) in println (mk 3)", ("N ([3; 3], N ([2; 2], N ([1; 1], E)))\n", "", 0), "생성자 인자 안의 리스트"
          "match [1; 2; 3] with a :: b :: rest -> println (a, b, rest) | _ -> ()", ("(1, 2, [3])\n", "", 0), "x :: y :: rest 패턴"
          "println (match ([1], [2]) with (x :: _, y :: _) -> x + y | _ -> 0)", ("3\n", "", 0), "튜플 match 안의 cons 패턴"
          "let rec loop n acc = if n = 0 then length acc else loop (n - 1) (n :: (if n % 1000 = 0 then [] else acc)) in println (loop 2000000 [])", ("1000\n", "", 0), "오래 사는 칸과 바로 버리는 칸이 섞임"
        ]
      "튜플 match 와 rev_append",
        [
          "println (match ((print 1; [1]), (print 2; [])) with ([], _) -> 0 | (_, []) -> 2 | (x :: _, y :: _) -> x + y)", ("122\n", "", 0), "원소는 왼쪽부터 계산한 뒤 맞춤"
          "let x = 10 in println ((match (1, 2) with (x, 3) -> x | (a, b) when a > b -> 0 | _ -> x) + x)", ("20\n", "", 0), "_ 분기, 가드, 실패한 분기의 x 는 바깥을 가리지 않음"
          "match (1, 2) with (0, _) -> 0 | (_, 0) -> 1", ("", "Error: Match failure: (1, 2)\n", 1), "맞는 분기가 없을 때만 튜플을 만듦"
          "println (match (Some 1, [2; 3]) with (Some a, b :: _) -> a + b | _ -> 0)", ("3\n", "", 0), ""
          "println (match (1, (2, 3)) with (a, (b, c)) -> a + b + c)", ("6\n", "", 0), "안쪽 튜플은 블록"
          "println (match (1, \"a\", true) with (1, s, false) -> s | (_, s, true) -> s + \"!\" | _ -> \"\")", ("a!\n", "", 0), "원소 셋"
          "let rec merge xs ys acc = match (xs, ys) with ([], _) -> append (reverse acc) ys | (_, []) -> append (reverse acc) xs | (x :: xr, y :: yr) -> if x <= y then merge xr ys (x :: acc) else merge xs yr (y :: acc) in println (merge [1; 4; 6] [2; 3; 9] [])", ("[1; 2; 3; 4; 6; 9]\n", "", 0), "꼬리 위치의 튜플 match 와 append (reverse ...)"
          "println (append (reverse [1; 2; 3]) [4], append (reverse []) [1], let f = append in f (reverse [1]) [2])", ("([3; 2; 1; 4], [1], [1; 2])\n", "", 0), "append (reverse xs) ys 는 rev_append 하나"
          "let r = ref 0 in println (append (reverse (r := 1; [1])) (r := !r + 10; [!r]))", ("[1; 11]\n", "", 0), "rev_append 도 인자를 왼쪽부터"
        ]
      "생성자 값 (튜플 인자를 편 블록)",
        [
          "type t = A | B of int * string | C of int in println [A; B (1, \"x\"); C 2]", ("[A; B (1, \"x\"); C 2]\n", "", 0), "출력"
          "type t = B of int * int in let f = B in println (map f [(1, 2); (3, 4)])", ("[B (1, 2); B (3, 4)]\n", "", 0), "생성자를 함수 값으로 (실행 중에 폄)"
          "type t = B of int * int in match B (1, 2) with B p -> println p", ("(1, 2)\n", "", 0), "인자 전체를 묶으면 튜플을 다시 만듦"
          "type t = B of int * int in let mk p = B p in println (mk (5, 6), mk (5, 6) = B (5, 6), B (1, 2) = B (1, 3))", ("(B (5, 6), true, false)\n", "", 0), "튜플 변수를 인자로, ="
          "println (Some (1, 2), Some (Some (1, [2])), (match Some (1, 2) with Some (a, b) -> a + b | None -> 0))", ("(Some (1, 2), Some (Some (1, [2])), 3)\n", "", 0), "option 의 튜플 인자"
          "println (match Some (3, 4) with Some ((1, x) | (3, x)) -> x | _ -> 0)", ("4\n", "", 0), "or 패턴"
          "exception E of int * string in try raise (E (1, \"a\")) with E (n, s) -> println (n, s)", ("(1, \"a\")\n", "", 0), "예외의 튜플 인자"
          "exception E of int * string in raise (E (2, \"b\"))", ("", "Error: exception E (2, \"b\")\n", 1), "잡히지 않은 예외의 출력"
          "type 'a tree = Leaf | Node of 'a tree * 'a * 'a tree in let rec ins x t = match t with Leaf -> Node (Leaf, x, Leaf) | Node (l, v, r) -> if x < v then Node (ins x l, v, r) else Node (l, v, ins x r) in println (fold (fun t x -> ins x t) Leaf [3; 1; 2])", ("Node (Node (Leaf, 1, Node (Leaf, 2, Leaf)), 3, Leaf)\n", "", 0), "다형 트리"
          "type t = P of (int * int) in match P (1, 2) with P (a, b) -> println (a * 10 + b)", ("12\n", "", 0), "of (int * int)"
          "type t = W of int list * (int * int) in match W ([1], (2, 3)) with W (xs, (a, b)) -> println (xs, a, b)", ("([1], 2, 3)\n", "", 0), "안쪽 튜플은 그대로 블록"
          "println (Some 1, Some (-1), [Some (1, 2); None])", ("(Some 1, Some (-1), [Some (1, 2); None])\n", "", 0), "인자 하나와 펴진 블록이 섞임"
          "type t = B of int * int in let g = fun p -> match p with B (x, _) -> x in println (g (B (7, 8)))", ("7\n", "", 0), "_ 원소"
        ]
      "리스트 내장 함수",
        [
          "println (reverse [1; 2; 3], append [1] [2; 3], append [] [4], range 3 5, range 5 3)", ("([3; 2; 1], [1; 2; 3], [4], [3; 4; 5], [])\n", "", 0), "reverse, append, range"
          "println (range 2147483646 2147483647); println (length (range (-2147483647 - 1) (-2147483647)))", ("[2147483646; 2147483647]\n2\n", "", 0), "int 의 끝에서도 멈춤"
          "let f = append [1] in println (f [2], map (append [0]) [[1]], (fun r -> r [1; 2]) reverse)", ("([1; 2], [[0; 1]], [2; 1])\n", "", 0), "partial application, 값으로"
          "let xs = range 1 300000 in println (length (append (reverse xs) xs))", ("600000\n", "", 0), "긴 리스트"
        ]
      "배열",
        [
          "let a = [| 3; 1; 2 |] in a.[0] <- 10; println a; println (Array.length a); [| [|\"x\"|] |]", ("[|10; 1; 2|]\n3\n[|[|\"x\"|]|]\n", "", 0), "출력"
          "let a = [| 1; 2 |] in a.[2]", ("", "Error: Index was outside the bounds of the array.\n", 1), "범위 밖 (인터프리터와 같은 문장)"
          "let a = [| 1 |] in a.[-1] <- 0", ("", "Error: Index was outside the bounds of the array.\n", 1), "음수"
          "Array.create (-1) 0", ("", "Error: Array.create: the length must be non-negative (got -1)\n", 1), ""
          "let a = Array.create 100000 1 in let s = ref 0 in for i = 0 to 99999 do s := !s + a.[i] done; !s", ("100000\n", "", 0), "큰 배열"
        ]
      "모듈",
        [
          "module M = struct type t = A | B of int let f x = match x with A -> 0 | B n -> n end;; (M.f (M.B 3), M.A, [M.B 1])", ("(3, M.A, [M.B 1])\n", "", 0), "생성자 이름은 M.A"
          "module M = struct exception E of string end;; raise (M.E \"x\")", ("", "Error: exception M.E \"x\"\n", 1), "잡히지 않은 예외"
          "module M = struct let rec even n = if n = 0 then true else odd (n - 1) and odd n = if n = 0 then false else even (n - 1) end;; M.even 1000000", ("true\n", "", 0), "and 그룹, tail call"
        ]
      "레코드",
        [
          "type p = { x: int; y: int } in { y = 2; x = 1 }", ("{ x = 1; y = 2 }\n", "", 0), "선언한 순서로 출력"
          "type p = { name: string; tags: string list } in let a = { name = \"a\"; tags = [\"t\"] } in println a; toString { a with name = \"b\" }", ("{ name = \"a\"; tags = [\"t\"] }\n\"{ name = \\\"b\\\"; tags = [\\\"t\\\"] }\"\n", "", 0), "println, toString"
          "type p = { x: int } in Some { x = -1 }", ("Some { x = -1 }\n", "", 0), "생성자 인자의 레코드는 괄호 없음"
          "type p = { x: int; y: int } in let f t v = print t; v in { y = f 1 1; x = f 2 2 }", ("12{ x = 2; y = 1 }\n", "", 0), "값은 쓴 순서대로 계산"
          "type p = { x: int; y: int } in let a = { x = 1; y = 2 } in let f t v = print t; v in { (print 0; a) with y = f 1 3 }", ("01{ x = 1; y = 3 }\n", "", 0), "with: 앞의 식 먼저"
          "type p = { x: int; y: int } in ({ x = 1; y = 2 } = { y = 2; x = 1 }, { x = 1; y = 2 } = { x = 1; y = 3 })", ("(true, false)\n", "", 0), "= 는 필드를 비교"
          "type p = { x: int } in printfn \"%A|%s\" { x = 1 } \"s\"", ("{ x = 1 }|s\n", "", 0), "%A"
          "type p = { x: int; y: int } in match { y = 2; x = 1 } with { y = 3 } -> 0 | { x = a; y = b } -> a * 10 + b", ("12\n", "", 0), "레코드 패턴: 필드는 선언한 순서의 칸"
          "type p = { x: int; y: bool } in match { x = 1; y = false } with { y = true } -> 1", ("", "Error: Match failure: { x = 1; y = false }\n", 1), "레코드 패턴의 Match failure"
        ]
      "Match failure",
        [
          "match [1] with | [] -> 0", ("", "Error: Match failure: [1]\n", 1), ""
          "let (a, 1) = (5, 2) in a", ("", "Error: Match failure: (5, 2)\n", 1), "let 패턴"
        ]
      "구조적 = (인터프리터처럼 길이를 먼저)",
        [
          "[1; 2] = [1; 2] && [1; 2] <> [1] && (1, \"x\") = (1, \"x\") && [[1]; []] = [[1]; []]", ("true\n", "", 0), ""
          "[fun x -> x] = []", ("false\n", "", 0), "길이가 다르면 원소를 보지 않음"
          "[fun x -> x] = [fun x -> x]", ("", "Error: Type error: expected int, bool, string, list, tuple or data but got function\n", 1), ""
        ]
      "단락 평가: 오른쪽은 계산하지 않음",
        [
          "false && 1 / 0 = 0", ("false\n", "", 0), ""
          "true || 1 / 0 = 0", ("true\n", "", 0), ""
          "if true then 1 else 1 / 0", ("1\n", "", 0), ""
          "true && 1 / 0 = 0", ("", "Error: Attempted to divide by zero.\n", 1), ""
        ]
    ]

/// 차등 테스트의 입력: 평가 예제와 실행 중 오류 예제 중 컴파일할 수 있는 것
let private differentialInputs =
    lazy ((EvalTests.cases |> List.map fst) @ runtimeErrorCases |> List.filter compilable)

/// 실행 테스트와 차등 테스트의 모든 입력을 실행 파일 하나로 빌드하고 각각 실행한 결과 (표준 출력, 표준 에러, 종료 코드).
/// 입력마다 실행 파일을 만들면 느림: 외부 도구 3번, 새 실행 파일의 첫 실행(macOS 에서 약 0.3초), .NET 의 프로세스 생성.
/// 그래서 입력을 코어 수만큼의 모듈로 나눠 병렬로 컴파일하고, 실행은 실행 파일 안의 드라이버가 입력마다 fork 해서 함
let private batch =
    lazy
        (let t = requireTools ()
         let inputs =
             (runCases |> List.collect (snd >> List.map (fun (input, _, _) -> input)))
             @ differentialInputs.Value
             @ (examples |> List.map snd)
             |> List.distinct
         let programs =
             inputs
             |> List.mapi (fun i input ->
                 let ast = Program.load input
                 Infer.typecheck ast |> ignore
                 sprintf "calc_entry_%d" i, ast)
         let chunkSize = max 1 ((programs.Length + Environment.ProcessorCount - 1) / Environment.ProcessorCount)
         let modules = programs |> List.chunkBySize chunkSize |> List.map Codegen.emitEntries
         let work = Path.Combine(Path.GetTempPath(), "calc-test-batch-" + Guid.NewGuid().ToString("N"))
         Directory.CreateDirectory work |> ignore
         try
             let exe = Path.Combine(work, "batch")
             Toolchain.buildMany t modules (List.map fst programs) exe
             let _, stderr, code = runProcess exe [ "--all"; work ]
             if code <> 0 then failwithf "batch driver failed (exit %d): %s" code stderr
             let read i ext = File.ReadAllText(Path.Combine(work, sprintf "%d.%s" i ext))
             inputs |> List.mapi (fun i input -> input, (read i "out", read i "err", int (read i "code"))) |> Map.ofList
         finally
             Directory.Delete(work, true))

/// 묶음 빌드의 결과. 묶음에 없는 입력이면 따로 컴파일
let private batchRun (input: string) =
    match batch.Value.TryFind input with
    | Some result -> result
    | None -> compileAndRun input

[<Tests>]
let tests =
    testList "Compile" [
        testList "생성한 MLIR" [
            test "리터럴: 태그를 붙인 i64 상수" {
                let mlir = Codegen.emitProgram (Program.parse "42")
                Expect.stringContains mlir "arith.constant 337 : i64" "42 = (42 << 3) | 1"
                Expect.stringContains mlir "llvm.func @calc_main() -> i64" ""
                Expect.stringContains mlir "llvm.func @main() -> i32" ""
            }
            test "값 표현: int 와 bool 의 태그" {
                Expect.equal (Codegen.tagInt 0) 1L ""
                Expect.equal (Codegen.tagInt 5) 41L ""
                Expect.equal (Codegen.tagInt -1) -7L "음수도 산술 시프트로 복원됨"
                Expect.equal (Codegen.tagBool false) 3L ""
                Expect.equal (Codegen.tagBool true) 11L ""
            }
            test "Some 의 이름은 런타임의 전역, None 은 즉시값 6 (생성자 번호의 기준)" {
                let mlir = Codegen.emitProgram (Program.parse "match Some None with | Some None -> 1 | _ -> 0")
                Expect.stringContains mlir "llvm.mlir.addressof @calc_name_some" ""
                Expect.stringContains mlir "arith.constant 6 : i64" "None"
                Expect.isFalse (mlir.Contains "@ctor_") "이 모듈의 생성자 이름 전역은 없음"
            }
            test "인자 없는 생성자는 즉시값 (이름 전역의 번호 << 32 | 6), 이름은 C 문자열 전역" {
                let mlir = Codegen.emitProgram (Program.parse "type t = Red | Blue in [Red; Red]")
                Expect.stringContains mlir "llvm.mlir.global internal constant @ctor_0(\"Red\\00\")" ""
                Expect.isFalse (mlir.Contains "@ctor_1") "같은 생성자는 하나"
                Expect.isFalse (mlir.Contains "!llvm.struct<(i64, ptr)>") "전역 블록이 없음"
                Expect.stringContains mlir "llvm.mlir.addressof @calc_name_none" "번호의 기준"
            }
            test "생성자 헤더: 종류 5, 인자 단어 수 (이름 칸이 없음)" {
                Expect.equal (Codegen.dataHeader 1) 0x105L "인자 하나"
                Expect.equal (Codegen.dataHeader 3) 0x305L "튜플 인자 원소 셋"
                Expect.equal Codegen.ctorTag 6L ""
            }
            test "클로저는 calc_alloc 으로 할당하고 간접 호출" {
                let mlir = Codegen.emitProgram (Program.parse "let k = 10 in (fun f -> f 32) (fun x -> x + k)")
                Expect.stringContains mlir "llvm.func internal tailcc @lambda_0(%closure: !llvm.ptr, %arg: i64) -> i64" ""
                Expect.stringContains mlir "llvm.call @calc_alloc(" ""
                Expect.stringContains mlir "llvm.mlir.addressof @lambda_0" ""
                Expect.stringContains mlir ": !llvm.ptr, (!llvm.ptr, i64) -> i64" "간접 호출"
            }
            test "이름에 묶은 함수는 인자를 모두 받는 함수를 직접 호출" {
                let mlir = Codegen.emitProgram (Program.parse "let add a b = a + b in add 1 2")
                Expect.stringContains mlir "llvm.func internal tailcc @lambda_0_direct(%closure: !llvm.ptr, %arg0: i64, %arg1: i64) -> i64" ""
                Expect.stringContains mlir "= llvm.call tailcc @lambda_0_direct(" "직접 호출"
                let program = mlir.Substring(mlir.IndexOf "llvm.func internal") // 선언부의 calc_apply 는 빼고
                Expect.isFalse (program.Contains "!llvm.ptr, (!llvm.ptr, i64) -> i64") "간접 호출 없음"
            }
            test "문자열 상수는 8바이트 정렬 전역, 같은 내용은 하나" {
                let mlir = Codegen.emitProgram (Program.parse "\"hi\" + \"hi\" + \"\"")
                Expect.stringContains
                    mlir
                    "llvm.mlir.global internal constant @str_0() {addr_space = 0 : i32, alignment = 8 : i64} : !llvm.struct<(i64, i64, array<2 x i16>)>"
                    ""
                Expect.stringContains mlir "dense<[104, 105]> : tensor<2xi16>" "UTF-16 코드 단위"
                Expect.stringContains mlir "@str_1() {addr_space = 0 : i32, alignment = 8 : i64} : !llvm.struct<(i64, i64)>" "빈 문자열"
                Expect.isFalse (mlir.Contains "@str_2") "\"hi\" 는 한 번만"
            }
            test "문자열 헤더: 종류 2, 길이 칸 + 코드 단위를 담는 단어" {
                Expect.equal (Codegen.stringHeader 0) 0x102L ""
                Expect.equal (Codegen.stringHeader 4) 0x202L "8바이트 = 1단어"
                Expect.equal (Codegen.stringHeader 5) 0x302L "10바이트 = 2단어"
            }
            test "cons 태그와 튜플 헤더" {
                Expect.equal Codegen.consTag 2L "cons 는 헤더 없는 [head, tail] 블록의 주소 | 2"
                Expect.equal (Codegen.tupleHeader 3) 0x304L "종류 4, 원소 3개"
                Expect.equal Codegen.nilValue 5L "[] 는 즉시값"
            }
            // 타입 추론이 피연산자를 int 로 정한 연산은 태그 검사와 런타임 호출 없이 (Infer.intOperations)
            test "타입이 int 인 연산은 런타임을 부르지 않음" {
                let typed (input: string) =
                    let ast = Program.load input
                    Infer.typecheck ast |> ignore
                    Codegen.emitProgram ast
                let fib = typed "let rec fib n = if n < 2 then n else fib (n - 1) + fib (n - 2) in fib 10"
                for f in [ "calc_compare"; "calc_add"; "calc_eq" ] do
                    Expect.isFalse (fib.Contains("llvm.call @" + f + "(")) f
                let mixed = typed "let g (s: string) = s + \"!\" in (1 + 2, 1 = 2, g \"a\", \"a\" < \"b\", true = false)"
                Expect.isTrue (mixed.Contains "llvm.call @calc_concat2(") "string 의 + 는 런타임의 연결"
                Expect.isTrue (mixed.Contains "llvm.call @calc_compare(") "string 의 < 는 런타임"
                Expect.isTrue (mixed.Contains "llvm.call @calc_eq(") "bool 의 = 는 즉시값 경로와 런타임"
                let poly = typed "let eq x y = x = y in (eq 1 1, eq \"a\" \"b\")"
                Expect.isTrue (poly.Contains "llvm.call @calc_eq(") "다형 함수 안의 = 는 태그를 보고 고름"
                // 타입 검사를 하지 않은 식은 지금처럼 태그를 봄
                Expect.isTrue ((Codegen.emitProgram (Program.parse "let f x y = x < y in f 1 2")).Contains "llvm.call @calc_compare(") ""
            }
            // 타입 추론이 string 으로 정한 + 연쇄와 포맷이 펼친 식은 조각을 모아 calc_concatN 한 번 (Infer.stringAdds)
            test "문자열 연쇄는 한 번에 연결" {
                let typed (input: string) =
                    let ast = Program.load input
                    Infer.typecheck ast |> ignore
                    Codegen.emitProgram ast
                let count (text: string) (pattern: string) = text.Split(pattern).Length - 1
                let chain = typed "let a = toString 1 in let b = toString 2 in a + b + \"!\" + (b + a)"
                Expect.equal (count chain "llvm.call @calc_concat5(") 1 "a + b + \"!\" + (b + a) 는 조각 다섯 개"
                Expect.isFalse (chain.Contains "llvm.call @calc_add(") "중간 문자열 없음"
                let format = typed "sprintf \"%d-%s:%b\" 1 \"s\" true"
                Expect.isTrue (format.Contains "llvm.call @calc_concat5(") "포맷의 조각 다섯 개 (포맷의 함수는 클로저와 direct 함수 두 곳)"
                Expect.isFalse (format.Contains "llvm.call @calc_add(") "포맷도 중간 문자열 없음"
                let long = typed (String.concat " + " [ for i in 1..10 -> sprintf "\"%d\"" i ])
                Expect.equal (count long "llvm.call @calc_concat8(") 1 "8 개가 넘으면 앞 8 개를 먼저"
                Expect.equal (count long "llvm.call @calc_concat3(") 1 "그 결과와 남은 두 개"
                Expect.isTrue ((Codegen.emitProgram (Program.parse "let f x y = x + y in f \"a\" \"b\"")).Contains "llvm.call @calc_add(") "타입 검사를 하지 않은 식은 태그를 봄"
            }
            // 본문이 인자를 바로 match (a, b) 하는 함수: direct 함수는 원소를 따로 받고, 튜플 리터럴을 넘기는 호출은 블록을 만들지 않음
            test "튜플 인자를 편 direct 함수" {
                let mlir = Codegen.emitProgram (Program.parse "let rec go i p = match p with (a, b) -> if i = 0 then a else go (i - 1) (b, a + b) in go 10 (0, 1)")
                let header = "llvm.func internal tailcc @lambda_0_direct(%closure: !llvm.ptr, %arg0: i64, %arg1: i64, %arg2: i64) -> i64 {"
                Expect.isTrue (mlir.Contains header) "i, a, b 를 따로 받음"
                let start = mlir.IndexOf header
                let body = mlir.Substring(start, mlir.IndexOf("\n}", start) - start)
                Expect.isFalse (body.Contains "@calc_alloc(") "재귀 호출이 튜플을 만들지 않음"
                Expect.isTrue (body.Contains "call tailcc tail @lambda_0_direct(") "재귀 호출은 그대로 tail call"
                let used = Codegen.emitProgram (Program.parse "let f p = match p with (a, b) -> a + b in let g = f in g (1, 2)")
                Expect.isTrue (used.Contains "@lambda_0_direct(%closure: !llvm.ptr, %arg0: i64, %arg1: i64)") "인자 하나짜리 함수도 direct 함수를 따로 만듦"
                // 인자가 여럿이면 튜플을 그대로 받는 함수도 만들어 클로저가 알림 (모르는 함수로 인자를 모두 주는 호출)
                let passed = Codegen.emitProgram (Program.parse "let step acc p = match p with (a, b) -> acc + a * b in let g h = h 1 (2, 3) in g step")
                Expect.isTrue (passed.Contains "@lambda_0_tupled(%closure: !llvm.ptr, %arg0: i64, %arg1: i64)") "튜플을 받는 함수"
                Expect.isTrue (passed.Contains "llvm.mlir.addressof @lambda_0_tupled") "클로저의 마지막 칸"
                Expect.isFalse (used.Contains "_tupled") "인자 하나짜리 함수는 만들지 않음"
            }
            // 끝이 모두 튜플 리터럴(또는 자기 재귀)인 함수: direct 함수가 struct 로 돌려주고, 결과를 바로 match 하는 곳은 블록을 만들지 않음
            test "튜플을 돌려주는 direct 함수는 struct" {
                let mlir = Codegen.emitProgram (Program.parse "let divmod a b = (a / b, a % b) in let r = (match divmod 7 2 with (q, m) -> q + m) in (r, divmod 9 4)")
                Expect.isTrue (mlir.Contains "@lambda_0_direct(%closure: !llvm.ptr, %arg0: i64, %arg1: i64) -> !llvm.struct<(i64, i64)>") "direct 함수의 결과"
                Expect.isTrue (mlir.Contains "llvm.extractvalue") "match 하는 곳은 원소를 꺼냄"
                let start = mlir.IndexOf "@lambda_0_direct(%closure"
                let body = mlir.Substring(start, mlir.IndexOf("\n}", start) - start)
                Expect.isFalse (body.Contains "@calc_alloc(") "direct 함수는 튜플 블록을 만들지 않음"
                Expect.isTrue (mlir.Contains "@lambda_0_tupled(%closure: !llvm.ptr, %arg0: i64, %arg1: i64) -> i64") "간접 호출은 튜플을 돌려주는 함수"
                let notStruct (input: string) = Expect.isFalse ((Codegen.emitProgram (Program.parse input)).Contains "!llvm.struct") input
                notStruct "let f x = (x, x, x, x, x) in f 1"
                notStruct "let rec even n = if n = 0 then (true, 0) else odd (n - 1) and odd n = if n = 0 then (false, 0) else even (n - 1) in even 3"
                notStruct "let p = (1, 2) in let f x = if x then p else (3, 4) in f true"
            }
            // 세대 구분 GC: 이미 있는 객체에 쓰는 세 곳(:=, 배열 원소, let rec … and … 의 클로저 칸)에 write barrier, cons 는 따로 할당
            test "write barrier 와 cons 할당" {
                let count (text: string) (pattern: string) = text.Split(pattern).Length - 1
                let mlir = Codegen.emitProgram (Program.parse "let r = ref [] in let a = Array.create 2 [] in r := [1]; a.[0] <- [2]; let rec f x = g x and g x = f x in (!r, a.[0])")
                Expect.equal (count mlir "llvm.call @calc_write_barrier(") 4 ":=, 배열 쓰기, 클로저 칸 둘"
                Expect.stringContains mlir "arith.andi %v" "포인터(태그 0, 2)일 때만"
                Expect.stringContains mlir "llvm.call @calc_alloc_cons() : () -> !llvm.ptr" "cons 는 cons 만 담는 블록"
                Expect.stringContains mlir "@calc_write_barrier(!llvm.ptr, i64) attributes {memory_effects" "GC 의 기록만 바꿈"
                let ints = Codegen.emitProgram (Program.parse "let r = ref 0 in r := 1; !r")
                Expect.equal (count ints "llvm.call @calc_write_barrier(") 1 "int 를 쓰는 곳도 barrier 를 내지만 실행 중 태그 검사로 건너뜀 (시간 차이가 없어 그대로 둠)"
            }
            test "클로저 칸, ref, 배열 접근에는 서로 다른 TBAA 태그, 오류 함수는 noreturn" {
                let mlir = Codegen.emitProgram (Program.parse "let a = Array.create 3 0 in let r = ref 0 in let f i = a.[i] <- !r; r := i in f 1")
                for tag in [ "#calc_tbaa_captured"; "#calc_tbaa_ref"; "#calc_tbaa_array" ] do
                    Expect.isTrue (mlir.Contains("{tbaa = [" + tag + "]}")) tag
                for f in [ "calc_error_index"; "calc_error_divide_by_zero"; "calc_error_overflow"; "calc_match_failure" ] do
                    Expect.stringContains mlir (sprintf "llvm.func @%s(" f) f
                    Expect.isTrue (mlir.Split('\n') |> Array.exists (fun l -> l.Contains("@" + f + "(") && l.Contains "noreturn")) f
            }
            // 파서가 while/for 를 바꾼 let rec 을 Lower 가 알아보고 지금 함수 안의 반복(scf.while)으로 (클로저를 만들지 않음)
            test "while 과 for 는 지금 함수 안의 scf.while" {
                let mlir = Codegen.emitProgram (Program.parse "let s = ref 0 in for i = 1 to 3 do (let j = ref 0 in while !j < i do j := !j + 1 done; s := !s + !j) done; !s")
                Expect.equal (mlir.Split([| "scf.while" |], System.StringSplitOptions.None).Length - 1) 2 "for 하나, while 하나"
                Expect.isFalse (mlir.Contains "llvm.func internal tailcc @lambda_") "반복 몸체가 최상위 함수가 되지 않음"
            }
            // 모르는 함수에 인자 둘: 헤더의 인자 수를 보고 클로저 마지막 칸의 direct 함수를 바로 부름
            test "인자가 여럿인 간접 호출은 direct 함수를 부르는 경로가 있음" {
                let mlir = Codegen.emitProgram (Program.parse "let g h = h 1 2 in g (fun a b -> a - b)")
                Expect.stringContains mlir (sprintf "%d : i64" (Codegen.directClosureHeader 0 2)) "fun a b 의 클로저 헤더에 인자 수"
                Expect.stringContains mlir "llvm.mlir.addressof @lambda_1_direct" "클로저 마지막 칸에 direct 함수"
                Expect.stringContains mlir ": !llvm.ptr, (!llvm.ptr, i64, i64) -> i64" "인자 둘로 간접 호출"
            }
            // 배열 접근은 헤더의 종류를 보고 바이트(bool 배열) 또는 단어로 읽고 씀. 헤더 읽기는 따로 TBAA 태그
            test "배열 접근은 bool 배열의 바이트 경로가 있음" {
                let mlir = Codegen.emitProgram (Program.parse "let a = Array.create 3 true in a.[1] <- false; a.[1]")
                Expect.stringContains mlir (sprintf "arith.constant %d : i64" Codegen.kindBoolArray) "종류 비교"
                Expect.stringContains mlir "{tbaa = [#calc_tbaa_array]} : !llvm.ptr -> i8" "바이트 읽기"
                Expect.stringContains mlir "{tbaa = [#calc_tbaa_array]} : i8, !llvm.ptr" "바이트 쓰기"
                Expect.stringContains mlir "{tbaa = [#calc_tbaa_header]} : !llvm.ptr -> i64" "헤더 읽기"
            }
            test "어떤 값과도 일치하는 분기 뒤는 생성하지 않음" {
                // + 는 int 인지 보는 scf.if 를 만들므로 - 로 확인
                let mlir = Codegen.emitProgram (Program.parse "let (a, b) = (1, 2) in a - b")
                Expect.isFalse (mlir.Contains "calc_match_failure(%") "튜플 분해는 실패할 수 없음"
                Expect.isFalse (mlir.Contains "scf.if") "검사 코드 없음"
            }
            test "클로저 헤더: 종류 1, 뒤따르는 단어 수" {
                Expect.equal (Codegen.closureHeader 0) 0x101L "코드 포인터만"
                Expect.equal (Codegen.closureHeader 2) 0x301L "코드 포인터 + 캡처 2개"
            }
            test "인자가 다 모인 내장 함수는 런타임 함수를 바로 호출" {
                let mlir = Codegen.emitProgram (Program.parse "substr \"hello\" 1 3")
                Expect.stringContains mlir "llvm.func @calc_builtin_substr(i64, i64, i64) -> i64" "선언"
                Expect.stringContains mlir "= llvm.call @calc_builtin_substr(" "호출"
                Expect.isFalse (mlir.Contains "@lambda_") "클로저 없음"
            }
            test "let 은 새 코드 없이 SSA 이름을 재사용" {
                let mlir = Codegen.emitProgram (Program.parse "let x = 5 in x")
                Expect.stringContains mlir "%v1 = arith.constant 41 : i64" ""
                Expect.stringContains mlir "llvm.return %v1 : i64" "x 는 %v1 그 자체"
            }
            test "if 는 scf.if, + 는 런타임 호출" {
                let mlir = Codegen.emitProgram (Program.parse "if true then 1 + 2 else 0")
                Expect.stringContains mlir "scf.if" ""
                Expect.stringContains mlir "llvm.call @calc_add" ""
            }
            test "나눗셈은 0과 MinValue / -1 을 검사" {
                let mlir = Codegen.emitProgram (Program.parse "7 / 2")
                Expect.stringContains mlir "llvm.call @calc_error_divide_by_zero()" ""
                Expect.stringContains mlir "llvm.call @calc_error_overflow()" ""
                Expect.stringContains mlir "arith.divsi" ""
            }
        ]

        testList "tail call" [
            let functionsOf (mlir: string) =
                mlir.Split("llvm.func internal tailcc @")
                |> Array.skip 1
                |> Array.map (fun f -> f.Substring(0, f.IndexOf "\n}"))
            let lines (text: string) = text.Split('\n') |> Array.map (fun l -> l.Trim())
            // tail 호출 바로 다음 줄은 그 결과를 돌려주는 llvm.return (tailcc 의 tail call 조건)
            let tailReturns (mlir: string) =
                let ls = lines mlir
                [ for i in 0 .. ls.Length - 2 do
                      if ls[i].Contains "llvm.call tailcc tail " then
                          let result = ls[i].Substring(0, ls[i].IndexOf " =")
                          yield ls[i + 1] = sprintf "llvm.return %s : i64" result ]
            test "tail position 의 호출은 tail 호출 다음 llvm.return" {
                let mlir = Codegen.emitProgram (Program.parse "let rec go n acc = if n = 0 then acc else go (n - 1) (acc + 1) in go 10 0")
                // 본문을 함께 쓰는 두 함수(acc 를 받는 클로저, 인자를 모두 받는 direct 함수)에 하나씩. 진입 함수의 go 10 0 은 보통 호출
                Expect.equal (tailReturns mlir) [ true; true ] ""
                Expect.stringContains mlir "llvm.call tailcc tail @lambda_0_direct(" "재귀 호출도 direct"
            }
            test "tail position 의 if 는 블록과 llvm.cond_br, 안쪽 if 는 scf.if" {
                let mlir = Codegen.emitProgram (Program.parse "fun x -> if x = 1 then (if x = 2 then 3 else 4) + 1 else 5")
                let body = (functionsOf mlir)[0]
                Expect.stringContains body "llvm.cond_br" "tail position"
                Expect.stringContains body "scf.if" "+ 의 피연산자는 tail position 이 아님"
                Expect.equal (lines body |> Array.filter (fun l -> l.StartsWith "llvm.return") |> Array.length) 2 "분기마다 return"
            }
            test "tail position 이 아닌 호출과 진입 함수의 호출은 보통 호출" {
                Expect.equal (tailReturns (Codegen.emitProgram (Program.parse "fun f -> 1 + f 2"))) [] "+ 의 피연산자"
                Expect.equal (tailReturns (Codegen.emitProgram (Program.parse "(fun x -> x) 1"))) [] "진입 함수 (signature 가 다름)"
            }
            test "tail position 의 match: 분기마다 블록, 마지막은 Match failure 뒤 unreachable" {
                let mlir = Codegen.emitProgram (Program.parse "fun f -> fun n -> match n with 0 -> f 0 | 1 -> f 1")
                // 안쪽 함수와 direct 함수가 같은 본문을 가짐
                Expect.equal (tailReturns mlir) [ true; true; true; true ] ""
                Expect.stringContains mlir "llvm.unreachable" ""
            }
        ]

        testList "클로저 변환" [
            let lowered input = Lower.lowerProgram (Program.parse input)
            test "자유 변수를 캡처" {
                let p = lowered "let k = 1 in let j = 2 in fun x -> x + k + j"
                Expect.equal p.Functions.Length 1 ""
                let f = p.Functions.Head
                Expect.equal (f.Name, f.Params, f.Captured, f.Self) ("lambda_0", [ "x" ], [ "j"; "k" ], None) "정렬된 순서"
                Expect.equal p.Main (Core.CLet ("k", Core.CInt 1, Core.CLet ("j", Core.CInt 2, Core.CMakeClosure ("lambda_0", [ "j"; "k" ])))) ""
            }
            test "인자와 안쪽 let 은 캡처하지 않음" {
                let f = (lowered "fun x -> let y = x in y").Functions.Head
                Expect.isEmpty f.Captured ""
            }
            test "let rec 의 자기 자신은 캡처하지 않음" {
                let f = (lowered "let rec fact n = if n = 0 then 1 else n * fact (n - 1) in fact 5").Functions.Head
                Expect.equal (f.Self, f.Captured) (Some "fact", []) ""
            }
            test "커링: 안쪽 함수가 바깥 인자를 캡처" {
                let p = lowered "fun a -> fun b -> a + b"
                // 바깥 클로저 이름을 먼저 정하고 안쪽 함수를 만듦. 인자가 둘이라 direct 함수도 (f a b 가 중간 클로저 없이 부름)
                Expect.equal
                    (p.Functions |> List.map (fun f -> f.Name, f.Params, f.Captured, f.Direct))
                    [ "lambda_1", [ "b" ], [ "a" ], None
                      "lambda_0", [ "a" ], [], Some ("lambda_0_direct", 2)
                      "lambda_0_direct", [ "a"; "b" ], [], None ]
                    ""
            }
            test "이름에 묶은 함수: 클로저들과 인자를 모두 받는 direct 함수, 호출은 CCallDirect" {
                let p = lowered "let add a b = a + b in add 1 2"
                Expect.equal
                    p.Main
                    (Core.CLet ("add", Core.CMakeClosure ("lambda_0", []), Core.CCallDirect ("lambda_0_direct", Core.CVar "add", [ Core.CInt 1; Core.CInt 2 ])))
                    "바깥 클로저 이름은 본문보다 먼저 정함"
                Expect.equal
                    (p.Functions |> List.map (fun f -> f.Name, f.Params, f.Captured))
                    [ "lambda_1", [ "b" ], [ "a" ]; "lambda_0", [ "a" ], []; "lambda_0_direct", [ "a"; "b" ], [] ]
                    "direct 함수의 캡처는 바깥 클로저와 같음"
            }
            test "인자가 모자라면 클로저 적용, 남으면 결과에 적용, 하나면 바깥 함수가 direct" {
                Expect.equal
                    (lowered "let add a b = a + b in add 1").Main
                    (Core.CLet ("add", Core.CMakeClosure ("lambda_0", []), Core.CApply (Core.CVar "add", Core.CInt 1)))
                    ""
                Expect.equal
                    (lowered "let id x = x in id id 1").Main
                    (Core.CLet ("id", Core.CMakeClosure ("lambda_0", []), Core.CApply (Core.CCallDirect ("lambda_0", Core.CVar "id", [ Core.CVar "id" ]), Core.CInt 1)))
                    ""
            }
            test "다시 묶은 이름은 알려진 함수가 아님" {
                let p = lowered "let f a b = a in fun f -> f 1 2"
                let inner = p.Functions |> List.find (fun f -> f.Params = [ "f" ])
                Expect.equal inner.Body (Core.CApply (Core.CApply (Core.CVar "f", Core.CInt 1), Core.CInt 2)) "인자 f"
                Expect.equal
                    (lowered "let f a b = a in let f = 1 in f").Main
                    (Core.CLet ("f", Core.CMakeClosure ("lambda_0", []), Core.CLet ("f", Core.CInt 1, Core.CVar "f")))
                    ""
            }
            test "let rec: 재귀 호출도 direct, 인자 표기는 건너뜀" {
                let p = lowered "let rec go (n: int) acc = if n = 0 then acc else go (n - 1) acc in go 3 0"
                let direct = p.Functions |> List.find (fun f -> f.Name = "lambda_0_direct")
                Expect.equal (direct.Params, direct.Self, direct.Captured) ([ "n"; "acc" ], Some "go", []) ""
                Expect.equal
                    direct.Body
                    (Core.CIf (
                        Core.CPrim (Core.PEqual, [ Core.CVar "n"; Core.CInt 0 ]),
                        Core.CVar "acc",
                        Core.CCallDirect ("lambda_0_direct", Core.CVar "go", [ Core.CPrim (Core.PSubtract, [ Core.CVar "n"; Core.CInt 1 ]); Core.CVar "acc" ])
                    ))
                    ""
            }
            test "생성자: 바로 적용하면 블록, 값으로 쓰면 함수" {
                Expect.equal (lowered "Some 1").Main (Core.CData ("Some", Some (Core.CInt 1))) ""
                Expect.equal (lowered "None").Main (Core.CData ("None", None)) ""
                let p = lowered "Some"
                Expect.equal p.Main (Core.CMakeClosure ("lambda_0", [])) "fun x -> Some x"
                Expect.equal p.Functions.Head.Body (Core.CData ("Some", Some (Core.CVar "x"))) ""
            }
            test "type 선언이 생성자 범위를 정함" {
                Expect.equal
                    (lowered "type t = A | B of int in B 2").Main
                    (Core.CData ("B", Some (Core.CInt 2)))
                    "선언 자체는 코드가 없음"
            }
            test "내장 함수: 인자가 다 모이면 호출, 남는 인자는 결과에 적용" {
                Expect.equal (lowered "length \"ab\"").Main (Core.CBuiltin ("length", [ Core.CString "ab" ])) ""
                Expect.equal
                    (lowered "raise Not_found 2").Main
                    (Core.CApply (Core.CBuiltin ("raise", [ Core.CData ("Not_found", None) ]), Core.CInt 2))
                    ""
            }
            test "내장 함수를 값으로: 인자를 하나씩 받는 클로저" {
                let p = lowered "substr"
                Expect.equal p.Main (Core.CMakeClosure ("lambda_2", [])) "바깥 함수"
                Expect.equal
                    (p.Functions |> List.map (fun f -> f.Name, f.Params, f.Captured))
                    [ "lambda_0", [ "$b2" ], [ "$b0"; "$b1" ]; "lambda_1", [ "$b1" ], [ "$b0" ]; "lambda_2", [ "$b0" ], [] ]
                    "안쪽 함수가 바깥 인자를 캡처"
            }
            test "let 으로 가린 내장 함수는 보통 변수" {
                Expect.equal (lowered "let length = 5 in length").Main (Core.CLet ("length", Core.CInt 5, Core.CVar "length")) ""
                Expect.equal (lowered "fun length -> length 1").Functions.Head.Body (Core.CApply (Core.CVar "length", Core.CInt 1)) "인자"
            }
            test "&& 와 || 는 if 로" {
                Expect.equal (lowered "true && false").Main (Core.CIf (Core.CBool true, Core.CBool false, Core.CBool false)) ""
                Expect.equal (lowered "true || false").Main (Core.CIf (Core.CBool true, Core.CBool true, Core.CBool false)) ""
            }
        ]

        testList "패턴 변환" [
            test "match 의 자유 변수에서 패턴이 묶는 이름을 뺌" {
                let f = (Lower.lowerProgram (Program.parse "let k = 1 in fun xs -> match xs with | x :: rest when x > k -> x | _ -> k")).Functions.Head
                Expect.equal f.Captured [ "k" ] "x, rest 는 캡처하지 않음"
            }
            test "let 패턴은 분기 하나짜리 match" {
                Expect.equal
                    (Lower.lowerProgram (Program.parse "let (a, b) = (1, 2) in a")).Main
                    (Core.CMatch (Core.CTuple [ Core.CInt 1; Core.CInt 2 ], [ Ast.PTuple [ Ast.PVar "a"; Ast.PVar "b" ], None, Core.CVar "a" ]))
                    ""
            }
        ]

        // 위 실행 테스트는 묶음 실행 파일(Toolchain.buildMany)을 씀. calc --compile 과 같은 경로(@main 이 있는 단독 실행 파일)도 확인
        testList "단독 실행 파일" [
            test "결과 출력" {
                Expect.equal (compileAndRun "let xs = [\"a\"; \"b\"] in (xs, fun x -> x)") ("([\"a\"; \"b\"], <fun>)\n", "", 0) ""
            }
            test "실행 중 오류" {
                Expect.equal (compileAndRun "1 / 0") ("", "Error: Attempted to divide by zero.\n", 1) ""
            }
            test "재귀 깊이 100만 (링크 옵션의 512MB 스택)" {
                Expect.equal (compileAndRun "let rec f n = if n = 0 then 0 else 1 + f (n - 1) in f 1000000") ("1000000\n", "", 0) ""
            }
            test "tail call 은 -O0 에서도 스택을 쓰지 않음 (tailcc): 깊이 1000만, if, match, 가드, and, ;, let, 함수 인자, 인자 수가 다른 함수" {
                let input =
                    String.concat "\n"
                        [ "let rec a n = if n = 0 then 0 else a (n - 1)"
                          "let rec b n = match n with 0 -> 0 | x when x > 0 -> b (x - 1) | _ -> 0"
                          "let rec even n = if n = 0 then true else odd (n - 1) and odd n = if n = 0 then false else even (n - 1)"
                          "let rec c n acc = if n = 0 then acc else (ignore n; let m = n - 1 in c m (acc + 1))"
                          "let rec loop g n = if n = 0 then g 0 else loop g (n - 1)"
                          // 인자 수가 다른 direct 함수와 클로저 사이 (tailcc 라서 signature 가 달라도 tail call)
                          "let rec one n = if n = 0 then 0 else two n 1 and two n d = if n = 0 then 0 else three (n - d) d 0 and three n d z = one (n + z)"
                          "let rec viaClosure n k = if n = 0 then k 0 else (fun m -> viaClosure m k) (n - 1)"
                          // while 과 for 는 tail recursion 으로 바뀜
                          "let loops n = let s = ref 0 in for i = 1 to n do s := !s + 1 done; while !s > 0 do decr s done; !s"
                          ";;"
                          "(a 10000000, b 10000000, even 10000000, c 10000000 0, loop (fun x -> x + 1) 10000000, one 10000000, viaClosure 10000000 (fun x -> x), loops 10000000)" ]
                // -O2 는 tail 표시가 없어도 대부분 tail call 로 바꾸므로, 최적화하지 않는 -O0 으로 보장을 확인
                Expect.equal
                    (compileAndRunWith (fun t -> { t with OptimizationLevel = "-O0" }) input)
                    ("(0, 0, true, 10000000, 1, 0, 0, 0)\n", "", 0)
                    ""
            }
        ]

        test "Unicode 표 (runtime/unicode.h) 가 지금 .NET 과 같음" {
            // 다르면: dotnet fsi src/Calc/runtime/gen-unicode.fsx
            let header = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "runtime", "unicode.h"))
            let table (name: string) =
                let start = header.IndexOf(name)
                let body = header.Substring(start, header.IndexOf("};", start) - start)
                Text.RegularExpressions.Regex.Matches(body, @"\{0x([0-9A-F]+), 0x([0-9A-F]+)\}")
                |> Seq.map (fun m -> Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16))
                |> Map.ofSeq
            let upper, lower = table "calc_upper_map", table "calc_lower_map"
            for cp in 0 .. 0x10FFFF do
                if cp < 0xD800 || cp > 0xDFFF then
                    let s = Char.ConvertFromUtf32 cp
                    let expect (map: Map<int, int>) (converted: string) =
                        let mapped = map |> Map.tryFind cp |> Option.defaultValue cp
                        if Char.ConvertFromUtf32 mapped <> converted then failtestf "U+%04X: table %04X, .NET %A" cp mapped converted
                    expect upper (s.ToUpperInvariant())
                    expect lower (s.ToLowerInvariant())
            let whitespace = [ for c in 0 .. 0xFFFF do if Char.IsWhiteSpace(char c) then sprintf "0x%04X," c ]
            Expect.stringContains header (String.Join(" ", whitespace)) "Char.IsWhiteSpace"
        }

        // 세대 구분 GC: 기본 설정, nursery 를 작게(256KB) 해서 수거를 자주, 그리고 할당 100번마다 minor GC (stress). 기대값은 인터프리터(calc)의 출력
        testList "세대 구분 GC" [
            for input, expected, name in gcCases ->
                test name {
                    requireTools () |> ignore
                    Expect.equal (compileAndRunGc [] input) expected "기본 (nursery 1MB 에서 16MB 까지 커짐)"
                    Expect.equal (compileAndRunGc [ "CALC_GC_NURSERY_KB", "256" ] input) expected "nursery 256KB"
                    Expect.equal (compileAndRunGc [ "CALC_GC_STRESS", "100" ] input) expected "할당 100번마다 minor GC"
                }
        ]

        testList "실행" [
            for name, cases in runCases ->
                test name {
                    requireTools () |> ignore
                    for input, expected, message in cases do
                        Expect.equal (batchRun input) expected (if message = "" then input else message)
                }
        ]

        test "예제 프로그램 (examples/*.calc) 은 인터프리터와 같은 출력" {
            requireTools () |> ignore
            Expect.isNonEmpty examples "examples 폴더"
            for name, source in examples do
                Expect.equal (batchRun source) (interpret source) name
        }

        test "차등: 컴파일할 수 있는 평가 예제는 인터프리터와 같은 결과" {
            requireTools () |> ignore
            let inputs = differentialInputs.Value
            for input in inputs do
                Expect.equal (batchRun input) (interpret input) input
            // 단계 7: 타입 검사를 통과하는 평가 예제 전부 + 실행 중 오류 예제
            Expect.isGreaterThanOrEqual inputs.Length 286 "컴파일한 예제 수"
        }
    ]
