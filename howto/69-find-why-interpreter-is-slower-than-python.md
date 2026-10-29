---
order: 69
created: 2026-10-29
commit: f5ef87d
version: 0.43.0
description: 인터프리터가 CPython보다 느리면 전체 시간을 시작 비용, JIT 데우기, 평가, GC pause, 할당량으로 나누고, 연산 하나의 비용을 Python과 마이크로 벤치마크로 비교한다. 원인 후보는 복사본에서 하나씩 고쳐 효과를 잰다. 이 인터프리터는 함수 호출이 아니라 내장 함수 호출 경로와 할당이 느렸다
---

# 인터프리터가 Python보다 느린 이유 찾기

> 관련 커밋: [f5ef87d](https://github.com/ohama/calc-mlir/commit/f5ef87d) (v0.43.0)

예제 다섯 개(`benchmarks/`)를 CPython 3.15, Calc 인터프리터, Calc 컴파일러로 실행했다. 인터프리터가 CPython보다 3~14배 느렸다. 인터프리터는 0.42.0에서 이미 F# 클로저와 칸 배열 환경으로 바꾼 상태였다(`68-speed-up-ast-interpreter-with-slot-frames.md`). 그래서 "AST 인터프리터라 느리다"는 설명으로는 무엇을 고칠지 알 수 없었다.

## The Insight

**전체 시간 하나로는 원인을 알 수 없다. 나누어 재야 한다.**

- **단계로 나눈다:** 시작, JIT 데우기, 평가, GC pause로 나누고 할당량도 잰다. 프로그램마다 큰 항목이 다르다.
- **연산 하나의 비용을 Python과 비교한다:** 같은 일을 반복하는 마이크로 벤치마크를 쓴다.
  - 이 인터프리터는 함수 호출 한 번이 CPython과 같았다(약 33ns).
  - 차이는 CPython에서 bytecode 하나인 일이 Calc에서는 내장 함수 호출이라는 데서 났다(배열 인덱스, `ref` 읽기와 쓰기).
- **원인 후보는 고쳐 보고 잰다:** 복사본에서 하나씩 고치고 효과를 표로 남긴다. 추정한 원인과 실제 효과가 달랐다. `VBool`을 공유해도 5%였고, 내장 함수 경로를 고치니 sieve가 5.8배 빨라졌다.

## Why This Matters

- **시작 비용이 섞인다.** `calc`는 아무것도 안 하는 프로그램도 0.12초가 걸린다(CPython 0.014초). 일이 작은 예제에서는 배율이 시작 비용으로 정해진다.
- **.NET JIT이 섞인다.** 처음에는 최적화하지 않은 코드로 실행한다. 같은 프로세스에서 두 번째로 평가하면 0.06~0.16초가 빨랐다.
- **GC와 할당이 숨는다.** msort는 평가 한 번에 7.2GB를 할당했다. GC pause는 그중 0.63초(24%)였다.
- **짐작으로 고치면 헛수고가 된다.** 비교 결과를 새로 만드는 `VBool` 할당이 의심스러웠지만, 고쳐 보니 효과는 작았다.

## Recognition Pattern

- 인터프리터나 VM이 비교 대상(CPython, 다른 구현)보다 느리고, 어디를 고칠지 정해야 할 때
- .NET, JVM처럼 JIT과 GC가 있는 런타임 위의 인터프리터를 잴 때
- 벤치마크 결과 배율이 예제마다 크게 다를 때(이 경우 1.4~14배)

## The Approach

### Step 1: 프로세스 밖에서 시작 비용을 나눈다

명령줄 옵션만 바꿔 실행해 뺄셈으로 나눈다. 이 프로젝트의 수치는 다음과 같았다.

| 실행 | 시간 | 들어 있는 것 |
|---|---|---|
| `calc --version` | 0.073초 | .NET 시작, 명령줄 처리 |
| `calc empty.calc` (`()` 한 줄) | 0.117초 | 위 + 파싱, prelude, 타입 검사 |
| `calc --no-typecheck fib.calc`와 `calc fib.calc`의 차이 | 0.05초 | 이 프로그램의 타입 검사 |

### Step 2: 같은 프로세스에서 단계별 시간, 할당량, GC를 잰다

컴파일러 프로젝트를 참조하는 작은 콘솔 프로젝트를 저장소 밖에 만든다. 그리고 평가를 여러 번 부른다.

```fsharp
let time label f =
    let a0 = GC.GetTotalAllocatedBytes true
    let p0 = GC.GetTotalPauseDuration()
    let gc0 = GC.CollectionCount 0
    let sw = Stopwatch.StartNew()
    let r = f ()
    printfn "%-10s %.3fs alloc %.1f MB gc0 %d pause %.3fs" label sw.Elapsed.TotalSeconds
        (float (GC.GetTotalAllocatedBytes true - a0) / 1e6) (GC.CollectionCount 0 - gc0)
        (GC.GetTotalPauseDuration() - p0).TotalSeconds
    r

for i in 1 .. 4 do
    time (sprintf "eval #%d" i) (fun () -> Frontend.runWithLargeStack (fun () -> Eval.evalWithOutput w ast) |> ignore)
```

- **첫 실행과 세 번째 실행의 차이**가 JIT 데우기다.
- **할당량을 반복 횟수로 나누면** 단계 하나의 비용이 나온다. 빈 반복 한 번이 112바이트였다.
  - `Frame`과 칸 배열: 64바이트
  - 새 `VInt`: 24바이트
  - 비교 결과 `VBool`: 24바이트

| 예제 | 시작 | JIT 데우기 | 평가 (데운 뒤) | GC pause | 할당량 |
|---|---|---|---|---|---|
| fib | 약 0.13 | 0.06 | 0.19 | 0.005 | 0.9 GB |
| sieve | 약 0.12 | 0.16 | 1.34 | 0.04 | 3.4 GB |
| msort | 약 0.13 | 0.10 | 2.63 | 0.63 | 7.2 GB |

### Step 3: 런타임 설정만 바꿔 본다

코드를 고치기 전에 환경 변수로 JIT과 GC의 몫을 확인한다.

```bash
DOTNET_TieredCompilation=0 calc msort.calc   # 처음부터 최적화: 시작이 느려져서 전체는 더 느림
DOTNET_gcServer=1 calc bst.calc              # bst 2.1초 → 1.6초 (메모리를 더 씀)
DOTNET_GCgen0size=0x10000000 calc msort.calc # gen0을 키우면 오히려 느림 (캐시)
```

설정으로 크게 바뀌지 않으면 원인은 코드에 있다.

### Step 4: 연산 하나를 Python과 비교한다

같은 반복 횟수(2백만)로 연산 하나만 다른 프로그램을 만든다.

```
let rec loop i = if i = 0 then 0 else loop (i - 1) in loop 2000000
let a = Array.create 10 1 in let rec loop i acc = if i = 0 then acc else loop (i - 1) (acc + a.[3]) in loop 2000000 0
let i = ref 2000000 in let c = ref 0 in (while !i > 0 do (c := !c + 1; i := !i - 1) done); !c
```

| 하는 일 | CPython | Calc 인터프리터 |
|---|---|---|
| 재귀 호출 `loop (i - 1)` | 32ns | 33ns |
| 빈 반복 | 8ns (`while`) | 33ns (재귀 호출) |
| `c += 1; i -= 1` 반복 | 14ns | 205ns (`ref`와 `while`) |
| 배열 원소 읽기 하나 | 약 3ns | 약 50ns |

함수 호출은 같고, 내장 함수 호출이 비쌌다. 코드를 보면 `a.[i] <- v` 하나가 다음 단계를 거쳤다.

1. 인자마다 `VBuiltin (name, args @ [ v ])`를 새로 만든다(partial application 세 번).
2. 인자 수를 `Map<string, int>`에서 찾는다.
3. `callBuiltin`의 `match name, args with`가 문자열을 차례로 비교한다. `.[]<-`는 28번째 경우다.

### Step 5: 복사본에서 하나씩 고쳐 효과를 잰다

`git archive HEAD src | tar -x -C <임시 디렉터리>`로 복사본을 만들고, Step 2의 측정 프로젝트가 그 복사본을 참조하게 한다. 각 행은 앞 행의 변경을 포함한다. 수치는 데운 뒤의 평가 시간(초)이다.

| 변경 | fib | sieve | msort | bst |
|---|---|---|---|---|
| 처음 | 0.189 | 1.344 | 2.634 | 1.834 |
| `VBool true`, `VBool false`를 하나씩 만들어 같이 씀 | 0.175 | 1.302 | 2.722 | 1.785 |
| 인자가 다 모인 내장 함수 호출을 바로 `callBuiltin` | 0.176 | 0.945 | 2.656 | 1.787 |
| `!`, `:=`, `.[]`, `.[]<-`는 전용 코드 | 0.175 | **0.231** | 2.631 | 1.789 |
| 튜플 패턴에서 `Array.ofList` 복사를 없앰 | 0.179 | 0.230 | **2.453** | **1.596** |
| (참고) `<Tailcalls>false</Tailcalls>` | 0.165 | 첫 실행 7초 | | |

- **tail call을 끄면** 빈 반복이 33ns에서 17ns가 된다. 그러나 반복마다 스택이 쌓여 512MB 스택을 처음 건드리는 비용이 커지므로 쓸 수 없다. 원인을 아는 것만으로도 다음 선택지(반복문을 따로 실행하기)가 보인다.
- **효과가 있는 것만 저장소에 넣는다.**

### Step 6: 빠른 경로는 같은 동작을 지키며 넣는다

```fsharp
and private saturatedBuiltin (scope: Scope) (head: Expr) (args: Code[]) : Code option =
    let builtin =
        match head with
        | Var name when not (scope.Vars.ContainsKey name) ->       // 사용자 변수가 가리면 쓰지 않음
            match Map.tryFind name scope.Consts with
            | Some (VBuiltin (n, [])) when not (Format.kinds.Contains n) && builtinArity[n] = args.Length -> Some n
            | _ -> None
        | _ -> None
    match builtin, args with
    | None, _ -> None
    | Some ":=", [| r; x |] ->
        Some (fun fr ->
            let r = r fr
            let x = x fr                     // 오른쪽을 먼저 계산한 뒤 타입 확인 (예전 경로와 같은 순서)
            match r with
            | VRef cell -> cell.Value <- x; VUnit
            | v -> typeError "ref" v)
    ...
    | Some name, _ -> Some (fun fr -> callBuiltin name [ for a in args -> a fr ])
```

- **언제 쓰는가:** 함수 위치가 범위의 변수가 아니고 인자 수가 맞을 때만 쓴다. 인자가 모자라거나 포맷 함수(`sprintf`)면 예전 경로를 쓴다.
- **계산 순서:** 인자를 모두 계산한 뒤 확인한다. 테스트는 타입 검사 없이 평가하므로 `1 := (print "x"; 2)`가 `x`를 출력한 뒤 오류가 나야 한다.
- **새 테스트:** 고치기 전과 후의 `Eval.fs`에서 모두 돌려 둘 다 통과하는지 본다. 그러면 테스트가 동작을 고정한다는 것을 확인할 수 있다.

## Example

`benchmarks/run.py`로 잰 전체 시간(초, 시작 포함)이다.

| 예제 | CPython | 0.42.0 | 0.43.0 |
|---|---|---|---|
| fib | 0.139 | 0.404 | 0.387 |
| sieve | 0.300 | 1.590 | **0.421** |
| msort | 0.206 | 2.961 | 2.753 |
| bst | 0.351 | 2.091 | 1.845 |

msort와 bst에 남은 원인은 할당이다. Python의 slicing과 `extend`는 C 안에서 돌고, 메모리는 reference count로 바로 다시 쓴다. Calc는 리스트를 F# 리스트와 `VList`로 두 번 감싸고, `x :: rest` 패턴이 맞을 때마다 `VList rest`를 만든다. 더 줄이려면 값 표현을 바꿔야 한다.

## 체크리스트

- [ ] 시작 비용(`--version`, 빈 프로그램)을 따로 쟀는가?
- [ ] 같은 프로세스에서 평가를 여러 번 불러 JIT 데우기를 뺐는가?
- [ ] 할당량과 GC pause(`GC.GetTotalAllocatedBytes`, `GC.GetTotalPauseDuration`)를 쟀는가?
- [ ] 연산 하나의 비용을 비교 대상과 같은 반복 횟수로 비교했는가?
- [ ] 원인 후보를 복사본에서 하나씩 고쳐 효과를 표로 남겼는가?
- [ ] 빠른 경로가 가리기, partial application, 계산 순서, 오류 메시지를 지키는가?
- [ ] 새 테스트가 고치기 전의 코드에서도 통과하는가?

## 관련 문서

- `68-speed-up-ast-interpreter-with-slot-frames.md` - Map 환경을 칸 배열과 F# 클로저로 바꾸기
- `13-implement-builtins-as-data-values.md` - 내장 함수와 `VBuiltin`의 partial application
- `53-enable-fsharp-tailcalls-in-debug-for-interpreters.md` - F#의 tail call과 인터프리터의 스택
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
