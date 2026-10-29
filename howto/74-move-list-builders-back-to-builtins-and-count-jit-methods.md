---
order: 74
created: 2026-10-29
commit: c91cb69
version: 0.48.0
description: 표준 라이브러리를 언어로 쓰는 원칙에도 예외가 있다. 함수 인자 없이 리스트를 만들기만 하는 reverse, append, range는 원소마다 해석할 까닭이 없어 내장 함수로 되돌리면 두 실행기가 함께 빨라진다. 시작 비용은 JIT이 컴파일하는 메서드 수를 세어 보고, ReadyToRun은 빌드를 바꾸지 않는 선택으로 둔다
---

# 리스트를 만드는 함수는 내장 함수로, 시작 비용은 JIT 메서드 수로

> 관련 커밋: [c91cb69](https://github.com/ohama/calc-mlir/commit/c91cb69) (v0.48.0)

0.30.0에서 `map`, `range`, `reverse` 같은 리스트 함수를 Calc로 쓴 prelude로 옮겼다(`52-write-standard-library-as-prelude-in-the-language.md`). 인터프리터, 타입 추론, 컴파일러가 같은 정의 하나를 쓰게 하려는 것이었다. 0.47.0까지 평가기를 다듬고 나니(`73-reduce-long-lived-objects-not-short-lived-ones.md`) 두 가지가 남았다.

- **prelude의 일부:** merge sort 평가 시간의 33%가 prelude의 `append (reverse acc) ys`였다.
- **JIT:** 빈 프로그램도 0.071초가 걸렸다. JIT이 메서드 1,531개를 컴파일하기 때문이었다.

| `benchmarks/run.py` (초) | CPython | 0.47.0 | 0.48.0 | 0.48.0 ReadyToRun |
|---|---|---|---|---|
| merge sort 20만, 인터프리터 | 0.206 | 1.001 | 0.671 | 0.609 |
| merge sort 20만, 실행 파일 | 0.206 | 0.200 | 0.170 | |
| fib 32, 인터프리터 | 0.136 | 0.147 | 0.148 | 0.112 |
| 빈 프로그램, 인터프리터 | 0.014 | 0.071 | 0.071 | 0.035 |

## The Insight

**"표준 라이브러리는 그 언어로" 원칙의 비용은 함수마다 다르다.**

| 함수의 종류 | 예 | 언어로 썼을 때의 비용 |
|---|---|---|
| 함수 인자를 받음 | `map`, `filter`, `fold`, `exists` | 원소마다 어차피 사용자 함수를 부름. 언어로 써도 거의 같음 |
| 리스트를 만들기만 함 | `reverse`, `append`, `range` | 원소마다 해석 단계를 거침. 내장 함수면 F#/C 반복 한 번 |

뒤쪽은 결과가 정해진 구조 연산이라, 내장 함수로 바꿔도 의미가 달라질 곳이 거의 없다. 그래서 이 셋만 내장 함수로 되돌렸다.

**시작 비용은 JIT이 무엇을 컴파일하는지 세면 보인다.** profiler의 sample은 시작 몇십 ms에서 너무 적다. 대신 런타임이 컴파일한 메서드 목록을 받아 어느 어셈블리, 어느 모듈인지 센다.

## Why This Matters

- **원칙을 그대로 지키면 뜨거운 곳에서 계속 손해를 본다.** prelude의 `append`는 tail recursion을 지키려고 `reverse`한 뒤 다시 붙인다. 그래서 `append (reverse acc) ys` 하나가 리스트 칸을 3n개 만들고, 원소마다 해석 단계를 거친다. 인터프리터만이 아니라 컴파일한 코드도 같은 정의를 실행한다.
- **시작 비용의 원인이 엉뚱한 곳에 있다.** 빈 프로그램인데 JIT이 컴파일한 메서드의 39%(596개)가 파서였다. 사용자 프로그램이 아니라 prelude를 실행할 때마다 파싱하기 때문이다.

## Recognition Pattern

- 표준 라이브러리 일부를 그 언어로 쓴 구현에서, 프로그램 시간의 상당 부분이 라이브러리 함수일 때
- 그 함수가 함수 인자를 받지 않고 자료 구조만 만들거나 바꿀 때
- .NET 프로그램의 빈 실행이 hello world(0.02초)보다 훨씬 오래 걸릴 때

## The Approach

### Step 1: 라이브러리 호출의 몫을 잰다

벤치마크에서 의심 가는 호출만 직접 쓴 코드로 바꾼 사본을 만들어, 데운 뒤의 평가 시간을 비교한다.

```
| ([], _) -> append (reverse acc) ys      // 원래
| ([], _) -> revAppend acc ys             // 사본 (리스트를 한 번만 훑음)
```

| 프로그램 | 평가 | 할당 |
|---|---|---|
| msort 원래 | 0.930초 | 3.1 GB |
| msort `revAppend` 사본 | 0.627초 | 2.0 GB |

### Step 2: 바꿀 함수를 고른다: 함수 인자가 없고, 결과가 구조로 정해지는 것

`reverse`, `append`, `range`는 다음 조건을 모두 만족한다.

- **부수 효과가 없다:** 함수를 부르지 않으므로 실행 순서가 결과에 드러나지 않는다.
- **타입이 그대로다:** `'a list -> 'a list`, `'a list -> 'a list -> 'a list`, `int -> int -> int list`. 내장 함수의 타입 표에 같은 타입을 넣으면 타입 추론 결과가 같다.
- **스택이 자라지 않는다:** F#의 `List.rev`와 `List.append`는 반복이다. C 구현도 반복으로 쓴다.

`map`, `filter`, `fold`는 언어로 둔다. 원소마다 사용자 함수를 부르므로 내장 함수로 바꿔도 이득이 작다.

### Step 3: 모든 실행기에 같은 의미로 넣는다

```fsharp
// Eval.fs (인터프리터)
| "reverse", [ xs ] -> VList (List.rev (list' xs))
| "append", [ xs; ys ] ->
    let xs = list' xs
    VList (List.append xs (list' ys))
| "range", [ a; b ] ->                      // 끝값이 int 의 끝이어도 멈추도록 64비트로 셈
    let a, b = int64 (int' a), int64 (int' b)
    ...

// Infer.fs (타입)
"reverse", listOf a ^-> listOf a
"append", listOf a ^-> listOf a ^-> listOf a
"range", tInt ^-> tInt ^-> listOf tInt
```

```c
/* runtime/builtins.c (컴파일러). xs 의 원소를 새 칸에 담고 마지막 칸을 ys 에 이음 (ys 는 공유) */
value calc_builtin_append(value xs, value ys) {
    if (xs == CALC_NIL) return ys;
    value first = calc_cons(HEAD(xs), ys), last = first;
    for (xs = TAIL(xs); xs != CALC_NIL; xs = TAIL(xs)) {
        value cell = calc_cons(HEAD(xs), ys);
        TAIL(last) = cell;
        last = cell;
    }
    return first;
}
```

- **경계값:** prelude의 `range a b`는 `a = -2147483648`이면 `i - 1`이 한 바퀴 돌아 멈추지 않았다. 내장 함수는 64비트로 세서 멈춘다. 이런 경계는 두 실행기 모두에 테스트로 넣는다(`range 2147483646 2147483647`).
- **바뀌는 것을 기록한다:**
  - `--no-typecheck`로 잘못 쓰면 오류 메시지가 바뀐다. `range 1 true`는 prelude의 `<`가 내던 `expected int or string` 대신 `expected int`다.
  - prelude 의존 관계 테스트(`map`이 붙이는 정의)의 기대값도 바뀐다.
  - 이 둘은 CHANGELOG에 적는다.
- **문서의 개수:** 내장 함수와 prelude 함수의 개수가 바뀐다. 문서 여러 곳의 개수를 다시 센다.

### Step 4: JIT이 컴파일하는 메서드를 센다

```bash
DOTNET_JitStdOutFile=jit.txt DOTNET_JitDisasmSummary=1 calc empty.calc
```

`jit.txt`의 한 줄은 `JIT compiled Parser:_fsyacc_reduction_123(...) [Tier0, IL size=...]` 모양이다. 메서드 이름의 앞부분으로 묶어 센다.

| 묶음 | 메서드 수 |
|---|---|
| `Parser` (fsyacc의 규칙 동작) | 596 |
| FSharp.Core | 283 |
| System.* | 157 |
| `LayoutFilter` | 130 |
| FsLexYacc 런타임 | 110 |
| 합계 | 1,531 |

메서드 하나의 Tier0 컴파일은 짧지만, 1,500개면 수십 ms다.

### Step 5: ReadyToRun은 빌드를 바꾸지 않는 선택으로 둔다

```bash
dotnet publish src/Calc -c Release -r osx-arm64 -p:PublishReadyToRun=true -o dist
CALC=dist/calc python3 benchmarks/run.py
```

- **효과:** 미리 컴파일해 두므로 시작 비용이 반이 된다(0.071초 → 0.035초). 첫 실행의 Tier0 코드도 R2R 코드로 바뀌어, 긴 프로그램도 0.06~0.09초 빨라진다.
- **비용:** RID를 정해서 publish해야 하고 `dotnet build`로는 만들어지지 않는다. `Calc.dll`이 0.9MB에서 2.3MB가 된다.
- **둔 방법:** 그래서 기본 빌드와 테스트는 그대로 두고, `docs/build.md`에 "빠른 실행 파일"로 적었다. `dist/`는 `.gitignore`에 넣었다.
- **측정 스크립트:** `run.py`는 프로그램을 `benchmarks/` 안에서 실행한다. 그래서 `CALC`를 실행한 위치 기준의 절대 경로로 바꿔 둔다(`os.path.abspath`).

## Example

| 시험 | 결과 | 넣었나 |
|---|---|---|
| `reverse`, `append`, `range`를 내장 함수로 | msort 인터프리터 -33%, 실행 파일 -15% | 넣음 |
| ReadyToRun publish | 시작 -50%, 모든 예제 -8~24% | 선택으로 문서화 |
| 지역 변수 인자를 칸에서 바로 읽기 | 측정 오차 수준 | 넣지 않음 |
| `gcConcurrent=0` | bst -10%, 최대 메모리 2~2.4배 | 넣지 않음 |

## 체크리스트

- [ ] 라이브러리 호출의 몫을 직접 쓴 사본으로 쟀는가?
- [ ] 내장 함수로 바꿀 함수가 함수 인자 없이 구조만 만드는가?
- [ ] 인터프리터, 타입 표, 컴파일러 런타임에 같은 의미로 넣었는가?
- [ ] 경계값(빈 리스트, `b < a`, `int`의 끝)을 두 실행기 모두에서 테스트했는가?
- [ ] 바뀌는 오류 메시지와 의존 관계를 CHANGELOG에 적었는가?
- [ ] 문서의 함수 개수를 다시 셌는가?
- [ ] 시작 비용을 JIT 메서드 수(`DOTNET_JitDisasmSummary`)로 나눠 보았는가?
- [ ] ReadyToRun을 넣는다면 기본 빌드와 테스트를 바꾸지 않는 방법을 골랐는가?

## 관련 문서

- `52-write-standard-library-as-prelude-in-the-language.md` - prelude를 언어로 쓴 이유
- `71-find-costs-in-prelude-and-startup.md` - prelude를 직접 재귀로, 시작 비용 나누기
- `72-profile-fsharp-interpreter-and-read-its-il.md` - profiler와 IL
- `73-reduce-long-lived-objects-not-short-lived-ones.md` - GC와 오래 사는 객체
