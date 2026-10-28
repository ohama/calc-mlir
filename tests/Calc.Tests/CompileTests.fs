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
let compileAndRun (input: string) =
    let t = requireTools ()
    let ast = Program.parse input
    Infer.typecheck ast |> ignore
    let mlir = Codegen.emitProgram ast
    let exe = Path.Combine(Path.GetTempPath(), "calc-test-" + Guid.NewGuid().ToString("N"))
    try
        Toolchain.build t mlir exe false |> ignore
        runProcess exe []
    finally
        File.Delete exe

/// 컴파일러가 지원하고 타입 검사를 통과하는 식인지
let private compilable (input: string) =
    try
        let ast = Program.parse input
        Infer.typecheck ast |> ignore
        Codegen.emitProgram ast |> ignore
        true
    with
    | Core.NotSupported _
    | Infer.TypeError _ -> false

/// 인터프리터로 실행했을 때 calc 가 내는 것과 같은 (표준 출력, 표준 에러, 종료 코드)
let private interpret (input: string) =
    try
        Eval.formatValue (Eval.eval (Program.parse input)) + "\n", "", 0
    with ex ->
        "", "Error: " + ex.Message + "\n", 1

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
             (runCases |> List.collect (snd >> List.map (fun (input, _, _) -> input))) @ differentialInputs.Value
             |> List.distinct
         let programs =
             inputs
             |> List.mapi (fun i input ->
                 let ast = Program.parse input
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
            test "Some 과 None 은 런타임의 전역 (내장 함수가 만든 option 과 패턴이 맞도록)" {
                let mlir = Codegen.emitProgram (Program.parse "match Some None with | Some None -> 1 | _ -> 0")
                Expect.stringContains mlir "llvm.mlir.addressof @calc_name_some" ""
                Expect.stringContains mlir "llvm.mlir.addressof @calc_none" ""
                Expect.isFalse (mlir.Contains "@ctor_") "이 모듈의 생성자 이름 전역은 없음"
            }
            test "인자 없는 생성자는 전역 상수 블록, 이름은 C 문자열 전역" {
                let mlir = Codegen.emitProgram (Program.parse "type t = Red | Blue in [Red; Red]")
                Expect.stringContains mlir "llvm.mlir.global internal constant @ctor_0(\"Red\\00\")" ""
                Expect.stringContains mlir "@unit_0() {addr_space = 0 : i32, alignment = 8 : i64} : !llvm.struct<(i64, ptr)>" ""
                Expect.isFalse (mlir.Contains "@unit_1") "같은 생성자는 하나"
            }
            test "생성자 헤더: 종류 5, 이름과 인자" {
                Expect.equal (Codegen.dataHeader false) 0x105L "이름만"
                Expect.equal (Codegen.dataHeader true) 0x205L "이름 + 인자"
            }
            test "클로저는 calc_alloc 으로 할당하고 간접 호출" {
                let mlir = Codegen.emitProgram (Program.parse "let k = 10 in let add = fun x -> x + k in add 32")
                Expect.stringContains mlir "llvm.func internal @lambda_0(%closure: !llvm.ptr, %arg: i64) -> i64" ""
                Expect.stringContains mlir "llvm.call @calc_alloc(" ""
                Expect.stringContains mlir "llvm.mlir.addressof @lambda_0" ""
                Expect.stringContains mlir ": !llvm.ptr, (!llvm.ptr, i64) -> i64" "간접 호출"
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
            test "cons 와 튜플 헤더" {
                Expect.equal Codegen.consHeader 0x203L "종류 3, head 와 tail"
                Expect.equal (Codegen.tupleHeader 3) 0x304L "종류 4, 원소 3개"
                Expect.equal Codegen.nilValue 5L "[] 는 즉시값"
            }
            test "어떤 값과도 일치하는 분기 뒤는 생성하지 않음" {
                let mlir = Codegen.emitProgram (Program.parse "let (a, b) = (1, 2) in a + b")
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

        testList "클로저 변환" [
            let lowered input = Lower.lowerProgram (Program.parse input)
            test "자유 변수를 캡처" {
                let p = lowered "let k = 1 in let j = 2 in fun x -> x + k + j"
                Expect.equal p.Functions.Length 1 ""
                let f = p.Functions.Head
                Expect.equal (f.Name, f.Param, f.Captured, f.Self) ("lambda_0", "x", [ "j"; "k" ], None) "정렬된 순서"
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
                // 안쪽 함수가 먼저 만들어짐
                Expect.equal (p.Functions |> List.map (fun f -> f.Name, f.Param, f.Captured)) [ "lambda_0", "b", [ "a" ]; "lambda_1", "a", [] ] ""
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
                    (lowered "fst (fun x -> x, 1) 2").Main
                    (Core.CApply (Core.CBuiltin ("fst", [ Core.CTuple [ Core.CMakeClosure ("lambda_0", []); Core.CInt 1 ] ]), Core.CInt 2))
                    ""
            }
            test "내장 함수를 값으로: 인자를 하나씩 받는 클로저" {
                let p = lowered "substr"
                Expect.equal p.Main (Core.CMakeClosure ("lambda_2", [])) "바깥 함수"
                Expect.equal
                    (p.Functions |> List.map (fun f -> f.Name, f.Param, f.Captured))
                    [ "lambda_0", "$b2", [ "$b0"; "$b1" ]; "lambda_1", "$b1", [ "$b0" ]; "lambda_2", "$b0", [] ]
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

        testList "실행" [
            for name, cases in runCases ->
                test name {
                    requireTools () |> ignore
                    for input, expected, message in cases do
                        Expect.equal (batchRun input) expected (if message = "" then input else message)
                }
        ]

        test "차등: 컴파일할 수 있는 평가 예제는 인터프리터와 같은 결과" {
            requireTools () |> ignore
            let inputs = differentialInputs.Value
            for input in inputs do
                Expect.equal (batchRun input) (interpret input) input
            // 단계 7: 타입 검사를 통과하는 평가 예제 전부 + 실행 중 오류 예제
            Expect.isGreaterThanOrEqual inputs.Length 286 "컴파일한 예제 수"
        }
    ]
