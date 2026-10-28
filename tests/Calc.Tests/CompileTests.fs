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
                    (fun () -> Codegen.emitProgram (Program.parse "1 + 2") |> ignore)
                    (fun ex -> Expect.equal ex.Message "Not supported by the compiler yet: Add" "")
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
        ]

        test "차등: 컴파일할 수 있는 평가 예제는 인터프리터와 같은 결과" {
            requireTools () |> ignore
            let inputs = EvalTests.cases |> List.map fst |> List.filter compilable
            for input in inputs do
                let expected = Eval.formatValue (Eval.eval (Program.parse input)) + "\n"
                Expect.equal (compileAndRun input) (expected, "", 0) input
            // 단계가 올라갈수록 늘어남 (단계 0: 리터럴만)
            Expect.isGreaterThanOrEqual inputs.Length 2 "컴파일한 예제 수"
        }
    ]
