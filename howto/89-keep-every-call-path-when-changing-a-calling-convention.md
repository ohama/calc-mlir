---
order: 89
created: 2026-10-30
commit: 9097df0
version: 0.63.1
description: 함수의 호출 규약을 바꾸는 최적화는 그 함수에 들어오는 모든 경로를 세어 봐야 한다. 0.63.0의 튜플 인자 펴기는 이름으로 부르는 경로를 빠르게 했지만, 값으로 넘겨 간접 호출하는 경로를 막아서 그쪽이 0.17초에서 0.27초로 느려졌다. 막지 말고 옛 규약의 입구를 하나 더 두면 두 경로를 모두 지킨다. 앞선 최적화의 벤치마크를 다음 최적화 때도 다시 잰다
---

# 호출 규약을 바꿀 때 모든 호출 경로를 지키기

> 관련 커밋: [9097df0](https://github.com/ohama/calc-mlir/commit/9097df0) (v0.63.1)

0.63.0은 튜플 인자를 폈다(`88-flatten-tuple-parameters-with-worker-wrapper.md`). 본문이 `match p with (a, b)`인 함수는 direct 함수가 원소를 따로 받는다. 그런데 0.58.0부터 클로저는 "인자 k개를 한 번에 받는 함수"의 포인터를 들고 있었다(`83-call-multi-argument-closures-through-direct-entry.md`). 그 포인터로 부르는 쪽은 튜플을 펴지 않으므로, 0.63.0은 편 함수의 클로저가 그 포인터를 알리지 않게 막았다. 틀린 결과는 막았지만 그 경로는 느려졌다.

| 프로그램 (실행 파일) | 0.61.0 | 0.63.0 | 0.63.1 |
|---|---|---|---|
| `fold step 0 pairs` 3백만 쌍 5번 (`step acc p = match p with (a, b) -> …`) | 0.17초 | 0.27초 | 0.17초 |
| `go (i - 1) (b, …)` 5천만 번 (이름으로 부름) | 0.27초 | 0.20초 | 0.20초 |

0.63.0을 만들 때 잰 것은 이름으로 부르는 경로뿐이었다.

## The Insight

**함수 하나에는 들어오는 경로가 여럿 있고, 경로마다 호출하는 쪽이 기대하는 규약이 다르다.** 이 컴파일러의 이름 붙은 함수에는 경로가 셋이다.

| 경로 | 호출하는 쪽 | 기대하는 인자 |
|---|---|---|
| 이름으로 인자를 다 줌 (`go 1 (2, 3)`) | Lower가 만든 `CCallDirect` | Lower가 아는 대로 (편 원소) |
| 모르는 함수에 인자 k개 (`f acc x`) | Codegen의 `emitApplyMany`가 클로저의 포인터로 | 원래 인자 k개 (튜플 그대로) |
| 인자 하나씩 (`map f`, 부분 적용) | 클로저의 코드 포인터 | 인자 하나 |

0.63.0은 첫째 경로의 규약을 바꿨다. 둘째 경로의 호출하는 쪽은 바뀐 규약을 모르므로, 그 경로에는 **옛 규약의 입구가 따로 있어야 한다.** 막는 것은 쉽고 안전하지만 손실이다. 입구를 하나 더 두면 두 경로를 모두 지킨다.

```
lambda_0         인자 하나씩       (클로저의 코드 포인터)
lambda_0_direct  (acc, a, b)       이름으로 부를 때   ← 0.63.0이 바꾼 규약
lambda_0_tupled  (acc, p)          간접 호출          ← 0.63.1이 더한 옛 규약의 입구
```

## Why This Matters

- **최적화가 다른 최적화를 끈다.** 새 최적화의 벤치마크는 빨라졌다고 보여 주고, 꺼진 최적화의 벤치마크는 아무도 다시 재지 않는다. 결과가 맞으므로 테스트도 통과한다.
- **안전하게 막은 곳이 성능 회귀가 된다.** "이 경우는 옛 길로" 같은 조건은 대개 틀리지 않으려고 넣는다. 그 옛 길이 앞선 최적화 이전의 길이면, 그 최적화의 효과가 사라진다.
- **흔한 쓰임새가 막힌 쪽에 있을 수 있다.** 튜플을 받는 함수를 `fold`에 넘기는 것은 이름으로 부르는 것만큼 흔하다.

## Recognition Pattern

- 함수의 인자 모양, 인자 수, 반환 모양을 바꾸는 최적화 (worker/wrapper, uncurrying, unboxing, 인자 순서 바꾸기)
- "이 경우는 최적화하지 않음"으로 끝나는 조건을 넣을 때
- 클로저나 vtable처럼 호출하는 쪽이 함수를 모르고 부르는 경로가 있을 때

## The Approach

### Step 1: 함수에 들어오는 경로를 모두 적는다

호출을 만드는 코드를 찾는다. 이 컴파일러에서는 `CCallDirect`를 만드는 Lower, 클로저 블록의 칸을 읽어 부르는 Codegen(`emitApplyMany`, `emitApplyValue`), 런타임의 `calc_apply`다. 경로마다 "호출하는 쪽이 무엇을 아는가"를 적는다.

### Step 2: 규약을 바꾸는 경로와 그대로인 경로를 나눈다

바꾼 규약을 아는 쪽(Lower가 만든 호출)만 새 입구를 부른다. 나머지 경로에는 옛 규약의 입구를 남긴다. 새 입구로 갈 수 없다고 경로를 막지 않는다.

### Step 3: 입구를 더할 때 본문을 다시 낮추지 않는다

옛 규약의 입구는 클로저 쪽과 같은 본문(튜플을 `match`한 뒤 본문)이면 된다. 한 번 낮춘 Core 식을 같이 쓰므로 Lower에서 일이 늘지 않는다. 생성 코드는 한 벌 늘어난다.

```fsharp
let entry = if flat.IsSome then outer + "_tupled" else outer + "_direct"
let direct = if rest.IsEmpty then None else Some (entry, parameters.Length)   // 클로저가 알리는 입구
...
flat |> Option.iter (fun (directParams, directBody) -> add (outer + "_direct") directParams directBody)
if not rest.IsEmpty then add entry parameters body                              // 옛 규약: 튜플 그대로
```

인자가 하나인 함수는 둘째 경로를 쓰지 않으므로(k ≥ 2일 때만) 입구를 더하지 않는다.

### Step 4: 앞선 최적화의 벤치마크를 다시 잰다

0.58.0의 효과를 보여 준 프로그램(모르는 함수에 인자 여럿)을, 튜플을 받는 함수로 바꿔 0.61.0, 0.63.0, 지금 세 빌드에서 잰다. 옛 빌드는 따로 만들어 둔다.

```bash
git stash && dotnet build src/Calc -c Release -o "$SCRATCH/calc061" && git stash pop
```

### Step 5: 경로마다 테스트한다

| 경로 | 프로그램 |
|---|---|
| 간접 호출, 인자 수가 맞음 | `fold step 0 [(1, 2); (3, 4)]`, `let g h = h 1 (2, 3) in g f` |
| 간접 호출의 계산 순서 | `h (print "a"; 1) (print "b"; (2, 3))` |
| 간접 호출로 들어간 재귀 | `apply2 walk 1000001 (1, 2)` (본문의 재귀가 tail call로 이어지는지) |
| 인자 수보다 많이 줌 | 함수를 돌려주는 편 함수 `h 1 (2, 3) 4` |

MLIR 테스트에서 클로저 칸이 새 입구가 아니라 옛 규약의 입구(`@lambda_0_tupled`)를 가리키는지 확인한다.

## Example

```mlir
llvm.func internal tailcc @lambda_7_tupled(%closure: !llvm.ptr, %arg0: i64, %arg1: i64) -> i64 {
...
%v22 = llvm.mlir.addressof @lambda_7_tupled : !llvm.ptr     // 클로저의 마지막 칸
```

`fold`가 `step`을 부를 때 헤더의 인자 수(2)가 맞으므로 `_tupled`를 `(acc, p)`로 한 번에 부른다. `step`을 이름으로 부르는 곳은 여전히 `_direct`를 `(acc, a, b)`로 부른다.

## 체크리스트

- [ ] 규약을 바꾸는 함수에 들어오는 경로를 모두 적었는가? (Lower, Codegen, 런타임)
- [ ] 바뀐 규약을 모르는 경로에 옛 규약의 입구가 있는가?
- [ ] "최적화하지 않음"으로 막은 경로가 앞선 최적화의 경로가 아닌가?
- [ ] 앞선 최적화의 벤치마크를 다시 쟀는가?
- [ ] 입구를 더할 때 본문을 다시 낮추지 않는가?

## 관련 문서

- `88-flatten-tuple-parameters-with-worker-wrapper.md` - 규약을 바꾼 최적화
- `83-call-multi-argument-closures-through-direct-entry.md` - 막혔던 경로 (클로저의 direct 포인터)
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
