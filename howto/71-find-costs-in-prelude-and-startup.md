---
order: 71
created: 2026-10-29
commit: 6b87d26
version: 0.45.0
description: 평가기를 다듬은 뒤에도 느리면 평가기 밖을 잰다. 언어로 쓴 표준 라이브러리는 쓰는 방식(fold에 클로저 넘기기)이 비용을 정하고, 고치면 인터프리터와 컴파일러가 함께 빨라진다. 시작 비용은 프로세스 안에서 단계별로 재고, 명령줄 라이브러리처럼 큰 항목은 흔한 경우만 건너뛴다. 피연산자가 상수나 지역 변수인 연산은 전용 클로저로 만든다
---

# prelude와 시작 비용에서 숨은 비용 찾기

> 관련 커밋: [6b87d26](https://github.com/ohama/calc-mlir/commit/6b87d26) (v0.45.0)

0.44.0에서 인터프리터의 할당을 줄였다(`70-cut-allocations-and-jit-delay-in-dotnet-interpreter.md`). 그 뒤 후보를 더 찾았더니, 가장 큰 두 가지는 평가기 밖에 있었다.

- **prelude:** 언어로 쓴 표준 라이브러리의 `reverse`와 `append`가 msort 평가 시간의 42%였다.
- **명령줄 처리:** Argu가 빈 프로그램 실행 시간의 절반이었다.

| `benchmarks/run.py` (시작 포함) | CPython | 0.44.0 | 0.45.0 |
|---|---|---|---|
| fib 32 (인터프리터) | 0.136초 | 0.231초 | 0.160초 |
| 에라토스테네스의 체 (인터프리터) | 0.296초 | 0.340초 | 0.288초 |
| merge sort 20만 (인터프리터) | 0.207초 | 1.962초 | 1.607초 |
| merge sort 20만 (컴파일한 실행 파일) | 0.207초 | 0.290초 | 0.200초 |
| 빈 프로그램 (인터프리터) | 0.014초 | 0.121초 | 0.067초 |

## The Insight

**평가기를 고친 뒤에는 평가기가 실행하는 코드와 평가기 앞의 코드를 잰다.**

- **표준 라이브러리를 언어로 쓰면 그 코드도 사용자 코드처럼 느릴 수 있다.** `fold`로 짧게 쓴 정의는 원소마다 클로저를 부른다.
  - 인터프리터: 클로저 호출 한 번이 단계 하나다.
  - 컴파일러: 알 수 없는 함수의 indirect call(`calc_apply`)이 된다.
  - 정의를 직접 재귀로 바꾸면 두 실행기가 함께 빨라진다.
- **시작 비용은 단계의 합이다.** .NET 시작, 명령줄 처리, 파싱, 타입 검사로 나눠 재면, 예상하지 못한 항목(Argu의 reflection)이 가장 클 수 있다.

## Why This Matters

- **평가기 프로파일에서는 prelude가 보이지 않는다.** 평가기 입장에서 prelude의 `fold`는 사용자 함수와 같다. 그래서 "클로저 호출이 많다"까지만 보이고, 그 호출이 어디서 오는지는 보이지 않는다.
- **짧은 정의가 비싸다.** 다음 정의는 읽기 쉽다.

  ```
  let append xs ys = fold (fun acc x -> x :: acc) ys (reverse xs)
  ```

  하지만 merge sort의 `append (reverse acc) ys`는 남은 리스트를 세 번 훑고, 원소마다 클로저를 세 번 부른다.
- **명령줄 라이브러리는 실행마다 비용을 낸다.** Argu는 `CliArgs` union을 reflection으로 읽어 parser를 만든다. 0.2초짜리 프로그램에서 0.055초다.

## Recognition Pattern

- 표준 라이브러리 일부를 그 언어 자체로 쓴 인터프리터나 컴파일러
- 벤치마크 하나만 유독 느리고, 그 프로그램이 라이브러리 함수(`append`, `map`)를 많이 쓸 때
- 빈 프로그램 실행 시간이 런타임의 hello world보다 훨씬 길 때(이 경우 0.12초와 0.02초)

## The Approach

### Step 1: 의심 가는 호출을 프로그램에서 바꿔 잰다

라이브러리를 고치기 전에, 벤치마크 프로그램에서 그 호출만 직접 재귀로 바꾼 사본을 만들어 잰다.

```
// msort.calc 의 merge 끝
| ([], _) -> append (reverse acc) ys          // 원래
| ([], _) -> revAppend acc ys                 // 사본: let rec revAppend xs ys = match xs with [] -> ys | x :: r -> revAppend r (x :: ys)
```

| 프로그램 | 평가 (데운 뒤) | 할당 |
|---|---|---|
| 리스트 만들기만 (`gen`) | 0.05초 | 88 MB |
| msort 원래 | 1.80초 | 3.9 GB |
| msort, `revAppend` 사본 | 1.05초 | 2.3 GB |

0.75초(42%)가 `append (reverse acc)`였다. 그래서 msort만 고치지 않고 prelude를 고친다. 같은 패턴을 쓰는 모든 프로그램이 빨라진다.

### Step 2: prelude 정의를 직접 재귀로 바꾸고, 보조 함수는 숨긴다

```
let reverse xs =
    let rec go xs acc =
        match xs with
        | [] -> acc
        | x :: rest -> go rest (x :: acc)
    go xs []

let map f xs =
    let rec go xs acc =
        match xs with
        | [] -> reverse acc
        | x :: rest -> go rest (f x :: acc)
    go xs []
```

- **지역 `go`를 쓰는 이유:** `revAppend`를 prelude에 두면 사용자에게 새 이름이 생긴다. 지역 함수로 두면 공개된 이름이 바뀌지 않는다.
- **지킬 것:**
  - 결과
  - `f`를 부르는 순서(앞 원소부터, 출력이 보이는 함수에서 중요)
  - tail recursion (긴 리스트에서 스택이 자라지 않음)
- **의존 관계:** prelude가 "쓰는 정의만 붙이는" 방식이라면 의존 관계가 바뀐다(`map`이 더 이상 `fold`를 붙이지 않음). 이것을 확인하는 테스트와 문서도 고친다.
- **컴파일러에도 효과가 있다:** `fold`에 넘긴 클로저는 컴파일러에서 indirect call이다. 직접 재귀의 `go`는 알려진 함수라 direct call이 된다. 그래서 컴파일한 msort도 0.29초에서 0.20초가 되었다.

### Step 3: 시작 비용을 프로세스 안에서 단계별로 잰다

평가기 프로젝트를 참조하는 작은 콘솔 프로그램에서 `main`이 하는 일을 차례로 부르고 `Stopwatch`로 잰다.

```fsharp
let t0 = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds   // 프로세스 시작 → main
let parser = Argu.ArgumentParser.Create<Program.CliArgs>(programName = "calc")
let _ = parser.ParseCommandLine [| path |]                                      // lap "Argu"
let ast = Frontend.parse src                                                    // lap "parse"
...
```

| 단계 | 시간 |
|---|---|
| 프로세스 시작 → `main` | 0.017초 |
| **Argu** | **0.055초** |
| 프로그램 파싱 (렉서, 파서 JIT 포함) | 0.016초 |
| prelude 붙이기 | 0.010초 |
| 타입 검사 | 0.012초 |

프로세스 밖에서도 확인한다. `calc --version`(0.073초)과 F# hello world(0.020초)의 차이가 Argu와 맞는다.

### Step 4: 흔한 경우만 라이브러리를 건너뛴다

```fsharp
let main argv =
    match argv with
    | [| path |] when not (path.StartsWith "-") -> runFile true (System.IO.File.ReadAllText path)
    | _ ->
    let parser = ArgumentParser.Create<CliArgs>(programName = "calc")
    ...
```

- **라이브러리를 바꾸지 않는다:** `--help` 출력, 오류 메시지, 다른 옵션의 동작은 그대로 Argu가 맡는다.
- **빠른 경로는 기존 함수를 부른다:** 빠른 경로가 부르는 `runFile`은 Argu 경로가 부르는 함수와 같다. 그래서 동작이 갈라지지 않는다.
- **테스트:** `Program.main [| path |]`와 `Program.main [| "--no-typecheck"; path |]`의 출력이 같은지 확인한다. `Console.SetOut`으로 출력을 받으므로 `testSequenced`로 다른 테스트와 겹치지 않게 한다.

### Step 5: 피연산자가 상수나 지역 변수인 연산은 전용 클로저로

```fsharp
type private Operand =
    | OConst of int          // Number n
    | OLocal of int          // 지금 함수의 칸 (바깥 함수의 변수는 parentOf 를 거치므로 OCode)
    | OCode of Code

let inline private arith (a: Operand) (b: Operand) ([<InlineIfLambda>] op: int -> int -> int) : Code =
    match a, b with
    | OLocal s, OConst k ->
        fun fr ->
            match slotOf fr s with
            | VInt x -> ofInt (op x k)
            | v -> typeError "int" v
    | ...
    | _ -> 일반 경로
```

- **효과:** `n - 1`은 클로저 세 번(연산, `n`, `1`)이 한 번이 된다. 빈 반복이 36%, fib가 11% 빨라졌다.
- **조합은 자주 쓰는 것만:** (지역, 상수), (지역, 지역), (식, 상수)만 두고 나머지는 일반 경로로 보낸다.
- **같은 오류:** 실패할 때의 오류는 일반 경로와 같은 순서와 문장으로 맞춘다.

## Example

넣지 않은 후보도 재서 기록했다(`docs/benchmarks.md`).

| 후보 | 효과 | 넣지 않은 이유 |
|---|---|---|
| 튜플을 `Value list` 대신 `Value[]`로 | bst -5% | 테스트의 `VTuple [...]` 44곳 |
| `match (a, b) with`에서 튜플을 만들지 않음 | msort -2% | 효과가 작음 |
| `gcServer`와 heap 2개 | msort -10%, bst -22% | 메모리 3배 |

## 체크리스트

- [ ] 느린 벤치마크에서 라이브러리 호출만 바꾼 사본을 만들어 그 몫을 쟀는가?
- [ ] 언어로 쓴 라이브러리 정의가 원소마다 클로저를 부르지 않는가? (`fold`에 함수 넘기기)
- [ ] 라이브러리를 고칠 때 결과, 함수 호출 순서, tail recursion, 공개된 이름을 지켰는가?
- [ ] 라이브러리의 의존 관계를 확인하는 테스트와 문서를 고쳤는가?
- [ ] 컴파일러가 같은 라이브러리를 쓴다면 컴파일 결과도 다시 쟀는가?
- [ ] 시작 비용을 프로세스 안에서 단계별로 쟀는가?
- [ ] 큰 항목을 건너뛰는 빠른 경로가 기존 경로와 같은 함수를 부르는가?

## 관련 문서

- `52-write-standard-library-as-prelude-in-the-language.md` - 언어로 쓴 prelude와 `Prelude.wrap`
- `69-find-why-interpreter-is-slower-than-python.md` - 측정 방법
- `70-cut-allocations-and-jit-delay-in-dotnet-interpreter.md` - 할당과 JIT 대기 줄이기
