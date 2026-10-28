---
created: 2026-10-28
description: 가드가 있는 match를 scf.if로 그대로 펼치면 "나머지 분기"가 두 곳에 생겨 코드가 분기 수의 지수로 늘어난다. 분기마다 조건(패턴과 가드)을 i1로 먼저 계산하면 나머지 분기는 한 번만 생성되고 cf 블록 없이도 선형 크기가 된다
---

# scf.if만으로 match 컴파일하기: 조건을 i1로 먼저

`match`를 컴파일하는 교과서적인 방법은 분기마다 기본 블록을 만들고 실패하면 다음 블록으로 점프하는 것이다(MLIR의 `cf` 방언). 이미 `if`를 값을 내는 `scf.if`로 만들고 있다면 `match`도 `scf.if`만으로 만들 수 있다. 다만 **가드** 때문에 순진하게 펼치면 코드가 폭발한다.

## The Insight

분기 하나는 "패턴이 맞고, 가드도 참이면 본문, 아니면 나머지 분기"다. 이것을 그대로 `scf.if` 두 겹으로 쓰면 **나머지 분기가 두 곳에 들어간다.**

```
scf.if (패턴) {
  묶기; scf.if (가드) { 본문 } else { ⟨나머지 분기⟩ }      ← 1
} else {
  ⟨나머지 분기⟩                                            ← 2
}
```

나머지 분기 안에서 같은 일이 되풀이되므로 분기가 n개면 마지막 분기는 2ⁿ번 생성된다.

해결책은 **조건 전체를 먼저 `i1` 하나로 만드는 것**이다. 가드는 패턴이 맞을 때만 계산해야 하므로 조건 자체도 `scf.if`로 만든다. 다만 그 `scf.if`는 `i1`을 낸다.

```
%cond = scf.if (패턴) -> (i1) { 묶기; 가드 } else { false }
%r    = scf.if %cond -> (i64) { 묶기; 본문 } else { ⟨나머지 분기⟩ }   ← 한 곳
```

이렇게 하면 코드 크기가 분기 수에 비례한다. 가드가 있는 분기 n개로 측정한 결과는 다음과 같다(직접 확인함).

| 분기 수 | 생성한 MLIR 줄 수 |
|---|---|
| 1 | 41 |
| 2 | 58 |
| 4 | 92 |
| 8 | 160 |
| 16 | 296 |

분기 하나에 17줄씩 늘어나는 직선이다.

## Why This Matters

- **나머지 분기를 두 곳에 생성하면:** 가드가 몇 개만 있어도 MLIR이 수천 줄이 되고, `mlir-opt`와 `clang`이 느려집니다. 결과는 맞으므로 테스트로는 알아채기 어렵습니다.
- **`cf` 블록으로 바꾸면:** 이 문제는 없어지지만, `scf`와 `cf`를 한 함수에 섞어야 합니다. 그러면 영역 안에서 블록으로 점프할 수 없다는 제약을 따로 다뤄야 합니다. 이미 영역 기반으로 짠 코드 생성기라면 조건을 먼저 만드는 쪽이 훨씬 작게 바뀝니다.

## Recognition Pattern

- 값을 내는 구조적 제어 흐름(`scf.if`, 식 기반 IR)으로 `match`, `switch`, `cond`를 컴파일할 때
- "실패하면 다음으로"를 코드 생성기의 재귀로 표현했는데 같은 하위 트리를 두 번 부르고 있을 때
- 생성한 코드 크기가 입력 크기에 비해 빠르게 커질 때 → 분기 수를 늘려 가며 줄 수를 세 본다

## The Approach

### Step 1: 분기마다 조건, 묶기, 본문을 따로 만드는 함수로 나눈다

```fsharp
emitTest : Pattern -> v -> i1                         // 패턴이 맞는가
emitBind : Pattern -> v -> env -> env                 // 맞았을 때 이름 묶기 (필드 읽기)
```

### Step 2: 조건을 i1로 먼저, 나머지 분기는 else 한 곳에

```fsharp
| (pattern, guard, body) :: rest ->
    let test = emitTest fb pattern v
    let cond =
        match guard with
        | None -> test
        | Some g ->
            let guardTrue () = untagBool fb (emitExpr fb (emitBind fb pattern v env) g)
            emitIfOf fb "i1" test guardTrue (fun () -> emitConst fb "i1" 0L)
    emitIf fb cond (fun () -> emitExpr fb (emitBind fb pattern v env) body) (fun () -> emitCases rest)
```

`emitCases rest`는 `else` 영역에서 **한 번만** 불린다.

### Step 3: 하위 패턴은 앞이 맞을 때만, 필드도 그때 읽는다

`x :: rest`는 값이 cons인지 먼저 보고, 그 영역 안에서 head를 읽는다. `[]`(즉시값 5)의 필드를 읽으면 잘못된 주소를 읽는다. 여러 하위 패턴도 같은 방식으로 `scf.if (i1)` 사슬로 잇는다.

```mlir
%isCons = arith.cmpi ne, %v, %nil : i64
%ok = scf.if %isCons -> (i1) {
  %head = llvm.load ...                  // 여기서만 읽음
  ...                                    // head 패턴 검사
  scf.yield %headOk : i1
} else {
  %f = arith.constant 0 : i1
  scf.yield %f : i1
}
```

### Step 4: 어떤 값과도 맞는 분기는 검사를 만들지 않고 사슬을 끝낸다

변수, `_`, 그것들로만 된 튜플은 항상 맞는다. 가드가 없으면 그 분기는 묶기와 본문만 만들고, 뒤 분기는 생성하지 않는다. `let (a, b) = e in body`(분기 하나짜리 match)에서는 `scf.if`가 하나도 생기지 않는다.

### Step 5: 마지막까지 실패하면 돌아오지 않는 함수 + 가짜 값

```mlir
llvm.call @calc_match_failure(%v) : (i64) -> ()     // "Error: Match failure: <값>", exit(1)
%dummy = arith.constant 0 : i64                     // scf.yield 할 값 (실행되지 않음)
```

## Example

묶기는 가드용과 본문용으로 두 번 생성된다. 읽기만 하니 비용은 작다. 하지만 **LLVM이 항상 합쳐 주지는 않는다.** `match [7] with | x :: _ when x > 5 -> x | _ -> 0`을 `clang -O2`한 결과는 다음과 같다(직접 확인함).

```llvm
%5 = tail call i32 @calc_compare(i64 57, i64 41)   ; 가드의 x: 방금 저장한 57 을 그대로 씀
...
7:
  %8 = load i64, ptr %2                           ; 본문의 x: 다시 읽음
```

가드의 읽기는 직전에 저장한 값으로 바뀌었다. 하지만 본문의 읽기는 남았다. 사이에 있는 `calc_compare`가 외부 함수라서 메모리를 바꿨을 수 있다고 보기 때문이다. 정확성에는 문제가 없지만, "LLVM이 알아서 합친다"고 문서에 쓰기 전에는 결과를 확인해야 한다.

## 체크리스트

- [ ] 나머지 분기를 생성하는 호출이 한 곳뿐인가?
- [ ] 분기 수를 늘려 가며 생성한 코드의 줄 수가 직선으로 느는지 쟀는가?
- [ ] 가드는 패턴이 맞을 때만 계산하는가?
- [ ] 하위 패턴의 필드는 바깥 검사가 통과한 영역 안에서만 읽는가?
- [ ] 항상 맞는 분기 뒤를 생성하지 않는가?
- [ ] 실패 경로가 인터프리터와 같은 메시지로 끝나는가?

## 관련 문서

- `emit-short-circuit-with-scf-if-regions.md` - 영역 안에서 분기를 생성하는 기본 방법
- `implement-pattern-matching-evaluator.md` - 인터프리터의 `matchPattern` (컴파일러가 펼치는 대상)
- `compile-immutable-let-to-ssa-environment.md` - 묶기가 쓰는 이름 → SSA 환경
