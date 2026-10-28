---
created: 2026-10-27
description: 트리 순회 인터프리터는 언어의 재귀 깊이만큼 .NET 스택을 쓴다. StackOverflow는 잡을 수 없으므로 큰 스택의 스레드에서 평가한다
---

# 인터프리터의 깊은 재귀로 생기는 Stack Overflow 다루기

트리 순회(tree-walking) 인터프리터에서 **구현하는 언어의 재귀 한 단계는 .NET 스택 여러 프레임**을 쓴다. 그래서 언어 쪽에서는 겨우 수천 단계의 재귀인데도 .NET 프로세스가 죽는다. .NET은 `StackOverflowException`을 `try`로 잡을 수 없다.

## The Insight

`loop (n - 1)` 한 번을 평가할 때 F# 평가기 안에서는 이런 호출이 일어난다.

```
evalIn (App ...)            ← loop (n - 1)
  └ apply
      └ evalIn (If ...)     ← 함수 본문
          └ evalIn (App ...)  ← 다음 loop 호출
              └ apply ...
```

구현 언어의 호출 1번이 F# 함수 호출 여러 번이 된다. 기본 스택(메인 스레드, 1MB 안팎)은 금방 바닥난다.

그리고 **.NET은 스택 오버플로를 잡을 수 없다**. `try ... with`가 있어도 런타임이 `Stack overflow.`를 출력하고 프로세스를 즉시 끝낸다. 그래서 "오류 메시지를 출력하고 종료 코드 1"이라는 평소 오류 처리 경로를 타지 않는다.

## Why This Matters

함수와 `let rec`을 추가한 직후 측정한 결과다(Debug 빌드, `let rec loop n = if n = 0 then 0 else loop (n - 1) in loop N`).

| N | 기본 스택 | 512MB 스레드 |
|---|---|---|
| 4,000 | ✅ `0` | ✅ |
| 7,000 | ❌ `Stack overflow.` (종료 코드 134) | ✅ |
| 100,000 | ❌ | ✅ |
| 1,000,000 | ❌ | ❌ `Stack overflow.` |

- **CLI**: 재귀 깊이 7,000은 조금만 큰 입력으로도 넘는다. 리스트 길이나 카운터 루프가 그 예다.
- **테스트 (더 헷갈리는 증상)**: 테스트 하나가 무한 재귀에 빠지면 **테스트 호스트 프로세스 전체가 죽는다**. `dotnet test` 출력은 이 한 줄뿐이다.

  ```
  Test Run Aborted.
  ```

  어느 테스트가 원인인지, 몇 개가 통과했는지 나오지 않는다. 이 프로젝트에서는 `let rec f n = f (n + 1)`이 바깥 `f`를 부를 것이라고 잘못 예상한 테스트 케이스 하나가 원인이었다.

## Recognition Pattern

- 출력이 `Stack overflow.`이고 종료 코드가 134다 (macOS/Linux의 SIGABRT)
- `try ... with`로 감쌌는데도 오류 메시지 없이 프로세스가 끝난다
- `dotnet test`가 `Test Run Aborted.`만 출력하고 결과 요약이 없다
- 재귀 함수, 반복, 깊이 중첩된 식(예: `1+1+1+...` 수만 개)을 지원하는 인터프리터를 만들고 있다

## The Approach

순서대로 생각한다.

1. **한계를 측정한다.** 추측하지 말고 N을 늘려 가며 실제 한계를 찾는다.
2. **스택을 키운다.** 가장 싸고 효과가 큰 방법이다. 이 문서의 방법이다.
3. **그래도 부족하면** 구조를 바꾼다. tail call 최적화, 트램폴린, CPS 변환, 명시적 스택 기계가 선택지다. 비용이 크다.
4. **어떤 방법을 쓰든 한계는 남는다.** 한계를 문서에 적는다.

### Step 1: 한계를 측정한다

```bash
D=src/Calc/bin/Debug/net10.0/Calc.dll
for n in 1000 2000 4000 7000; do
  printf "%s: " $n
  dotnet $D -e "let rec loop n = if n = 0 then 0 else loop (n - 1) in loop $n" 2>&1 | grep -E "Result|Stack"
done
```

### Step 2: 평가를 큰 스택의 스레드에서 실행한다

`System.Threading.Thread`의 생성자는 `maxStackSize`를 받는다. 스레드 안에서 난 일반 예외는 밖으로 꺼내서 다시 던진다. 그래야 기존 `try ... with` 오류 처리가 그대로 동작한다.

```fsharp
/// 평가기는 재귀 호출 깊이만큼 .NET 스택을 사용하고, StackOverflow 는 catch 할 수 없음.
/// 기본 스택(1MB 내외)으로는 재귀 깊이 수천에서 프로세스가 죽으므로 큰 스택의 스레드에서 실행.
let evalStackSize = 512 * 1024 * 1024

let runWithLargeStack (f: unit -> 'T) : 'T =
    let mutable result = Unchecked.defaultof<'T>
    let mutable error = None
    let thread =
        System.Threading.Thread(
            (fun () ->
                try result <- f ()
                with ex -> error <- Some ex),
            evalStackSize)
    thread.Start()
    thread.Join()
    match error with
    | Some ex -> raise ex
    | None -> result
```

```fsharp
let value = runWithLargeStack (fun () -> Eval.eval ast)
```

- 512MB는 가상 메모리 **예약**이다. 실제로 쓰는 만큼만 물리 메모리를 차지한다.
- `raise ex`는 원래 스택 트레이스를 덮어쓴다. 트레이스가 중요하면 `ExceptionDispatchInfo.Capture(ex).Throw()`를 쓴다. 이 프로젝트는 메시지만 출력하므로 `raise`로 충분하다.

### Step 3: 테스트에서 무한 재귀를 조심한다

- 재귀 테스트 케이스를 추가할 때는 **종료 조건이 확실히 걸리는지** 먼저 CLI로 실행해 본다.
- `Test Run Aborted.`가 나오면 `dotnet run --project tests/X -- --debug --sequenced`로 실행한다.
  - `--debug`는 테스트마다 `Lexer.불리언과 if 키워드 starting...`처럼 시작을 출력한다.
  - `--sequenced`는 테스트를 하나씩 실행한다. Expecto는 기본이 병렬이라 이 옵션이 없으면 마지막으로 시작된 테스트가 범인이 아닐 수 있다.
  - 프로세스가 죽기 직전에 출력된 `starting...` 줄이 원인 테스트다.
- 테스트는 보통 `Eval.eval`을 기본 스택에서 direct call한다. 깊은 재귀를 테스트하려면 테스트에서도 `runWithLargeStack`을 쓴다.

### Step 4: 한계를 문서에 남긴다

```markdown
- 재귀 깊이 한계: 약 100,000 (512MB 스택). 넘으면 `Stack overflow.` 후 프로세스 종료.
- 꼬리 재귀 최적화는 하지 않는다.
```

## Example

스택을 키운 뒤 측정한 결과다.

```
10000: Result: 0
100000: Result: 0
1000000: Stack overflow.
```

일반 오류는 여전히 같은 경로로 처리된다.

```
$ calc -e "1 / 0"
Error: Attempted to divide by zero.
```

## 체크리스트

- [ ] 재귀를 지원하게 된 시점에 깊이 한계를 실제로 측정했다
- [ ] 평가를 큰 스택의 스레드에서 실행한다
- [ ] 스레드 안의 예외를 밖으로 다시 던져서 기존 오류 처리가 동작한다
- [ ] 남은 한계(깊이, 꼬리 재귀 최적화 없음)를 문서에 적었다
- [ ] `Test Run Aborted.`가 나오면 무한 재귀를 먼저 의심한다

## 관련 문서

- `implement-closures-and-let-rec.md` - 재귀를 가능하게 하는 `let rec` 구현
