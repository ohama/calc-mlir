/// 외부 도구(mlir-opt, mlir-translate, clang)와 C 런타임으로 MLIR 텍스트를 실행 파일로 만듦 (docs/compiler-design.md 2, 3장)
module Toolchain

open System
open System.Diagnostics
open System.IO

type Tools =
    { MlirOpt: string
      MlirTranslate: string
      Clang: string
      GcPrefix: string
      /// 빌드 출력 폴더로 복사된 runtime/runtime.c
      Runtime: string }

/// 외부 도구 실행이 실패함 (도구의 표준 에러를 담음)
exception ToolError of string with
    override this.Message = this.Data0

/// 찾는 순서: 환경 변수 → Homebrew 경로 → PATH
let private findTool (envVar: string) (defaultDir: string) (name: string) =
    let candidates =
        [ match Environment.GetEnvironmentVariable envVar with
          | null | "" -> ()
          | dir -> Path.Combine(dir, name)
          Path.Combine(defaultDir, name)
          yield!
              (match Environment.GetEnvironmentVariable "PATH" with
               | null -> []
               | path -> path.Split(Path.PathSeparator) |> Array.map (fun dir -> Path.Combine(dir, name)) |> List.ofArray) ]
    candidates |> List.tryFind File.Exists

/// 필요한 도구를 모두 찾으면 Ok, 하나라도 없으면 무엇이 없는지와 설치 방법
let find () : Result<Tools, string> =
    let llvmDir = "/opt/homebrew/opt/llvm/bin"
    let tool name = findTool "CALC_LLVM_BIN" llvmDir name
    let gcPrefix =
        match Environment.GetEnvironmentVariable "CALC_GC_PREFIX" with
        | null | "" -> "/opt/homebrew/opt/bdw-gc"
        | prefix -> prefix
    let runtime = Path.Combine(AppContext.BaseDirectory, "runtime", "runtime.c")
    let missing =
        [ for name in [ "mlir-opt"; "mlir-translate"; "clang" ] do
              if (tool name).IsNone then name
          if not (File.Exists(Path.Combine(gcPrefix, "include", "gc.h"))) then "bdw-gc (gc.h)"
          if not (File.Exists runtime) then "runtime/runtime.c" ]
    match missing with
    | [] ->
        Ok
            { MlirOpt = (tool "mlir-opt").Value
              MlirTranslate = (tool "mlir-translate").Value
              Clang = (tool "clang").Value
              GcPrefix = gcPrefix
              Runtime = runtime }
    | names ->
        Error(
            sprintf "Missing tools: %s (install with: brew install llvm bdw-gc, or set CALC_LLVM_BIN / CALC_GC_PREFIX)"
                (String.concat ", " names)
        )

let private run (file: string) (args: string list) =
    let psi = ProcessStartInfo(file)
    for a in args do
        psi.ArgumentList.Add a
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Process.Start psi
    let stderrTask = p.StandardError.ReadToEndAsync()
    p.StandardOutput.ReadToEnd() |> ignore
    p.WaitForExit()
    let stderr = stderrTask.Result
    if p.ExitCode <> 0 then
        raise (ToolError(sprintf "%s failed (exit %d):\n%s" (Path.GetFileName file) p.ExitCode (stderr.TrimEnd())))

/// MLIR 텍스트 → 실행 파일. keepTemps 면 중간 파일 폴더 경로를 반환하고 지우지 않음
let build (tools: Tools) (mlir: string) (output: string) (keepTemps: bool) : string option =
    let dir = Path.Combine(Path.GetTempPath(), "calc-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory dir |> ignore
    try
        let source = Path.Combine(dir, "prog.mlir")
        let lowered = Path.Combine(dir, "prog.llvm.mlir")
        let llvmIr = Path.Combine(dir, "prog.ll")
        File.WriteAllText(source, mlir)
        // scf/cf/arith 를 llvm 방언으로 (함수는 이미 llvm.func)
        run tools.MlirOpt
            [ source
              "--convert-scf-to-cf"
              "--convert-cf-to-llvm"
              "--convert-arith-to-llvm"
              "--reconcile-unrealized-casts"
              "-o"
              lowered ]
        run tools.MlirTranslate [ "--mlir-to-llvmir"; lowered; "-o"; llvmIr ]
        run tools.Clang
            [ "-O2"
              "-Wno-override-module"
              llvmIr
              tools.Runtime
              "-I" + Path.Combine(tools.GcPrefix, "include")
              "-L" + Path.Combine(tools.GcPrefix, "lib")
              "-lgc"
              "-o"
              Path.GetFullPath output ]
        if keepTemps then Some dir else None
    finally
        if not keepTemps then
            try
                Directory.Delete(dir, true)
            with _ ->
                ()
