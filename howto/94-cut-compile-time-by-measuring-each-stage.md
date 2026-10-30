---
order: 94
created: 2026-10-30
commit: a5258f7
version: 0.66.1
description: 외부 도구를 부르는 컴파일러의 시간은 단계마다 재야 어디로 가는지 보인다. 도구를 따로 실행한 시간과 컴파일러 안에서 부른 시간의 차이는 프로세스를 만드는 비용이다. 도구를 셸 하나로 묶고, 링크가 느린 원인(Homebrew clang이 넘기는 libLTO)을 찾아 Apple clang으로 링크한다. 남은 JIT은 ReadyToRun publish로 줄인다
---

# 단계마다 재서 컴파일 시간 줄이기

> 관련 커밋: [a5258f7](https://github.com/ohama/calc-mlir/commit/a5258f7) (v0.66.1)

`calc --compile`은 F#으로 파싱, 타입 추론, MLIR 생성을 한 뒤 외부 도구 넷을 차례로 부른다: `mlir-opt`, `mlir-translate`, `clang -c`, 링크. 작은 프로그램도 0.3초쯤 걸렸다.

| 프로그램 | 0.66.0 | 0.66.1 | 0.66.1, ReadyToRun |
|---|---|---|---|
| `benchmarks/bst.calc` | 314ms | 285ms | 187ms |
| `benchmarks/fib.calc` | 286ms | 255ms | 165ms |
| `benchmarks/msort.calc` | 326ms | 299ms | |

## The Insight

**외부 도구의 시간은 "도구가 일한 시간"과 "도구를 부르는 시간"으로 나뉜다.** 도구를 셸에서 따로 실행한 시간과 컴파일러 안에서 부른 시간을 나란히 재면 차이가 보인다. 차이가 바로 부르는 쪽의 비용이다. `mlir-opt`는 혼자 15ms였는데 .NET 안에서는 30ms였다. .NET의 `Process.Start`가 프로세스 하나에 11ms 남짓 들었다.

**같은 도구라도 누가 부르느냐에 따라 일이 다르다.** 링크가 49ms였던 것은 ld가 느려서가 아니었다. Homebrew clang이 링커에 `-lto_library <Homebrew의 libLTO.dylib>`를 넘겼고, ld가 그 큰 라이브러리를 읽었다. `/usr/bin/clang`(Apple clang)으로 링크하면 28ms다.

## Why This Matters

- **어림짐작으로 고르면 엉뚱한 곳을 다듬는다.** "LLVM 최적화가 오래 걸리겠지" 싶어 `clang -O1`로 바꾸면 7~14ms가 줄지만, 생성 코드가 느려진다. 프로세스 만들기 비용 30ms는 아무것도 잃지 않고 줄일 수 있었다.
- **작은 고정 비용이 많다.** 작은 프로그램에서는 도구가 일하는 시간보다 시작, JIT, 프로세스 만들기 같은 고정 비용이 더 크다. 사용자가 가장 자주 컴파일하는 것이 작은 프로그램이다.

## Recognition Pattern

- 컴파일러가 외부 도구(어셈블러, 링커, 다른 컴파일러)를 여러 번 부를 때
- 런타임이 무거운 언어(.NET, JVM)에서 프로세스를 만들 때
- 작은 입력에서도 시간이 일정하게 0.2~0.3초 걸릴 때

## The Approach

### Step 1: 단계마다 시간을 출력하는 스위치를 만든다

```fsharp
let timings = Environment.GetEnvironmentVariable "CALC_TIMINGS" = "1"

let timed (name: string) (action: unit -> 'a) : 'a =
    if not timings then action ()
    else
        let sw = Stopwatch.StartNew()
        let result = action ()
        eprintfn "  %-16s %6.1f ms" name sw.Elapsed.TotalMilliseconds
        result
```

F# 단계(`parse + prelude`, `typecheck`, `lower + codegen`)와 외부 도구를 하나씩 감싼다. 스위치가 켜지면 도구를 하나씩 따로 실행해 각각 잰다.

```
$ CALC_TIMINGS=1 calc --compile=bst benchmarks/bst.calc
  parse + prelude    34.0 ms
  typecheck          22.1 ms
  lower + codegen    42.3 ms
  mlir-opt           30.2 ms
  mlir-translate     22.4 ms
  clang              42.0 ms
  clang              58.1 ms     ← 링크
```

### Step 2: 도구를 따로 실행해 비교한다

`--keep-temps`로 중간 파일을 남기고, 같은 명령을 셸에서 `time`으로 잰다. 도구가 혼자 15ms인데 안에서 30ms면 차이가 부르는 비용이다. 빈 명령(`/usr/bin/true`)을 `Process.Start`로 부르는 시간도 따로 잰다(11.5ms).

### Step 3: 도구를 셸 하나로 묶는다

도구마다 프로세스를 만드는 대신, 네 단계를 적은 `/bin/sh -c` 스크립트 하나를 실행한다. 오류 메시지는 전과 같아야 하므로 단계마다 표준 에러를 파일로 받고, 실패한 단계의 번호와 종료 코드를 남긴다.

```sh
'mlir-opt' '...' 2>'dir/step0.err' >/dev/null; code=$?; if [ $code -ne 0 ]; then echo 0 $code > 'dir/failed'; exit 1; fi
'mlir-translate' '...' 2>'dir/step1.err' >/dev/null; code=$?; ...
```

실패하면 `failed`와 `stepN.err`를 읽어 전과 같은 `Error: mlir-translate failed (exit 3): ...`를 낸다. 인자는 작은따옴표로 감싸고, 인자 안의 `'`는 `'\''`로 바꾼다. 가짜 도구(항상 실패하는 스크립트)와 작은따옴표가 든 경로로 두 모드를 모두 시험한다.

### Step 4: 링크가 느린 원인을 `-v`로 본다

```sh
clang -v x.o -o x 2>&1 | grep ld     # Homebrew clang: ... -lto_library /opt/homebrew/.../libLTO.dylib
```

ld를 직접 부르면 27ms, Apple clang으로 부르면 28ms, Homebrew clang으로 부르면 49ms였다. LTO를 쓰지 않으므로 `/usr/bin/clang`이 있으면 그것으로 링크한다. 컴파일(`clang -c`)은 MLIR이 만든 LLVM IR 버전에 맞는 Homebrew clang 그대로 둔다.

### Step 5: 남은 고정 비용을 확인한다

남은 시간의 대부분은 .NET 시작(55ms)과 F# 단계의 JIT(100ms)이다. ReadyToRun으로 publish하면 미리 컴파일되어 F# 단계가 30ms가 된다. 빌드 설정에 넣으면 RID가 빌드에 들어가고 빌드가 느려지므로, 설치 방법으로 권장한다.

```sh
dotnet publish src/Calc -c Release -r osx-arm64 -p:PublishReadyToRun=true -o dist
```

## Example

해 보고 넣지 않은 것도 적어 둔다.

| 후보 | 결과 | 한 것 |
|---|---|---|
| 도구 넷을 셸 하나로 | 약 30ms 줄어듦 | 함 |
| Apple clang으로 링크 | 49ms → 28ms | 함 |
| ReadyToRun publish | 285ms → 187ms | 설치 방법으로 권장 |
| `InvariantGlobalization`, `gcConcurrent=0`, `TieredPGO=0` | 합쳐서 20ms 남짓, PGO를 끄면 인터프리터가 느려질 수 있음 | 안 함 |
| `clang -O1` / `-O0` | 7~14ms 줄지만 생성 코드가 느려짐 | 안 함 |
| 컴파일과 링크를 clang 하나로 | 80ms로 오히려 느림 | 안 함 |

## 체크리스트

- [ ] 단계마다 시간을 출력하는 스위치가 있는가?
- [ ] 도구를 따로 실행한 시간과 안에서 부른 시간을 비교했는가?
- [ ] 프로세스를 하나 만드는 비용을 따로 쟀는가?
- [ ] 도구를 묶은 뒤에도 실패 메시지(도구 이름, 종료 코드, 표준 에러)가 같은가?
- [ ] 느린 도구는 `-v`로 실제로 무엇을 하는지 보았는가?
- [ ] 생성 코드를 느리게 하는 후보는 빼고, 결과를 적었는가?

## 관련 문서

- `75-profile-native-executable-and-tune-boehm-gc.md` - 실행 파일의 시간을 재는 방법
- `89-keep-every-call-path-when-changing-a-calling-convention.md` - 재서 고르는 같은 방법
