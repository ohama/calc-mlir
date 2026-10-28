---
created: 2026-10-28
description: if, &&, ||는 값을 내는 scf.if 하나로 만든다. 분기 코드는 영역 안에서 생성해야 단락 평가가 되므로, 코드 생성기는 분기를 "나중에 부를 함수"로 받는다. 값이 있는 scf.if는 else와 scf.yield가 둘 다 있어야 한다
---

# scf.if 영역으로 if와 단락 평가(&&, ||) 컴파일하기

식 언어의 `if c then a else b`는 값을 내는 식이다. LLVM IR로 직접 만들면 기본 블록 셋과 `phi`가 필요하지만, MLIR에서는 **값을 내는 `scf.if`** 하나로 끝난다. `&&`와 `||`도 같은 연산으로 만든다.

```
a && b   ≡   if a then b else false
a || b   ≡   if a then true else b
```

## The Insight

**분기의 코드는 영역(region) 안에서 생성해야 한다.** `scf.if`의 두 영역 중 실행되는 쪽만 계산된다. 그래서 코드 생성기가 분기 식을 먼저 생성한 다음 `scf.if`에 SSA 이름만 넘기면, 두 분기가 모두 `scf.if` **앞에서** 실행된다. 결과는 맞아 보이지만 단락 평가가 깨진다. `false && 1 / 0 = 0`이 `false` 대신 0으로 나누기 오류를 낸다.

그래서 `emitIf`는 분기를 값(SSA 이름)이 아니라 **함수(`unit -> string`)** 로 받는다. 들여쓰기를 한 단계 늘리고, 그 안에서 함수를 불러 코드를 영역 안에 쓴다.

```fsharp
let private emitIf (fb: FunctionBuilder) (cond: string) (thenBranch: unit -> string) (elseBranch: unit -> string) =
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = scf.if %s -> (i64) {" r cond)
    fb.Indent()
    fb.Emit(sprintf "scf.yield %s : i64" (thenBranch ()))   // 여기서 생성 → 영역 안
    fb.Dedent()
    fb.Emit("} else {")
    fb.Indent()
    fb.Emit(sprintf "scf.yield %s : i64" (elseBranch ()))
    fb.Dedent()
    fb.Emit("}")
    r
```

## Why This Matters

- **분기를 먼저 생성하고 `arith.select`나 `scf.if`로 고르면:**
  - 단락 평가가 깨집니다.
  - 선택되지 않은 분기의 오류가 실행됩니다. 예: `if true then 1 else 1 / 0`
  - 재귀 함수가 생기면 기저 사례 분기가 무시돼서 끝나지 않습니다.
- **문법 오류:** `scf.if`의 규칙을 모르면 `mlir-opt`가 오류를 냅니다. 메시지는 정확하지만 원인을 바로 알기 어렵습니다.

## Recognition Pattern

- 식 언어(OCaml, F#, Rust 블록)의 `if`/`match`를 MLIR로 컴파일할 때
- `&&`, `||`, `?:`처럼 피연산자 일부만 계산해야 하는 연산이 있을 때
- 생성한 코드에서 "선택되지 않은 분기"의 부수 효과가 보일 때

## The Approach

### Step 1: 조건은 i1로 바꾼다

값이 태그가 붙은 i64라면 `true`의 표현과 비교해서 i1을 만든다.

```mlir
%c = arith.cmpi eq, %v, %true : i64      // %true = arith.constant 11 : i64
```

### Step 2: 값을 내는 scf.if는 else와 yield가 둘 다 필요

`-> (i64)`로 결과를 선언하면 두 분기 모두 `scf.yield`로 같은 타입의 값을 내야 한다(직접 확인함).

| 빠뜨린 것 | `mlir-opt` 오류 |
|---|---|
| `else` 영역 | `'scf.if' op must have an else block if defining values` |
| 한 분기의 `scf.yield` | `region branch point has 0 operands, but region successor needs 1 inputs` |

결과가 없는 `scf.if`는 `else`를 생략할 수 있다. 오류 검사처럼 "조건이 참이면 함수만 부르는" 코드에 쓴다.

```mlir
scf.if %isZero {
  llvm.call @calc_error_divide_by_zero() : () -> ()
}
```

### Step 3: 영역 안에서 바깥 값을 그대로 쓴다

`scf.if`는 `IsolatedFromAbove`가 아니다. 그래서 영역 안에서 바깥에 정의된 SSA 값(`%x`)을 그대로 쓸 수 있다. 반대로 영역 안에서 정의한 값은 밖에서 쓸 수 없고, 꼭 `scf.yield`로 내보내야 한다. 코드 생성기의 반환값(SSA 이름)은 언제나 "현재 위치에서 보이는 값"이어야 한다.

### Step 4: `&&`, `||`, `not`

```fsharp
| And (a, b) ->
    let c = untagBool fb (emitExpr fb a)
    emitIf fb c (fun () -> emitExpr fb b) (fun () -> emitConst fb "i64" (tagBool false))
| Or (a, b) ->
    let c = untagBool fb (emitExpr fb a)
    emitIf fb c (fun () -> emitConst fb "i64" (tagBool true)) (fun () -> emitExpr fb b)
// true(11) 와 false(3) 는 3번 비트만 다름: 분기 없이 xor 한 번
| Not e -> emitOp fb "arith.xori" (emitExpr fb e) (emitConst fb "i64" 8L) "i64"
```

## Example

`false && 1 / 0 = 0`이 만드는 MLIR. 나눗셈과 0 검사가 모두 `scf.if`의 첫 영역 **안에** 있다.

```mlir
%v1 = arith.constant 3 : i64            // false
%v3 = arith.constant 11 : i64
%v2 = arith.cmpi eq, %v1, %v3 : i64
%v4 = scf.if %v2 -> (i64) {
  ...                                   // 1 / 0 = 0 계산 (0 검사 포함)
  scf.yield %eq : i64
} else {
  %f = arith.constant 3 : i64
  scf.yield %f : i64
}
```

실행 결과는 인터프리터와 같은 `false`이고 종료 코드는 0이다. `true && 1 / 0 = 0`은 `Error: Attempted to divide by zero.`와 종료 코드 1로 끝난다.

```fsharp
// ❌ BAD: 분기를 먼저 생성 → 둘 다 실행됨
let t = emitExpr fb thenExpr
let e = emitExpr fb elseExpr
emitSelect fb c t e

// ✅ GOOD: 분기를 함수로 넘겨 영역 안에서 생성
emitIf fb c (fun () -> emitExpr fb thenExpr) (fun () -> emitExpr fb elseExpr)
```

## 체크리스트

- [ ] 분기 코드를 영역 안에서 생성하는가? (분기를 함수로 넘김)
- [ ] 값을 내는 `scf.if`에 `else`와 양쪽 `scf.yield`가 있는가?
- [ ] 영역 안에서 정의한 값을 밖에서 쓰지 않는가?
- [ ] 단락 평가 테스트(`false && <오류>`, `true || <오류>`, `if true then 1 else <오류>`)가 있는가?

## 관련 문서

- `emit-mlir-as-llvm-func-and-lower.md` - `scf`를 llvm까지 내리는 패스
- `match-interpreter-integer-semantics-in-native-code.md` - 영역 안의 나눗셈 검사
- `control-let-if-scope-with-precedence.md` - 파서 쪽 `if`의 범위
