---
order: 47
created: 2026-10-28
commit: caef51e
version: 0.27.0
description: or 패턴 p1 | p2를 분기 두 개로 펼치면 가드가 있을 때 결과가 달라진다. 검사는 "왼쪽이 맞으면 참, 아니면 오른쪽"인 i1 scf.if로, 이름 묶기는 묶는 이름 수만큼 값을 내는 scf.if(%v:2 = scf.if ... -> (i64, i64))로 맞은 쪽에서 값을 꺼낸다
---

# or 패턴을 값 여러 개를 내는 scf.if로 컴파일하기

> 관련 커밋: [caef51e](https://github.com/ohama/calc-mlir/commit/caef51e) (v0.27.0)

or 패턴 `p1 | p2`는 "둘 중 하나가 맞으면 맞음"이다.

```
match n with
| 1 | 2 | 3 -> "small"
| (x, "a") | (x, "b") -> ...        // 두 쪽이 같은 이름 x 를 묶음
```

컴파일러에서 가장 쉬운 방법은 분기를 여러 개로 펼치는 것이다(`| 1 -> e | 2 -> e | 3 -> e`). 하지만 **가드가 있으면 결과가 달라진다.**

## The Insight

**or 패턴은 "왼쪽부터 처음 맞는 쪽 하나"로 이름을 묶은 뒤 가드를 본다.** 가드가 실패하면 다른 쪽을 다시 시도하지 않고 다음 분기로 간다. OCaml이 이렇게 동작하고, 이 프로젝트의 인터프리터도 같다. 분기로 펼치면 가드가 실패했을 때 다른 쪽을 시도하게 된다(직접 확인함).

```
match (1, 5) with (x, _) | (_, x) when x > 3 -> x | _ -> 0
→ 0      왼쪽 (x, _) 로 x = 1, 가드 실패 → 다음 분기

match (1, 5) with (x, _) when x > 3 -> x | (_, x) when x > 3 -> x | _ -> 0
→ 5      펼친 버전: 두 번째 분기에서 x = 5 로 다시 시도
```

두 쪽이 겹치고(같은 값에 둘 다 맞음) 묶는 값이 다르며 가드가 있을 때만 차이가 난다. 드물지만, 인터프리터와 결과가 달라지면 differential testing이 실패한다. 그래서 펼치지 않고 패턴 그대로 컴파일한다.

## Why This Matters

- **분기로 펼친 경우:** 위의 경우에서 인터프리터와 컴파일한 실행 파일의 결과가 달라집니다. 가드와 or 패턴이 함께 있는 프로그램에서만 나타나서 찾기 어렵습니다.
- **이름을 한쪽에서만 꺼낸 경우:** `(x, "a") | (x, "b")`는 우연히 같은 자리라서 맞지만, `[Some x] | [Some x; _]`처럼 자리가 다르면 틀린 값을 꺼냅니다.
- **패턴 수가 많을 때 펼친 경우:** 중첩된 or 패턴(`Some (1 | 2), (a | b)`)은 조합 수만큼 분기가 늘어나고, 본문 코드도 그만큼 복사됩니다.

## Recognition Pattern

- 패턴 매칭을 결정 트리나 `if` 사슬로 컴파일하는데 or 패턴을 더할 때
- 기준 구현(인터프리터)이 or 패턴의 가드를 어떻게 다루는지 맞춰야 할 때
- MLIR 영역(region)에서 값 여러 개를 내보내야 할 때

## The Approach

### Step 1: 검사는 "왼쪽이 맞으면 참, 아니면 오른쪽"

```fsharp
| Ast.POr (a, b) ->
    let c = emitTest fb a v
    emitIfOf fb "i1" c (fun () -> emitConst fb "i1" 1L) (fun () -> emitTest fb b v)
```

오른쪽 검사는 왼쪽이 실패했을 때만 계산된다. 하위 패턴의 필드를 읽는 순서도 인터프리터와 같다.

### Step 2: 이름 묶기는 값 여러 개를 내는 `scf.if`

or 패턴이 묶는 이름이 n개면, n개 값을 내는 `scf.if`를 만든다. `then` 영역은 왼쪽 패턴으로, `else` 영역은 오른쪽 패턴으로 이름을 묶고, 같은 순서로 `scf.yield`한다.

```fsharp
| Ast.POr (a, b) ->
    let names = patternVars a |> Set.toList          // 두 쪽이 같은 이름 (파서가 확인)
    if names.IsEmpty then env
    else
        let c = emitTest fb a v
        let valuesFrom p () =
            let bound = emitBind fb p v Map.empty
            names |> List.map (fun n -> bound[n])
        let results = emitIfMany fb names.Length c (valuesFrom a) (valuesFrom b)
        List.fold2 (fun acc n r -> Map.add n r acc) env names results
```

MLIR에서는 결과가 여러 개인 연산의 이름을 `%v:N`으로 짓고, 각 결과를 `%v#0`, `%v#1`로 쓴다.

```mlir
%v27:1 = scf.if %v25 -> (i64) {
  ...                                   // 왼쪽 패턴에서 x
  scf.yield %v29 : i64
} else {
  ...                                   // 오른쪽 패턴에서 x
  scf.yield %v32 : i64
}
scf.yield %v27#0 : i64                  // x
```

```fsharp
let private emitIfMany (fb: FunctionBuilder) (count: int) (cond: string) thenBranch elseBranch =
    let r = fb.Fresh()
    let types = List.replicate count "i64" |> String.concat ", "
    fb.Emit(sprintf "%s:%d = scf.if %s -> (%s) {" r count cond types)
    ...                                   // scf.yield a, b : i64, i64
    [ for i in 0 .. count - 1 -> sprintf "%s#%d" r i ]
```

### Step 3: 왼쪽 검사를 다시 계산해도 된다

분기의 조건(Step 1)과 이름 묶기(Step 2)는 같은 왼쪽 검사를 각각 생성한다. 검사는 읽기와 비교뿐이라 다시 계산해도 의미가 같다. LLVM이 겹치는 읽기를 합치기도 한다(사이에 런타임 호출이 있으면 남는다). 정확성이 먼저이고, 합치기는 최적화 단계의 일이다.

### Step 4: 다른 단계도 함께 맞춘다

| 단계 | or 패턴 처리 |
|---|---|
| 파서 | 두 쪽이 같은 이름을 묶지 않으면 `Variable x must occur on both sides of this \| pattern` |
| 인터프리터 | `matchPattern a v |> Option.orElseWith (fun () -> matchPattern b v)` |
| 타입 추론 | 두 쪽의 타입, 같은 이름의 타입을 단일화 |
| 빠진 분기 검사 | 첫 열의 `Or (a, b)`를 행 두 개로 펼침 |

### Step 5: 겹치는 or 패턴과 가드를 differential testing에 넣는다

```fsharp
"match (2, 3) with | (1, y) | (y, 3) when y > 5 -> y | _ -> -1", ("-1\n", "", 0), "가드가 실패하면 다음 분기"
"match [Some 3] with | [Some x] | [Some x; _] -> x | _ -> 0", ("3\n", "", 0), "맞은 쪽에서 이름을 꺼냄"
```

## Example

```
type c = Red | Green | Blue in
let warm k = match k with Red | Green -> true | Blue -> false in
(warm Red, warm Blue)
→ (true, false)                         (인터프리터와 컴파일한 실행 파일이 같음)
```

## 체크리스트

- [ ] or 패턴을 분기로 펼치지 않았는가? (가드가 있으면 의미가 달라짐)
- [ ] 오른쪽 검사는 왼쪽이 실패했을 때만 하는가?
- [ ] 이름을 실제로 맞은 쪽에서 꺼내는가? (값 여러 개를 내는 `scf.if`)
- [ ] 두 쪽의 이름이 같은지 파서가 확인하는가?
- [ ] 겹치는 or 패턴과 가드가 함께 있는 예제가 테스트에 있는가?

## 관련 문서

- `35-compile-match-with-scf-if-conditions.md` - or 패턴이 들어가는 `match` 컴파일
- `42-check-match-exhaustiveness-with-maranget.md` - or 패턴을 펼쳐서 판정하는 빠진 분기 검사
- `28-emit-short-circuit-with-scf-if-regions.md` - 값을 내는 `scf.if`
