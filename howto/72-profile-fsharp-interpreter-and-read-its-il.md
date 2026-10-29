---
order: 72
created: 2026-10-29
commit: 46ac4db
version: 0.46.0
description: F# 인터프리터는 dotnet-trace로 함수별 self time을 재고, 뜨거운 클로저는 IL에서 실제로 부르는 메서드를 확인한다. 시간의 상당 부분이 .NET 형 검사 helper였다. args[i] fr는 배열 인덱스 함수 값과 InvokeFast로 컴파일되고, 인자 둘인 클로저 호출과 obj[] 저장도 호출마다 형 검사를 한다
---

# F# 인터프리터를 profiler와 IL로 들여다보기

> 관련 커밋: [46ac4db](https://github.com/ohama/calc-mlir/commit/46ac4db) (v0.46.0)

0.42.0부터 0.45.0까지는 측정과 짐작으로 후보를 골랐다(`69-find-why-interpreter-is-slower-than-python.md`, `70-cut-allocations-and-jit-delay-in-dotnet-interpreter.md`, `71-find-costs-in-prelude-and-startup.md`). 0.46.0에서는 sampling profiler로 함수별 시간을 재고, 뜨거운 클로저의 IL을 읽었다. 시간의 상당 부분이 Calc 코드가 아니라 .NET 런타임의 형 검사 helper였다.

| `benchmarks/run.py` (시작 포함) | CPython | 0.45.0 | 0.46.0 |
|---|---|---|---|
| merge sort 20만 | 0.207초 | 1.607초 | 1.035초 |
| BST 20만 | 0.349초 | 1.323초 | 0.957초 |
| N-Queens 10 | 0.043초 | 0.167초 | 0.129초 |
| fib 32 | 0.136초 | 0.160초 | 0.145초 |

## The Insight

**F# 코드 한 줄이 어떤 IL이 되는지는 읽어 보기 전에는 모른다.** profiler에 `CastHelpers.IsInstanceOfClass`나 `FSharpFunc.InvokeFast`가 크게 보이면, 소스에는 없는 형 검사가 호출마다 일어난다는 뜻이다. 이 프로젝트에서 찾은 것은 세 가지였다.

| F# 코드 | 실제로 일어나는 일 |
|---|---|
| `args[i] fr` (`args: (Frame -> Value)[]`) | 배열 인덱스를 함수 값으로 만들고 `InvokeFast(getter, args, i, fr)`로 부름. 그 안에서 형 검사 |
| `m v fr` (`m: Value -> Frame -> bool`) | `InvokeFast`가 `m`이 인자 둘을 받는 클로저인지 형 검사한 뒤 부름 |
| `fr[i] <- v` (`fr: obj[]`, `Value[]`) | 배열 공변성(covariance) 검사 `StelemRef` |

셋 다 고치는 방법은 짧다.

| 고친 코드 | 효과 |
|---|---|
| `(args[i]) fr` | 꺼낸 함수를 바로 `Invoke` |
| `Adapt`해 둔 `m.Invoke(v, fr)` | 형 검사 없이 virtual call 한 번 |
| struct 칸의 배열 `fr[i].Item <- v` | struct field에 쓰므로 공변성 검사가 없음 |

## Why This Matters

- **짐작으로는 찾지 못한다.** `args[i] fr`는 정상적인 F# 문법이고, 결과도 맞다. 이 형 검사 helper들은 msort, queens, bst 평가 시간의 35~60%였다.
- **소스만 보면 엉뚱한 곳을 고친다.** profiler가 `InvokeFast`를 보여 주었을 때 처음에는 `inline` 함수(`setSlot`)가 펼쳐지지 않았다고 짐작했다. 고쳐 보니 효과가 없었다. IL을 꺼내 보고서야 배열 인덱스였다는 것을 알았다.

## Recognition Pattern

- F#으로 쓴 인터프리터, 클로저 컴파일 평가기, 콜백이 많은 코드
- profile의 상위에 `System.Runtime.CompilerServices.CastHelpers.*`, `FSharpFunc.InvokeFast`, `StelemRef`가 있을 때
- 함수 값을 배열이나 리스트에 넣고 `xs[i] arg`로 부르는 코드

## The Approach

### Step 1: dotnet-trace를 프로젝트 밖에 설치하고 trace를 뜬다

```bash
dotnet tool install dotnet-trace --tool-path <임시 폴더>/tools      # 전역에 설치하지 않음
<임시 폴더>/tools/dotnet-trace collect --format speedscope -o msort.nettrace -- calc msort.calc
```

- **실행 시간:** 1~2초 이상 돌도록 입력을 키운다(`fib 35`, `place 11`). 0.1초짜리는 sample이 모자란다.
- **결과 파일:** `msort.speedscope.json`이 생긴다. speedscope 웹 앱으로 열어도 되고, 아래처럼 스크립트로 집계해도 된다.

### Step 2: 평가하는 스레드를 고르고, 가짜 frame을 건너뛰어 self time을 센다

speedscope 파일에는 스레드마다 profile이 있다. 큰 스택 스레드에서 평가하는 인터프리터라면 main 스레드는 대부분 기다리는 시간이다. 그래서 event 수가 가장 많은 스레드를 고른다. leaf에는 `CPU_TIME`, `UNMANAGED_CODE_TIME`이라는 가짜 frame이 붙으므로 그 바로 위의 frame에 시간을 준다.

```python
p = max(d["profiles"], key=lambda p: len(p.get("events", [])))     # 평가 스레드
...
real = [i for i in stack if frames[i]["name"] not in ("CPU_TIME", "UNMANAGED_CODE_TIME")]
self_time[frames[real[-1]]["name"]] += t - last                     # evented: O(열기)/C(닫기) 사이 시간
```

| 0.45.0 msort의 self time | 비중 |
|---|---|
| `CastHelpers.IsInstanceOfClass` | 26% |
| `FSharpFunc<_, int>.InvokeFast` | 12% |
| `CastHelpers.StelemRef` | 12% |
| `Eval+compile@755-14.Invoke` | 8% |
| `CastHelpers.ChkCastClassSpecial` | 6% |

- **이름 읽기:** `Eval+compile@755-14`는 `Eval.fs` 755줄에서 만든 클로저다.
- **호출한 쪽:** 같은 방법으로 leaf의 바로 위 frame을 세면 helper를 누가 부르는지 알 수 있다.
- **주의:** tail call(`<Tailcalls>true</Tailcalls>`)이 있으면 호출한 쪽 frame이 사라져서, 호출 관계가 한 단계씩 어긋날 수 있다.

### Step 3: 뜨거운 클로저의 IL에서 부르는 메서드를 꺼낸다

```fsharp
// il.fsx: dotnet fsi il.fsx Calc.dll "compile@767-14"
let asm = Assembly.LoadFrom path
for t in asm.GetTypes() do
    if t.FullName.EndsWith name then
        for m in t.GetMethods(BindingFlags.Instance ||| BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly) do
            let il = m.GetMethodBody().GetILAsByteArray()
            // 0x28 call, 0x6F callvirt, 0x73 newobj 뒤의 4바이트 token 을 t.Module.ResolveMethod 로 풀어 출력
```

```
== Eval+compile@767-14.Invoke
   call ArrayModule :: Slot[] ZeroCreate[Slot](Int32)
   call FSharpFunc`2[FSharpFunc`2[Slot[],Value][],Int32] :: Value InvokeFast[Slot[],Value](FSharpFunc`2[FSharpFunc`2[Slot[],Value][],FSharpFunc`2[Int32,FSharpFunc`2[Slot[],Value]]], FSharpFunc`2[Slot[],Value][], Int32, Slot[])
```

IL의 `InvokeFast`는 `(Code[] -> int -> Frame -> Value)` 모양의 함수 값을 받는다. 이것은 배열 인덱스를 함수 값으로 만든 것이다. 소스에서 이 모양을 모두 찾는다.

```bash
grep -nE "[A-Za-z_]+\[[^]]+\] [a-z(]" src/Calc/Eval.fs
```

### Step 4: 형 검사를 피하도록 고친다

```fsharp
// 1. 배열에서 꺼낸 함수는 괄호로 감싸서 부름 (지우면 다시 느려지므로 주석을 남김)
setSlot frame i ((args[i]) fr)

// 2. 인자 둘인 클로저는 한 번 Adapt 해 두고 Invoke
type private Matcher = OptimizedClosures.FSharpFunc<Value, Frame, bool>
let rec private compilePattern scope pattern : Matcher =
    OptimizedClosures.FSharpFunc<_, _, _>.Adapt (patternFunction scope pattern)
... h.Invoke(x, fr) && t.Invoke(VList rest, fr)

// 3. Frame 은 struct 칸의 배열: 칸에 쓸 때 공변성 검사가 없음
[<Struct; NoEquality; NoComparison>]
type Slot =
    val mutable Item: obj
type Frame = Slot[]
let inline private setSlot (fr: Frame) (slot: int) (v: Value) = fr[slot + 1].Item <- v
```

- **`Adapt`:** `fun v fr -> …`로 만든 클로저는 이미 `OptimizedClosures.FSharpFunc<_,_,_>`의 하위 클래스다. 그래서 `Adapt`는 형 검사 한 번 뒤에 같은 객체를 돌려준다. 이 검사는 패턴을 컴파일할 때 한 번만 일어난다.
- **struct 칸:** `obj[]`에 `Value`를 쓰면 JIT이 배열의 실제 원소 형을 확인해야 한다. struct field에 쓰는 것은 일반 저장이다. 범위 검사는 그대로 남는다.
- **효과를 따로 잰다:** 같은 측정 프로젝트로 하나씩 고쳐 데운 뒤의 평가 시간을 잰다.

| 변경 | queens | msort | bst |
|---|---|---|---|
| 0.45.0 | 0.085 | 1.420 | 1.172 |
| 패턴 클로저 `Adapt` + `Invoke` | 0.076 | 1.264 | 1.001 |
| struct 칸 Frame | 0.076 | 1.204 | 1.013 |
| `(args[i]) fr` | 0.082 | 1.037 | 0.934 |

### Step 5: 다시 profile을 떠서 다음 병목을 본다

형 검사가 사라지자 다른 항목이 보였다.

- **bst:** 튜플을 만드는 `ListCollector`가 약 33%였다. 그래서 원소 둘, 셋은 `VTuple [ x; y; d fr ]`로 바로 만들었다.
- **queens, msort:** `match xs with [] -> … | x :: rest -> …`의 패턴 함수 호출이 컸다. 이 모양은 전용 코드로 바꿨다.
- **IL 다시 보기:** 고친 뒤 `InvokeFast`가 남은 곳은 `failwithf` 같은 오류 경로와, 쓰이지 않는 `inline` 함수의 원본 클로저뿐이었다.

## Example

| profile에 보이는 것 | 의심할 F# 코드 | 고치는 방법 |
|---|---|---|
| `FSharpFunc<A[], int>.InvokeFast` | `xs[i] arg` | `(xs[i]) arg` |
| `FSharpFunc<A, B>.InvokeFast` + `IsInstanceOfClass` | 인자 둘 이상인 함수 값을 `f a b`로 부름 | `OptimizedClosures.FSharpFunc<_,_,_>.Adapt` 후 `.Invoke(a, b)` |
| `CastHelpers.StelemRef` | 봉인되지 않은 클래스(F# union 포함)나 `obj`의 배열에 저장 | struct 칸의 배열 |
| `ListCollector.Add` | `[ for x in xs -> f x ]`를 뜨거운 경로에서 씀 | 길이가 정해져 있으면 리스트 리터럴 |

## 체크리스트

- [ ] 1~2초 이상 도는 입력으로 trace를 떴는가?
- [ ] 평가하는 스레드를 골랐고, `CPU_TIME` 같은 가짜 frame을 건너뛰었는가?
- [ ] profile 상위에 `CastHelpers`, `InvokeFast`, `StelemRef`가 있는지 보았는가?
- [ ] 뜨거운 클로저의 IL에서 부르는 메서드를 꺼내 짐작을 확인했는가?
- [ ] `xs[i] arg` 모양을 모두 찾아 `(xs[i]) arg`로 바꾸고 이유를 주석으로 남겼는가?
- [ ] 고친 뒤 IL에서 `InvokeFast`가 오류 경로에만 남았는가?
- [ ] 고칠 때마다 다시 profile을 떠서 다음 병목을 보았는가?

## 관련 문서

- `69-find-why-interpreter-is-slower-than-python.md` - 측정 프로젝트와 단계별 시간
- `70-cut-allocations-and-jit-delay-in-dotnet-interpreter.md` - 객체 하나인 Frame, 조건식의 bool 경로
- `71-find-costs-in-prelude-and-startup.md` - prelude와 시작 비용
- `68-speed-up-ast-interpreter-with-slot-frames.md` - 칸 배열 환경과 F# 클로저
