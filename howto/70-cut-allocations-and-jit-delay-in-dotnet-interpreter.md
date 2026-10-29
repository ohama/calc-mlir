---
order: 70
created: 2026-10-29
commit: 95e3f2b
version: 0.44.0
description: .NET 위의 클로저 인터프리터는 호출마다 몇 번 할당하는지가 속도를 정한다. Frame 객체와 칸 배열을 obj[] 하나로 합치고, 조건식은 VBool 없이 bool로 계산하고, 작은 정수는 미리 만들어 둔다. 짧게 끝나는 프로그램은 JIT의 call counting 지연을 0으로 한다. 후보는 하나씩 재고, 장단점이 있는 것은 넣지 않는다
---

# .NET 인터프리터에서 할당과 JIT 대기 줄이기

> 관련 커밋: [95e3f2b](https://github.com/ohama/calc-mlir/commit/95e3f2b) (v0.44.0)

이 인터프리터는 식을 F# 클로저(`Frame -> Value`)로 바꿔 실행한다(`68-speed-up-ast-interpreter-with-slot-frames.md`). 0.43.0에서 내장 함수 호출 경로를 고쳤다(`69-find-why-interpreter-is-slower-than-python.md`). 그 뒤에도 CPython보다 1.4~13배 느렸다. 후보 일곱 개를 복사본에서 하나씩 재고, 그중 다섯 개를 넣었다.

| 예제 (`benchmarks/run.py`, 시작 포함) | CPython | 0.43.0 | 0.44.0 |
|---|---|---|---|
| fib 32 | 0.136초 | 0.387초 | 0.231초 |
| N-Queens 10 | 0.043초 | 0.366초 | 0.217초 |
| merge sort 20만 | 0.206초 | 2.753초 | 1.962초 |
| BST 20만 | 0.350초 | 1.845초 | 1.383초 |

## The Insight

**.NET 위의 인터프리터는 단계 하나에 객체를 몇 개 만드는지가 속도를 정한다.** gen0 할당 자체는 싸다. 하지만 할당량이 많으면 수거가 자주 일어나고, 오래 사는 데이터(20만 개짜리 리스트)가 gen1, gen2로 옮겨지면서 비용이 커진다. 빈 반복 한 번에 112바이트를 만들던 것을 줄이는 쪽이 클로저 호출 몇 번을 줄이는 쪽보다 효과가 컸다.

- **호출마다 Frame 객체와 칸 배열** 두 개를 만들었다. `obj[]` 하나로 합치니 msort와 bst가 25~30% 빨라졌다.
- **비교 결과 `VBool`, 새 정수 `VInt`:** 조건 위치에서는 `VBool`을 만들지 않고, 작은 정수는 미리 만들어 둔다.

그리고 **짧은 프로그램은 JIT이 최적화를 시작하기 전에 끝난다.** .NET은 기본값으로 시작하고 100ms 동안 최적화 컴파일을 미룬다. 0.2초짜리 프로그램에서는 이 대기 시간이 실행 시간의 상당 부분이다.

## Why This Matters

- **gen0 할당이 싸다고 넘기기 쉽다.** 그러나 msort는 평가 한 번에 6.7GB를 할당했고, GC pause가 평가 시간의 약 25%였다.
- **`Value[]`를 가진 Frame 클래스**는 자연스러운 설계다. 하지만 할당이 두 번이고, 칸을 읽을 때 참조를 두 번 따라간다.
- **JIT 대기는 코드를 봐서는 보이지 않는다.** 같은 프로세스에서 두 번째 평가가 첫 번째보다 0.06~0.16초 빨랐다. 이 차이로 대기를 알 수 있다.

## Recognition Pattern

- F#/C#로 쓴 인터프리터, 클로저 컴파일 평가기, 바이트코드 VM에서 단계 하나의 할당량(`GC.GetTotalAllocatedBytes` ÷ 반복 횟수)이 수십 바이트를 넘을 때
- 값을 F# union으로 표현해서 `VInt n`, `VBool b`가 매번 새 객체일 때
- 1초 안팎에 끝나는 명령줄 도구를 .NET으로 만들었을 때

## The Approach

먼저 측정 도구를 갖춘다. 후보마다 같은 측정 프로젝트로 데운 뒤의 평가 시간과 할당량을 잰다(`69-find-why-interpreter-is-slower-than-python.md`의 Step 2). 각 행은 앞 행의 변경을 포함한다.

| 변경 | fib | queens | msort | bst |
|---|---|---|---|---|
| 0.43.0 | 0.176 | 0.117 | 2.508 | 1.686 |
| 작은 정수, 빈 리스트를 미리 만듦 | 0.156 | 0.116 | 2.550 | 1.613 |
| 조건식은 bool로 | 0.125 | 0.112 | 2.462 | 1.591 |
| 인자 하나인 호출도 바로 | 0.120 | 0.111 | 2.546 | 1.573 |
| Frame을 `obj[]` 하나로 | **0.100** | **0.079** | **1.740** | **1.190** |

### Step 1: Frame을 `obj[]` 하나로 합친다

```fsharp
// 전: 호출마다 객체 두 개
and Frame(size: int, parent: Frame) =
    member val Slots: Value[] = Array.zeroCreate size
    member _.Parent = parent

// 후: [바깥 Frame; 칸 0; 칸 1; …] 객체 하나
type Frame = obj[]

let inline private slotOf (fr: Frame) (slot: int) : Value = Unsafe.As<Value>(fr[slot + 1])
let inline private setSlot (fr: Frame) (slot: int) (v: Value) = fr[slot + 1] <- v
let inline private parentOf (fr: Frame) : Frame = Unsafe.As<obj[]>(fr[0])
let inline private newFrame (size: int) (parent: Frame) : Frame =
    let fr: obj[] = Array.zeroCreate (size + 1)
    fr[0] <- parent
    fr
```

- **`Unsafe.As`를 쓰는 이유:** `:?>` 형 변환은 꺼낼 때마다 형 검사를 한다. 0번에는 Frame만, 나머지에는 `Value`만 넣는다는 invariant를 지키면 검사를 건너뛸 수 있다. 이 invariant는 주석으로 남긴다.
- **함수 타입은 바꾸지 않는다:** `Frame`을 type abbreviation으로 두면 `Frame -> Value` 시그니처를 그대로 쓸 수 있다. 바꿀 곳은 `fr.Slots[i]`, `fr.Parent`, `Frame(n, p)`뿐이다.
- **효과:** msort의 할당이 6.7GB에서 3.9GB로 줄었다.

### Step 2: 조건식은 `VBool`을 거치지 않는다

`if a < b then …`는 `Less`가 `VBool`을 만들고 `If`가 그것을 다시 푸는 구조였다. 조건과 비교, 논리 연산만 bool을 돌려주는 함수(`Frame -> bool`)로 따로 컴파일한다.

```fsharp
and private test (scope: Scope) (expr: Expr) : Frame -> bool =
    match expr with
    | Less (a, b) -> orderTest (c a) (c b) (fun x y -> x < y) (fun n -> n < 0)
    | Equal (a, b) ->
        fun fr ->
            match a fr with
            | VInt x -> (match b fr with VInt y -> x = y | v -> typeError "int" v)   // valueEquals 와 같은 오류
            | x -> valueEquals x (b fr)
    | And (a, b) -> let a, b = test scope a, test scope b in fun fr -> a fr && b fr
    | _ -> let e = c expr in fun fr -> asBool e fr

| If (cond, t, e) -> let cond = test scope cond in …              // 조건
| Less _ | Equal _ | And _ … -> let t = test scope expr in fun fr -> ofBool (t fr)   // 값으로 쓸 때만 VBool
```

- **`[<InlineIfLambda>]`:** 비교 연산자 넷을 함수 인자로 넘기는 helper(`orderTest`)는 `inline`으로 만들고 연산자 인자에 이 attribute를 붙인다. 그래야 연산자마다 클로저 호출이 생기지 않는다.
- **같은 오류:** int 빠른 경로의 오류 메시지는 일반 경로(`compareValues`, `valueEquals`)의 메시지와 같게 맞춘다. 테스트는 타입 검사 없이 평가하므로 이 메시지까지 확인한다.

### Step 3: 작은 정수와 빈 리스트는 미리 만든다

```fsharp
let private smallInts = Array.init 1280 (fun i -> VInt (i - 256))
let inline private ofInt n = if uint32 (n + 256) < 1280u then smallInts[n + 256] else VInt n
let private vNil = VList []
```

- **범위 검사:** `uint32 (n + 256) < 1280u` 비교 하나로 -256~1023 범위를 확인한다. CPython의 작은 정수 캐시(-5~256)와 같은 생각이다.
- **같은 결과:** F# union의 `=`는 구조로 비교하므로, 미리 만든 값을 써도 결과는 같다.

### Step 4: JIT의 call counting 지연을 없앤다

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption Include="System.Runtime.TieredCompilation.CallCountingDelayMs" Value="0" />
</ItemGroup>
```

- **확인하는 순서:** 먼저 환경 변수 `DOTNET_TC_CallCountingDelayMs=0`으로 효과를 잰다. fib가 0.39초에서 0.31초, queens가 0.36초에서 0.26초가 되었다. 그다음 프로젝트에 넣고, 빌드된 `Calc.runtimeconfig.json`에 `configProperties`로 들어갔는지 본다.
- **같이 시험한 설정:**

  | 설정 | 결과 |
  |---|---|
  | `TieredCompilation=0` (처음부터 최적화) | 시작이 느려져서 전체가 더 느림 |
  | `TieredPGO=0` | 효과 없음 |
  | `InvariantGlobalization` | 효과 없음 |
  | gen0 크기 키우기 | 캐시 효율이 떨어져 느려짐 |

### Step 5: 장단점이 있는 후보는 넣지 않고 기록한다

| 후보 | 얻는 것 | 잃는 것 |
|---|---|---|
| ReadyToRun (`PublishReadyToRun`) | 시작 0.115초 → 0.061초 | `dotnet publish -r <RID>`가 필요해서 빌드 방법이 바뀜 |
| Calc의 tail call은 trampoline으로 처리하고 `<Tailcalls>false</Tailcalls>` | fib 33% (F#이 꼬리 위치의 모든 클로저 호출에 붙이는 `tail.` 비용이 없어짐) | 꼬리 호출만 도는 반복이 1.7배 느림, tail call이 아닌 재귀의 한계가 600만에서 400만 아래로 줄어듦 |

결정한 이유는 문서(`docs/benchmarks.md`)에 남긴다. 다음에 같은 후보를 다시 재지 않아도 된다.

## Example

빈 반복 한 번(`let rec loop i = if i = 0 then 0 else loop (i - 1)`)의 할당이다.

| | 0.43.0 | 0.44.0 |
|---|---|---|
| Frame | 객체 32B + 칸 배열 32B | `obj[]` 하나 32B |
| `i = 0`의 결과 | `VBool` 24B | 없음 (bool) |
| `i - 1` | `VInt` 24B | 1023 이하면 없음 |
| 합계 | 112B | 64B (2백만 번에 128MB) |

## 체크리스트

- [ ] 단계 하나의 할당량(전체 할당 ÷ 반복 횟수)을 쟀는가?
- [ ] 호출마다 만드는 객체가 하나인가? (Frame과 칸 배열을 합치기)
- [ ] `Unsafe.As`를 쓴다면 무엇을 어디에 넣는지 invariant를 주석으로 남겼는가?
- [ ] 조건 위치의 비교가 중간 bool 값을 만들지 않는가?
- [ ] 빠른 경로의 오류 메시지가 일반 경로의 메시지와 같은가? (타입 검사를 건너뛴 테스트)
- [ ] 1초 안팎의 프로그램이면 JIT 설정(`CallCountingDelayMs`)을 재 보았는가?
- [ ] 새 테스트가 고치기 전의 코드에서도 통과하는가?
- [ ] 넣지 않은 후보와 그 이유를 기록했는가?

## 관련 문서

- `68-speed-up-ast-interpreter-with-slot-frames.md` - 칸 배열 환경과 F# 클로저로 바꾸기
- `69-find-why-interpreter-is-slower-than-python.md` - 측정 방법과 내장 함수 호출 경로
- `53-enable-fsharp-tailcalls-in-debug-for-interpreters.md` - F#의 tail call과 인터프리터의 스택
