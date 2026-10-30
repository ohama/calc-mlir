/// 외부 도구(mlir-opt, mlir-translate, clang)와 C 런타임으로 MLIR 텍스트를 실행 파일로 만듦 (docs/compiler-design.md 2, 3장)
module Toolchain

open System
open System.Diagnostics
open System.IO

type Tools =
    { MlirOpt: string
      MlirTranslate: string
      Clang: string
      /// 빌드 출력 폴더로 복사된 runtime/runtime.c
      Runtime: string
      /// 생성한 코드를 컴파일하는 clang 의 최적화 수준. 기본은 -O2.
      /// tail call 은 tailcc 의 tail 호출이라 -O0 에서도 보장됨 (테스트가 -O0 으로 확인)
      OptimizationLevel: string }

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
    let runtime = Path.Combine(AppContext.BaseDirectory, "runtime", "runtime.c")
    let missing =
        [ for name in [ "mlir-opt"; "mlir-translate"; "clang" ] do
              if (tool name).IsNone then name
          if not (File.Exists runtime) then "runtime/runtime.c" ]
    match missing with
    | [] ->
        Ok
            { MlirOpt = (tool "mlir-opt").Value
              MlirTranslate = (tool "mlir-translate").Value
              Clang = (tool "clang").Value
              Runtime = runtime
              OptimizationLevel = "-O2" }
    | names ->
        Error(
            sprintf "Missing tools: %s (install with: brew install llvm, or set CALC_LLVM_BIN)"
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

/// MLIR 텍스트 → 오브젝트 파일 dir/name.o (중간 파일 name.mlir, name.llvm.mlir, name.ll 도 dir 에)
let private compileObject (tools: Tools) (dir: string) (name: string) (mlir: string) =
    let path ext = Path.Combine(dir, name + ext)
    File.WriteAllText(path ".mlir", mlir)
    // scf/cf/arith 를 llvm 방언으로 (함수는 이미 llvm.func)
    run tools.MlirOpt
        [ path ".mlir"
          "--convert-scf-to-cf"
          "--convert-cf-to-llvm"
          "--convert-arith-to-llvm"
          "--reconcile-unrealized-casts"
          "-o"
          path ".llvm.mlir" ]
    run tools.MlirTranslate [ "--mlir-to-llvmir"; path ".llvm.mlir"; "-o"; path ".ll" ]
    run tools.Clang [ tools.OptimizationLevel; "-Wno-override-module"; "-c"; path ".ll"; "-o"; path ".o" ]
    path ".o"

/// 런타임(runtime/ 의 .c 파일들)을 컴파일한 오브젝트. 런타임 파일(.c, .h)과 도구가 같으면 임시 폴더의 캐시를 다시 씀
/// (컴파일할 때마다 런타임을 다시 컴파일하지 않음)
let private runtimeObjects (tools: Tools) =
    let dir = Path.GetDirectoryName tools.Runtime
    let files =
        Directory.GetFiles(dir)
        |> Array.filter (fun f -> f.EndsWith ".c" || f.EndsWith ".h")
        |> Array.sort
    let key =
        String.concat "\n" [ yield! files |> Array.map File.ReadAllText; tools.Clang ]
        |> Text.Encoding.UTF8.GetBytes
        |> Security.Cryptography.SHA256.HashData
        |> Convert.ToHexString
    [ for source in files |> Array.filter (fun f -> f.EndsWith ".c") do
          let name = Path.GetFileNameWithoutExtension source
          let cached = Path.Combine(Path.GetTempPath(), sprintf "calc-%s-%s.o" name (key.Substring(0, 16)))
          if not (File.Exists cached) then
              // 다른 프로세스와 동시에 만들 수 있으므로 임시 이름으로 만든 뒤 옮김 (같은 폴더라 원자적)
              let temp = cached + "." + Guid.NewGuid().ToString("N")
              run tools.Clang [ "-O2"; "-c"; source; "-o"; temp ]
              File.Move(temp, cached, true)
          yield cached ]

let private link (tools: Tools) (objects: string list) (output: string) =
    run tools.Clang
        [ yield! objects
          yield! runtimeObjects tools
          // 깊은 재귀: 메인 스레드 스택을 512MB 로 (인터프리터의 큰 스택 스레드와 같은 크기, macOS ld)
          yield "-Wl,-stack_size,0x20000000"
          yield "-o"
          yield Path.GetFullPath output ]

let private withTempDir (keepTemps: bool) (action: string -> unit) : string option =
    let dir = Path.Combine(Path.GetTempPath(), "calc-" + Guid.NewGuid().ToString("N").Substring(0, 8))
    Directory.CreateDirectory dir |> ignore
    try
        action dir
        if keepTemps then Some dir else None
    finally
        if not keepTemps then
            try
                Directory.Delete(dir, true)
            with _ ->
                ()

/// MLIR 텍스트(Codegen.emitProgram) → 실행 파일. keepTemps 면 중간 파일 폴더 경로를 반환하고 지우지 않음
let build (tools: Tools) (mlir: string) (output: string) (keepTemps: bool) : string option =
    withTempDir keepTemps (fun dir -> link tools [ compileObject tools dir "prog" mlir ] output)

/// 여러 프로그램을 실행 파일 하나로. modules 는 Codegen.emitEntries 의 MLIR (병렬로 컴파일), entries 는 모든 진입 함수 이름.
/// 실행 파일 사용법:
///   prog <번호>          번호의 프로그램을 실행하고 결과를 출력 (calc --compile 의 실행 파일과 같음)
///   prog --all <폴더>    프로그램마다 fork 한 자식 프로세스에서 실행하고, 폴더에 <번호>.out, <번호>.err, <번호>.code 를 남김
/// 테스트에서 외부 도구, 새 실행 파일, 프로세스 생성 수를 줄이려고 씀
let buildMany (tools: Tools) (modules: string list) (entries: string list) (output: string) =
    withTempDir false (fun dir ->
        let objects =
            modules
            |> Array.ofList
            |> Array.Parallel.mapi (fun i mlir -> compileObject tools dir (sprintf "part%d" i) mlir)
            |> List.ofArray
        let driver = Path.Combine(dir, "driver.c")
        File.WriteAllLines(
            driver,
            [ "#include <stdint.h>"
              "#include <stdio.h>"
              "#include <stdlib.h>"
              "#include <string.h>"
              "#include <sys/wait.h>"
              "#include <unistd.h>"
              "void calc_init(void);"
              "void calc_print_result(int64_t v);"
              yield! entries |> List.map (sprintf "int64_t %s(void);")
              "static int64_t (*const entries[])(void) = {"
              yield! entries |> List.map (sprintf "    %s,")
              "};"
              sprintf "static const int count = %d;" entries.Length
              "static void run(int i) {"
              "    calc_init();"
              "    calc_print_result(entries[i]());"
              "}"
              "static FILE *open_result(const char *dir, int i, const char *ext) {"
              "    char path[4096];"
              "    snprintf(path, sizeof path, \"%s/%d.%s\", dir, i, ext);"
              "    return fopen(path, \"w\");"
              "}"
              "int main(int argc, char **argv) {"
              "    if (argc == 3 && strcmp(argv[1], \"--all\") == 0) {"
              "        for (int i = 0; i < count; i++) {"
              "            fflush(NULL);"
              "            pid_t pid = fork();"
              "            if (pid == 0) {"
              "                char path[4096];"
              "                snprintf(path, sizeof path, \"%s/%d.out\", argv[2], i);"
              "                freopen(path, \"w\", stdout);"
              "                snprintf(path, sizeof path, \"%s/%d.err\", argv[2], i);"
              "                freopen(path, \"w\", stderr);"
              "                run(i);"
              "                exit(0);"
              "            }"
              "            int status = 0;"
              "            waitpid(pid, &status, 0);"
              "            /* 셸과 같은 종료 코드: 신호로 끝나면 128 + 신호 번호 */"
              "            int code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);"
              "            FILE *f = open_result(argv[2], i, \"code\");"
              "            fprintf(f, \"%d\", code);"
              "            fclose(f);"
              "        }"
              "        return 0;"
              "    }"
              "    run(atoi(argv[1]));"
              "    return 0;"
              "}" ]
        )
        let driverObject = Path.Combine(dir, "driver.o")
        run tools.Clang [ "-O2"; "-c"; driver; "-o"; driverObject ]
        link tools (objects @ [ driverObject ]) output)
    |> ignore
