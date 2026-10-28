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
    | Codegen.NotSupported _
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
      "1 + 2 / 0" ]

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
            test "지원하지 않는 기능은 NotSupported" {
                Expect.throwsC
                    (fun () -> Codegen.emitProgram (Program.parse "let x = 1 in x") |> ignore)
                    (fun ex -> Expect.equal ex.Message "Not supported by the compiler yet: Let" "")
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

        testList "실행" [
            test "42" {
                Expect.equal (compileAndRun "42") ("42\n", "", 0) ""
            }
            test "int 범위 끝" {
                Expect.equal (compileAndRun "2147483647") ("2147483647\n", "", 0) ""
                Expect.equal (compileAndRun "0") ("0\n", "", 0) ""
            }
            test "bool" {
                Expect.equal (compileAndRun "true") ("true\n", "", 0) ""
                Expect.equal (compileAndRun "false") ("false\n", "", 0) ""
            }
            test "산술" {
                Expect.equal (compileAndRun "1 + 2 * 3") ("7\n", "", 0) ""
                Expect.equal (compileAndRun "(1 + 2) * 3 - 10") ("-1\n", "", 0) ""
                Expect.equal (compileAndRun "-(4 - 6)") ("2\n", "", 0) ""
            }
            test "나눗셈은 0 쪽으로 버림" {
                Expect.equal (compileAndRun "7 / -2") ("-3\n", "", 0) ""
                Expect.equal (compileAndRun "-7 / 2") ("-3\n", "", 0) ""
            }
            test "32비트에서 한 바퀴 돎 (인터프리터와 같음)" {
                Expect.equal (compileAndRun "2147483647 + 1") ("-2147483648\n", "", 0) "+ (런타임)"
                Expect.equal (compileAndRun "-2147483647 - 2") ("2147483647\n", "", 0) "-"
                Expect.equal (compileAndRun "65536 * 65536") ("0\n", "", 0) "*"
                Expect.equal (compileAndRun "-(-2147483647 - 1)") ("-2147483648\n", "", 0) "단항 -"
            }
            test "비교와 논리" {
                Expect.equal (compileAndRun "1 < 2 && 2 <= 2 && 3 > 2 && 3 >= 4 = false") ("true\n", "", 0) ""
                Expect.equal (compileAndRun "1 = 1 && true <> false && not (2 = 3)") ("true\n", "", 0) ""
                Expect.equal (compileAndRun "-1 < 0") ("true\n", "", 0) "음수 비교"
            }
            test "if" {
                Expect.equal (compileAndRun "if 1 < 2 then 10 else 20") ("10\n", "", 0) ""
                Expect.equal (compileAndRun "if false then true else if 1 = 1 then false else true") ("false\n", "", 0) ""
            }
            test "0으로 나누기는 오류" {
                Expect.equal (compileAndRun "1 / 0") ("", "Error: Attempted to divide by zero.\n", 1) ""
            }
            test "MinValue / -1 은 오버플로 오류" {
                Expect.equal
                    (compileAndRun "(-2147483647 - 1) / -1")
                    ("", "Error: Arithmetic operation resulted in an overflow.\n", 1)
                    ""
                Expect.equal (compileAndRun "(-2147483647 - 1) / 1") ("-2147483648\n", "", 0) "-1 이 아니면 정상"
            }
            test "단락 평가: 오른쪽은 계산하지 않음" {
                Expect.equal (compileAndRun "false && 1 / 0 = 0") ("false\n", "", 0) ""
                Expect.equal (compileAndRun "true || 1 / 0 = 0") ("true\n", "", 0) ""
                Expect.equal (compileAndRun "if true then 1 else 1 / 0") ("1\n", "", 0) ""
                Expect.equal (compileAndRun "true && 1 / 0 = 0") ("", "Error: Attempted to divide by zero.\n", 1) ""
            }
        ]

        test "차등: 컴파일할 수 있는 평가 예제는 인터프리터와 같은 결과" {
            requireTools () |> ignore
            let inputs = (EvalTests.cases |> List.map fst) @ runtimeErrorCases |> List.filter compilable
            // 예제마다 외부 도구를 세 번 실행하므로 병렬로 컴파일
            let actual = inputs |> Array.ofList |> Array.Parallel.map compileAndRun
            for input, result in Array.zip (Array.ofList inputs) actual do
                Expect.equal result (interpret input) input
            // 단계가 올라갈수록 늘어남 (단계 1: 정수, 불리언, if)
            Expect.isGreaterThanOrEqual inputs.Length 43 "컴파일한 예제 수"
        }
    ]
