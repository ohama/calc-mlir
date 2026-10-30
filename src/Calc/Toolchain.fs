/// 외부 도구(mlir-opt, mlir-translate, clang)와 C 런타임으로 MLIR 텍스트를 실행 파일로 만듦 (docs/compiler-design.md 2, 3장)
module Toolchain

open System
open System.Diagnostics
open System.IO

type Tools =
    { MlirOpt: string
      MlirTranslate: string
      Clang: string
      /// 링크에 쓰는 clang. macOS 면 Apple clang (/usr/bin/clang): Homebrew clang 은 링커에 Homebrew 의 큰 libLTO.dylib 을
      /// 넘겨서 LTO 를 쓰지 않아도 ld 가 그것을 읽음 (링크 49ms → 28ms)
      Linker: string
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
              Linker = (if File.Exists "/usr/bin/clang" then "/usr/bin/clang" else (tool "clang").Value)
              Runtime = runtime
              OptimizationLevel = "-O2" }
    | names ->
        Error(
            sprintf "Missing tools: %s (install with: brew install llvm, or set CALC_LLVM_BIN)"
                (String.concat ", " names)
        )

/// CALC_TIMINGS=1 이면 단계마다 걸린 시간을 표준 에러로 (컴파일 시간을 잴 때)
let timings = Environment.GetEnvironmentVariable "CALC_TIMINGS" = "1"

/// 단계 이름과 걸린 시간 (CALC_TIMINGS)
let timed (name: string) (action: unit -> 'a) : 'a =
    if not timings then action ()
    else
        let watch = Stopwatch.StartNew()
        let result = action ()
        eprintfn "  %-16s %6.1f ms" name watch.Elapsed.TotalMilliseconds
        result

let private runTool (file: string) (args: string list) =
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

let private run (file: string) (args: string list) = timed (Path.GetFileName file) (fun () -> runTool file args)

/// 외부 도구 실행 한 번: (도구 경로, 인자)
type private Step = string * string list

/// 단계들을 차례로 실행. .NET 에서 프로세스를 하나 만드는 데 11ms 남짓 들어서, 셸 하나가 단계를 모두 실행함.
/// 실패하면 셸이 dir 에 실패한 단계 번호와 그 단계의 표준 에러를 남기고, 도구를 따로 실행했을 때와 같은 ToolError.
/// CALC_TIMINGS=1 이면 단계마다 시간을 재려고 따로 실행
let private runSteps (dir: string) (steps: Step list) =
    if timings then
        for file, args in steps do
            run file args
    else
        let quote (a: string) = "'" + a.Replace("'", "'\\''") + "'"
        let script =
            steps
            |> List.mapi (fun i (file, args) ->
                let err = quote (Path.Combine(dir, sprintf "step%d.err" i))
                sprintf "%s 2>%s >/dev/null; code=$?; if [ $code -ne 0 ]; then echo %d $code > %s; exit 1; fi"
                    (String.concat " " (List.map quote (file :: args))) err i (quote (Path.Combine(dir, "failed"))))
            |> String.concat "\n"
        let psi = ProcessStartInfo("/bin/sh")
        psi.ArgumentList.Add "-c"
        psi.ArgumentList.Add script
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        use p = Process.Start psi
        let stderrTask = p.StandardError.ReadToEndAsync()
        p.StandardOutput.ReadToEnd() |> ignore
        p.WaitForExit()
        let failed = Path.Combine(dir, "failed")
        if File.Exists failed then
            let parts = File.ReadAllText(failed).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            let i, code = int parts[0], int (parts[1].Trim())
            let file, _ = steps[i]
            let stderr = File.ReadAllText(Path.Combine(dir, sprintf "step%d.err" i))
            raise (ToolError(sprintf "%s failed (exit %d):\n%s" (Path.GetFileName file) code (stderr.TrimEnd())))
        elif p.ExitCode <> 0 then
            raise (ToolError(sprintf "sh failed (exit %d):\n%s" p.ExitCode (stderrTask.Result.TrimEnd())))

/// MLIR 텍스트 → 오브젝트 파일 dir/name.o 를 만드는 단계들 (중간 파일 name.mlir, name.llvm.mlir, name.ll 도 dir 에)
let private objectSteps (tools: Tools) (dir: string) (name: string) (mlir: string) : Step list * string =
    let path ext = Path.Combine(dir, name + ext)
    File.WriteAllText(path ".mlir", mlir)
    // scf/cf/arith 를 llvm 방언으로 (함수는 이미 llvm.func)
    [ tools.MlirOpt,
      [ path ".mlir"
        "--convert-scf-to-cf"
        "--convert-cf-to-llvm"
        "--convert-arith-to-llvm"
        "--reconcile-unrealized-casts"
        "-o"
        path ".llvm.mlir" ]
      tools.MlirTranslate, [ "--mlir-to-llvmir"; path ".llvm.mlir"; "-o"; path ".ll" ]
      tools.Clang, [ tools.OptimizationLevel; "-Wno-override-module"; "-c"; path ".ll"; "-o"; path ".o" ] ],
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

let private linkStep (tools: Tools) (objects: string list) (output: string) : Step =
    tools.Linker,
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
    withTempDir keepTemps (fun dir ->
        let steps, objectFile = objectSteps tools dir "prog" mlir
        runSteps dir (steps @ [ linkStep tools [ objectFile ] output ]))

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
            |> Array.Parallel.mapi (fun i mlir ->
                // 모듈마다 폴더를 따로 (셸이 실패를 남기는 파일이 겹치지 않게)
                let part = Path.Combine(dir, sprintf "part%d" i)
                Directory.CreateDirectory part |> ignore
                let steps, objectFile = objectSteps tools part "part" mlir
                runSteps part steps
                objectFile)
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
        runSteps dir [ tools.Clang, [ "-O2"; "-c"; driver; "-o"; driverObject ]; linkStep tools (objects @ [ driverObject ]) output ])
    |> ignore
