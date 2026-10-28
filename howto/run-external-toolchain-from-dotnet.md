---
created: 2026-10-28
description: .NET에서 외부 컴파일러를 실행할 때는 ArgumentList로 인자를 넘기고 stderr를 비동기로 읽는다. 동기로 차례로 읽으면 pipe가 차서 멈춘다. 도구는 환경 변수 → 기본 경로 → PATH 순서로 찾고, 없으면 테스트를 건너뛴다
---

# .NET에서 외부 툴체인(mlir-opt, clang) 실행하기

F#으로 만든 컴파일러가 `mlir-opt`, `mlir-translate`, `clang`을 차례로 실행해서 실행 파일을 만든다. 이때 지킬 것은 다섯 가지다.

1. 도구를 찾는 순서를 정한다.
2. 인자는 `ArgumentList`로 넘긴다.
3. stderr는 비동기로 읽는다.
4. 함께 쓰는 C 파일은 출력 폴더로 복사한다.
5. 도구가 없으면 테스트를 실패가 아니라 Skipped로 처리한다.

## The Insight

**자식 프로세스의 stdout과 stderr는 크기가 정해진 pipe다.** 둘 다 리다이렉트한 상태에서 `StandardOutput.ReadToEnd()`를 먼저 부르면 문제가 생긴다. 자식이 stderr에 pipe 버퍼(macOS 64KB)보다 많이 쓰면 자식은 쓰기에서 멈추고, 부모는 stdout이 끝나기를 기다린다. 서로를 기다리니 영원히 끝나지 않는다.

stderr 200KB를 쓰는 프로세스로 직접 확인했다.

| 읽는 방법 | 결과 |
|---|---|
| stdout `ReadToEnd()` 다음 stderr `ReadToEnd()` | 30초 타임아웃 (멈춤) |
| stderr `ReadToEndAsync()`를 먼저 시작하고 stdout `ReadToEnd()` | 바로 끝남 |

컴파일러 오류 메시지는 길어질 수 있다. MLIR 검증 오류는 연산 전체를 출력한다. 그래서 이 문제는 실제로 일어난다.

## Why This Matters

- **deadlock:** 테스트가 실패하지도 않고 멈춘다. CI에서는 타임아웃으로만 보인다.
- **인자를 문자열 하나로 넘긴 경우:** 공백이 든 경로(`/Users/me/My Projects/...`)에서 인자가 둘로 갈라진다.
- **도구 위치를 PATH에만 의존한 경우:** Homebrew `llvm`은 keg-only라 PATH에 없어서 "설치했는데 못 찾는다"가 된다.
- **도구가 없을 때 테스트가 실패하는 경우:** 컴파일러와 상관없는 기여자의 `dotnet test`까지 빨갛게 된다.

## Recognition Pattern

- .NET 코드에서 컴파일러, 링커, 포매터 같은 외부 CLI를 실행할 때
- 외부 도구가 있어야만 돌 수 있는 테스트를 만들 때
- C 소스나 스크립트를 실행 파일 옆에 두고 써야 할 때

## The Approach

### Step 1: 찾는 순서는 환경 변수 → 기본 경로 → PATH

```fsharp
let private findTool (envVar: string) (defaultDir: string) (name: string) =
    let candidates =
        [ match Environment.GetEnvironmentVariable envVar with
          | null | "" -> ()
          | dir -> Path.Combine(dir, name)
          Path.Combine(defaultDir, name)           // /opt/homebrew/opt/llvm/bin
          yield!
              (match Environment.GetEnvironmentVariable "PATH" with
               | null -> []
               | path -> path.Split(Path.PathSeparator) |> Array.map (fun d -> Path.Combine(d, name)) |> List.ofArray) ]
    candidates |> List.tryFind File.Exists
```

결과는 `Result<Tools, string>`으로 돌려준다. 없는 것을 **모두** 모아서 설치 방법과 함께 한 번에 알려 준다. 하나씩 알려 주면 사용자가 여러 번 다시 실행해야 한다.

```
Missing tools: bdw-gc (gc.h) (install with: brew install llvm bdw-gc, or set CALC_LLVM_BIN / CALC_GC_PREFIX)
```

### Step 2: ArgumentList와 비동기 stderr

```fsharp
let private run (file: string) (args: string list) =
    let psi = ProcessStartInfo(file)
    for a in args do
        psi.ArgumentList.Add a            // 인자별로 따옴표 처리됨
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Process.Start psi
    let stderrTask = p.StandardError.ReadToEndAsync()   // 먼저 시작
    p.StandardOutput.ReadToEnd() |> ignore
    p.WaitForExit()
    if p.ExitCode <> 0 then
        raise (ToolError(sprintf "%s failed (exit %d):\n%s" (Path.GetFileName file) p.ExitCode (stderrTask.Result.TrimEnd())))
```

실패하면 도구 이름과 stderr를 그대로 담는다. 생성한 MLIR이 틀렸을 때 원인을 바로 볼 수 있다.

### Step 3: 중간 파일은 임시 폴더에, 옵션으로 남기기

`calc-XXXXXXXX` 임시 폴더에 `prog.mlir`, `prog.llvm.mlir`, `prog.ll`을 만들고 `finally`에서 지운다. `--keep-temps`를 주면 지우지 않고 경로를 출력한다. 생성한 코드를 디버깅할 때 꼭 필요하다.

### Step 4: 런타임 C 파일을 출력 폴더로 복사

```xml
<!-- Calc.fsproj -->
<None Include="runtime\**" CopyToOutputDirectory="PreserveNewest" LinkBase="runtime" />
```

코드에서는 `Path.Combine(AppContext.BaseDirectory, "runtime", "runtime.c")`로 찾는다. 이 항목은 `ProjectReference`를 따라 **테스트 프로젝트의 출력 폴더에도 복사된다**(`tests/Calc.Tests/bin/Debug/net10.0/runtime/runtime.c`). 그래서 테스트에서도 같은 코드로 찾을 수 있다. 소스 폴더 기준 상대 경로(`../../src/...`)는 실행 위치에 따라 깨진다.

### Step 5: 도구가 없으면 Skipped

Expecto의 `skiptestf`는 테스트를 실패가 아니라 건너뜀으로 표시한다.

```fsharp
let private tools = Toolchain.find ()

let private requireTools () =
    match tools with
    | Ok t -> t
    | Error message -> skiptestf "%s" message
```

`CALC_GC_PREFIX=/nonexistent`로 확인하면 실행 테스트는 Skipped가 되고, 도구가 필요 없는 테스트(MLIR 텍스트 검사)는 그대로 돈다.

## Example

```fsharp
// ❌ BAD: 인자를 문자열로, stdout 과 stderr 를 차례로 동기 읽기
let psi = ProcessStartInfo("clang", sprintf "%s -o %s" input output)   // 공백 경로에서 깨짐
...
let o = p.StandardOutput.ReadToEnd()
let e = p.StandardError.ReadToEnd()      // stderr 가 64KB 를 넘으면 위 줄에서 영원히 멈춤

// ✅ GOOD
psi.ArgumentList.Add input
psi.ArgumentList.Add "-o"
psi.ArgumentList.Add output
let e = p.StandardError.ReadToEndAsync()
let o = p.StandardOutput.ReadToEnd()
```

## 체크리스트

- [ ] 인자를 `ArgumentList`로 넘기는가?
- [ ] 둘 다 리다이렉트했다면 적어도 하나는 비동기로 읽는가?
- [ ] 도구를 환경 변수로 바꿀 수 있는가? 기본 경로에 keg-only 위치가 있는가?
- [ ] 없는 도구를 모두 모아 설치 명령과 함께 알려 주는가?
- [ ] 런타임 파일을 `AppContext.BaseDirectory` 기준으로 찾는가?
- [ ] 도구가 없을 때 테스트가 Skipped인가?

## 관련 문서

- `emit-mlir-as-llvm-func-and-lower.md` - 실행하는 패스와 그 이유
- `write-differential-tests-for-compiler.md` - 이 실행을 쓰는 테스트
- `setup-expecto-with-dotnet-test.md` - Expecto 설정
