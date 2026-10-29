---
order: 78
created: 2026-10-29
commit: c52b845
version: 0.52.0
description: append (reverse xs) ys 같은 라이브러리 관용구를 한 번에 처리하는 최적화는 인터프리터와 컴파일러에 같은 규칙으로 넣는다. 알아보는 조건(이름이 가려지지 않은 내장 함수), 계산 순서, 타입 검사를 건너뛸 때의 오류 순서까지 맞추고, 테스트는 고치기 전 코드에서도 통과하게 쓴다
---

# 라이브러리 관용구를 두 실행기에 같은 규칙으로 합치기

> 관련 커밋: [c52b845](https://github.com/ohama/calc-mlir/commit/c52b845) (v0.52.0)

0.51.0에서 컴파일러가 `append (reverse xs) ys`를 런타임의 `calc_rev_append` 하나로 부르게 했다(`77-match-tuple-components-without-building-tuples.md`). 0.52.0에서 인터프리터에도 같은 최적화를 넣었다.

| merge sort 20만 (초) | Python | 최적화 전 | 최적화 후 |
|---|---|---|---|
| 실행 파일 (0.51.0) | 0.209 | 0.097 | 0.080 (튜플 match와 함께 0.066) |
| 인터프리터 (0.52.0) | 0.207 | 0.665 | 0.611 |

## The Insight

**최적화가 보존해야 하는 것은 결과만이 아니다.** 관용구 하나를 한 번에 처리하려면 세 가지가 원래 코드와 같아야 한다.

| 지킬 것 | 이 경우 |
|---|---|
| **알아보는 조건** | `append`와 `reverse`가 사용자 정의로 가려지지 않은 내장 함수일 때만 |
| **계산 순서** | `reverse`의 인자를 먼저, 그다음 `ys` |
| **오류의 순서** | 타입 검사를 건너뛰면 `reverse 1`이 `ys`를 계산하기 **전에** 오류 |

그리고 이 최적화는 **두 실행기에 같은 규칙으로** 넣는다. 한쪽만 다르게 알아보면 차등 테스트가 오류 순서 같은 차이를 잡지 못하는 경우가 생긴다.

## Why This Matters

- **가린 이름:** `let reverse xs = xs in append (reverse [1; 2]) [3]`은 `[1; 2; 3]`이어야 한다. 이름만 보고 바꾸면 `[2; 1; 3]`이 된다.
- **부수 효과:** `append (reverse (print 1; [1])) (print 2; [2])`는 `12`를 출력해야 한다. 인자를 다른 순서로 계산하면 출력이 바뀐다.
- **타입 검사를 건너뛴 실행:** 이 인터프리터의 테스트는 대부분 타입 검사 없이 평가한다. `reverse`가 먼저 실패해야 하는데 `ys`를 먼저 계산하면 오류 전에 출력이 생긴다.

## Recognition Pattern

- 라이브러리 함수를 겹쳐 부르는 모양이 뜨거운 곳에 있을 때(`append (reverse xs) ys`, `length (map f xs)`)
- 같은 언어에 실행기가 둘 이상 있어서, 최적화를 한쪽에 넣으면 다른 쪽도 맞춰야 할 때

## The Approach

### Step 1: 알아보는 조건을 "그 이름이 가리키는 것"으로 정한다

```fsharp
let isBuiltin name expr =
    match expr with
    | Var v when not (scope.Vars.ContainsKey v) ->              // 지역 변수로 가려지지 않았고
        match Map.tryFind v scope.Consts with
        | Some (VBuiltin (n, [])) -> n = name                   // 실제로 그 내장 함수를 가리킴
        | _ -> false
    | _ -> false
```

- **컴파일러:** Lower를 거친 Core에서 `CBuiltin ("append", [ CBuiltin ("reverse", [ xs ]); ys ])`를 본다. Lower가 범위를 따라가서 가려진 이름은 `CBuiltin`이 아니라 보통 호출로 만들므로 같은 조건이 된다.
- **인터프리터:** 같은 판단을 범위(`scope.Vars`, `scope.Consts`)로 직접 한다.

### Step 2: 계산 순서와 오류 순서를 그대로 옮긴다

```fsharp
| f, [ App (r, xs); ys ] when isBuiltin "append" f && isBuiltin "reverse" r ->
    let xs, ys = compile scope xs, compile scope ys
    fun fr ->
        let xs = asList xs fr                  // reverse 의 인자를 먼저 계산하고 확인 (ys 보다 앞)
        let mutable acc = asList ys fr
        for x in xs do
            acc <- x :: acc
        VList acc
```

- **오류 메시지:** `asList`의 오류는 내장 함수의 `list'`와 같은 `Type error: expected list but got int`다.
- **확인 시점:** 확인(`asList xs`)이 `ys`의 계산보다 앞에 있어야 원래와 같다.

### Step 3: 테스트는 고치기 전 코드에서도 통과하게 쓴다

```fsharp
Expect.equal (calc "let reverse xs = xs in append (reverse [1; 2]) [3]") (VList [ VInt 1; VInt 2; VInt 3 ]) "가린 reverse"
// 인자 순서: "12"
// 오류 순서: "append (reverse 1) (print 2; [2])" → 출력 없이 Type error
```

이 테스트는 최적화 전과 후의 `Eval.fs`에서 모두 통과한다. 최적화가 동작을 바꾸지 않는다는 것을 테스트가 보여 준다.

## Example

| 실행기 | 알아보는 곳 | 바꾼 코드 |
|---|---|---|
| 컴파일러 | `Codegen.emitExpr`의 `CBuiltin ("append", [ CBuiltin ("reverse", [ xs ]); ys ])` | C 런타임 `calc_rev_append` |
| 인터프리터 | `Eval.compile`의 `App` 앞에서 `revAppend` | F# 반복으로 `x :: acc` |

같은 관용구를 고쳐도 효과의 크기는 실행기마다 다르다. 인터프리터는 -8%, 컴파일러는 -18%였다. 컴파일러는 칸 하나를 만드는 비용(Boehm GC 할당)이 상대적으로 크기 때문이다.

## 체크리스트

- [ ] 알아보는 조건이 이름이 아니라 "그 이름이 가리키는 내장 함수"인가?
- [ ] 사용자가 이름을 가린 경우를 테스트했는가?
- [ ] 인자 계산 순서가 원래와 같은가? (부수 효과로 확인)
- [ ] 타입 검사를 건너뛴 실행에서 오류가 나는 시점이 같은가?
- [ ] 두 실행기에 같은 조건과 같은 의미로 넣었는가?
- [ ] 새 테스트가 최적화 전 코드에서도 통과하는가?

## 관련 문서

- `77-match-tuple-components-without-building-tuples.md` - 컴파일러의 `rev_append`와 튜플 match
- `74-move-list-builders-back-to-builtins-and-count-jit-methods.md` - `reverse`, `append`를 내장 함수로
- `40-resolve-builtins-by-scope-when-compiling.md` - Lower가 내장 함수를 범위로 찾는 방법
