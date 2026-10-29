---
order: 73
created: 2026-10-29
commit: 777ce4d
version: 0.47.0
description: .NET 위의 인터프리터에서 GC pause는 오래 사는 객체의 수에 달려 있다. 짧게 사는 Frame을 다시 써서 할당을 0으로 만들어도 빨라지지 않았고, 트리 노드 하나의 객체를 6개에서 3개로 줄이자 GC pause와 시간이 함께 줄었다. 값 표현은 오래 사는 데이터의 객체 수를 기준으로 고른다
---

# 짧게 사는 객체가 아니라 오래 사는 객체를 줄이기

> 관련 커밋: [777ce4d](https://github.com/ohama/calc-mlir/commit/777ce4d) (v0.47.0)

0.46.0에서 형 검사 helper를 없앤 뒤(`72-profile-fsharp-interpreter-and-read-its-il.md`), msort와 bst에서는 GC pause가 평가 시간의 34~39%로 가장 큰 항목이 되었다. GC를 줄이려고 두 방향을 시험했다.

| 시험 | 줄인 것 | 할당 | 시간 |
|---|---|---|---|
| Frame 재사용 (Spare 최대 32개) | 짧게 사는 객체 | fib 282 MB → 0.1 MB | fib 그대로, bst 5% 느려짐 |
| 생성자 값과 튜플의 표현 | 오래 사는 객체 | bst 1.52 GB → 1.21 GB | bst 24% 빨라짐 |

## The Insight

**generational GC에서 비용은 할당한 바이트가 아니라 살아남는 객체를 따라간다.**

- **짧게 사는 객체:** gen0 수거는 살아 있는 객체만 훑는다. 그래서 바로 죽는 객체(호출 하나의 Frame, 패턴이 만든 임시 값)는 많이 만들어도 싸다.
- **오래 사는 객체:** 수거가 일어날 때마다 훑고, gen1, gen2로 복사한다. 비용은 그 객체의 개수와 크기를 따라간다. bst의 트리, msort의 리스트가 여기에 해당한다.

그래서 값 표현을 고를 때는 오래 사는 데이터 하나가 몇 객체인지를 센다.

| bst의 트리 노드 `Node (l, v, r)` | 객체 | 바이트 |
|---|---|---|
| 0.46.0: `VData (t, c, Some (VTuple [l; v; r]))` | `VData`, `Some`, `VTuple`, 리스트 칸 3개 = 6 | 184 |
| 0.47.0: `VData (t, c, VTuple [| l; v; r |])` | `VData`, `VTuple`, 배열 = 3 | 112 |

## Why This Matters

- **할당량은 쉽게 재지만 그대로 믿으면 안 된다.** `GC.GetTotalAllocatedBytes`로 fib의 할당이 282MB에서 0.1MB가 된 것을 보면 큰 개선 같다. 그러나 시간은 0.074초에서 0.072초였다.
- **재사용에도 비용이 있다.** Frame을 오래 사는 배열(Spare 목록)에 넣으면 write barrier와 card marking 비용이 든다. 넣고 빼는 비용도 있다. 이 비용이 할당을 아낀 이득보다 컸다.
- **오래 사는 데이터는 표현을 바꿔야 줄어든다.** 할당 위치를 아무리 다듬어도 트리 노드 하나가 6객체면 GC가 6객체를 훑는다.

## Recognition Pattern

- GC pause 비중(`GC.GetTotalPauseDuration` ÷ 평가 시간)이 높고, gen1, gen2 수거가 있을 때(`GC.CollectionCount 1`, `2`)
- 할당량은 많이 줄였는데 시간이 그대로일 때
- 값 표현에 `option`, list, wrapper가 겹쳐 있을 때: `Some (VTuple [...])`

## The Approach

### Step 1: 어떤 수거가 일어나는지 본다

```fsharp
let gc0, gc1, gc2 = GC.CollectionCount 0, GC.CollectionCount 1, GC.CollectionCount 2
let p0 = GC.GetTotalPauseDuration()
// … 평가 …
printfn "gc %d/%d/%d pause %.3fs" (GC.CollectionCount 0 - gc0) (GC.CollectionCount 1 - gc1) (GC.CollectionCount 2 - gc2) (GC.GetTotalPauseDuration() - p0).TotalSeconds
```

| 예제 | 수거 (gen0/gen1/gen2) | GC pause |
|---|---|---|
| fib | 34/0/0 | 0.001초 |
| bst | 191/35/11 | 0.29초 |

- **fib:** gen0만 있고 pause가 거의 없다. 할당을 줄여도 얻을 것이 없다.
- **bst:** gen1, gen2가 있다. 오래 사는 데이터가 있다는 뜻이다.

### Step 2: 오래 사는 데이터 하나의 객체 수를 센다

| 값 | 0.46.0 | 0.47.0 |
|---|---|---|
| `Some 1` | `VData` + `Some` = 2 | `VData` = 1 |
| `None` | `VData` (`payload = None`) | `VData0` = 1 |
| `(a, b, c)` | `VTuple` + 리스트 칸 3개 = 4 | `VTuple` + 배열 = 2 |

F# union의 case 하나와 `Some`, 리스트 칸은 각각 객체 하나다.

### Step 3: option 대신 case를 나눈다

```fsharp
// 전: 인자가 있으면 값마다 Some 을 하나 더 만듦
| VData of typeName: string * ctor: string * payload: Value option

// 후
| VData of typeName: string * ctor: string * payload: Value     // Some 1, Rect (2, 3)
| VData0 of typeName: string * ctor: string                     // None, Red
```

- **`null`은 쓰지 않는다:** 인자 없음을 `null`로 나타내면 객체는 줄지만, `match payload with VString s`가 `null`에서 `NullReferenceException`을 낸다. case를 나누면 컴파일러가 빠진 경우를 경고로 알려 준다.
- **case를 섞어 비교한다:** 같은 타입의 두 case는 섞어서 비교할 수 있어야 한다(`None = Some 1`).

  ```fsharp
  | VData (t1, c1, x), VData (t2, c2, y) when t1 = t2 -> c1 = c2 && valueEquals x y
  | VData0 (t1, c1), VData0 (t2, c2) when t1 = t2 -> c1 = c2
  | (VData (t1, _, _) | VData0 (t1, _)), (VData (t2, _, _) | VData0 (t2, _)) when t1 = t2 -> false
  ```

### Step 4: 길이가 정해진 묶음은 배열로

튜플은 길이가 바뀌지 않으므로 `Value list` 대신 `Value[]`를 쓴다. 리스트 칸 n개가 배열 하나가 된다. 패턴은 `vs[i]`로 원소를 꺼낸다. 만들 때는 원소 둘, 셋을 `[| x; y; d fr |]`로 바로 만든다.

### Step 5: 만들고 바로 푸는 값은 만들지 않는다

`match (xs, ys) with ([], _) -> … | (x :: xr, y :: yr) -> …`는 튜플을 만든 뒤 바로 패턴으로 푼다.

- **바꾼 방법:** 분기가 모두 같은 길이의 튜플 패턴이나 `_`면, 원소를 숨은 칸에 넣고 원소마다 패턴을 맞춘다.
- **실패할 때:** 맞는 분기가 없을 때만 튜플을 만들어 `Match failure: (1, 2)`를 낸다.
- **효과:** msort가 5% 빨라졌다.

### Step 6: 테스트의 기대값은 기계적으로 바꾸고, 결과가 같음은 다른 실행기로 확인한다

값 표현을 바꾸면 테스트의 기대값(`VTuple [ ... ]`, `VData (…, None)`)도 바뀐다. 그래서 새 테스트를 고치기 전 코드에서 돌릴 수 없다.

- **기대값 바꾸기:** 괄호의 짝을 세는 작은 스크립트로 `VTuple [`를 `VTuple [|`로 바꾼다. 여러 줄에 걸친 리터럴은 들여쓰기도 한 칸 맞춘다.
- **결과가 같은지 확인:** 같은 프로그램을 컴파일러로 실행해 비교하는 차등 테스트가 그대로 통과하면, 표현이 바뀌어도 결과가 같다는 근거가 된다(`26-write-differential-tests-for-compiler.md`).

## Example

데운 뒤의 평가 시간이다(초).

| 변경 | fib | msort | bst | bst의 GC pause |
|---|---|---|---|---|
| 0.46.0 | 0.074 | 0.964 | 0.867 | 0.29 |
| (넣지 않음) Frame 재사용, Spare 최대 32개 | 0.072 | 0.985 | 0.909 | |
| `VData` / `VData0` | 0.073 | 0.972 | 0.778 | |
| 튜플을 `Value[]`로 | 0.077 | 0.962 | 0.659 | 0.22 |
| 튜플 match에서 튜플을 만들지 않음 | 0.074 | 0.909 | 0.682 | |

msort는 많이 줄지 않았다. msort에서 오래 사는 것은 리스트 칸인데, 이미 칸 하나에 한 객체이기 때문이다.

## 체크리스트

- [ ] GC pause 비중과 gen1, gen2 수거 횟수를 쟀는가?
- [ ] gen0만 있다면 할당 줄이기보다 다른 곳을 보았는가?
- [ ] 오래 사는 데이터 하나가 몇 객체인지 셌는가? (`option`, list, wrapper가 겹치는지)
- [ ] `option`으로 "있음/없음"을 나타내는 곳을 case 둘로 나눌 수 있는가?
- [ ] 길이가 정해진 묶음을 리스트 대신 배열로 바꿀 수 있는가?
- [ ] 객체를 재사용할 때 그 관리 비용까지 쟀는가?
- [ ] 표현을 바꾼 뒤 다른 실행기와 비교하는 테스트로 결과가 같음을 확인했는가?

## 관련 문서

- `70-cut-allocations-and-jit-delay-in-dotnet-interpreter.md` - 호출마다 할당 한 번인 Frame
- `72-profile-fsharp-interpreter-and-read-its-il.md` - profiler와 IL로 형 검사 helper 찾기
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
