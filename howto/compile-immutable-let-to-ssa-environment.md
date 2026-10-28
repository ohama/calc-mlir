---
created: 2026-10-28
description: 바꿀 수 없는 let은 코드를 만들지 않는다. 이름 → SSA 이름 Map 하나로 섀도잉과 범위가 해결되고, 언어의 정적 범위가 MLIR 영역의 범위와 겹친다. 쓰이지 않는 값도 생성해 두면 LLVM이 순수한 것만 지운다
---

# 바꿀 수 없는 let을 SSA 이름 환경으로 컴파일하기

함수형 언어의 `let x = e in body`는 **코드를 만들지 않는다.** `e`의 값을 담은 SSA 이름(`%v3`)을 환경에 넣고 `body`를 생성하면 끝이다. 변수를 읽을 때는 환경에서 그 SSA 이름을 꺼내 그대로 쓴다.

```fsharp
/// env 는 변수 이름 → 그 값을 담은 SSA 이름
let rec private emitExpr (fb: FunctionBuilder) (env: Map<string, string>) expr : string =
    match expr with
    | Let (name, value, body) ->
        let v = emitExpr fb env value
        emitExpr fb (Map.add name v env) body
    | Var name ->
        match Map.tryFind name env with
        | Some v -> v
        | None -> raise (NotSupported ("built-in function " + name))
    ...
```

## The Insight

**SSA 값은 한 번 정의되면 바뀌지 않는다. 바꿀 수 없는 `let` 변수도 그렇다.** 둘의 성질이 같으니 변수 하나는 SSA 값 하나에 그대로 대응한다. 메모리도 복사도 필요 없다.

LLVM Kaleidoscope 튜토리얼처럼 변수마다 `alloca`를 만들고 `load`/`store`한 뒤 `mem2reg`에 맡기는 방식도 있다. 하지만 그 방식은 **변수를 바꿀 수 있는 언어**(`x = x + 1`)를 위한 것이다. 바꿀 수 없는 `let`에 쓰면 쓸모없는 코드만 늘어난다.

언어 쪽의 까다로운 규칙도 자료 구조가 대신 처리한다.

| 언어 규칙 | 처리하는 것 |
|---|---|
| 섀도잉 `let x = 1 in let x = x + 10 in x` | `Map.add`가 같은 키를 덮어씀. 오른쪽 `x + 10`은 덮어쓰기 **전** 환경으로 생성 |
| 범위 `let x = 1 in (let x = 2 in x) + x` | `Map`이 불변이라 안쪽 `let`이 끝나면 바깥 `env`가 그대로 남음 |
| `if` 분기 안의 `let` | 분기 영역 안에서 만든 이름은 그 분기의 `env`에만 있음 |

## Why This Matters

- **환경을 가변 사전(`Dictionary`)으로 만든 경우:** 안쪽 `let`이 끝난 뒤 이름을 되돌리는 코드가 필요하고, 하나라도 빠지면 범위가 샙니다. `let x = 1 in (let x = 2 in x) + x`가 `4`가 됩니다(정답은 `3`).
- **`alloca`로 시작한 경우:** 생성한 MLIR이 길어져 읽기 어렵습니다. 최적화 패스 없이는 느립니다.
- **쓰이지 않는 `let`을 생략한 경우:** `let x = 1 / 0 in 5`가 오류 없이 `5`가 됩니다. 값에 의한 호출(strict) 언어인 인터프리터는 오류를 냅니다.

## Recognition Pattern

- 바꿀 수 없는 변수만 있는 언어(ML 계열, 순수 함수형)를 SSA IR(MLIR, LLVM)로 컴파일할 때
- 코드 생성기에 "변수 → 메모리 위치" 표를 만들려고 할 때 → 변수가 바뀌지 않는다면 필요 없다.

## The Approach

### Step 1: 환경은 불변 Map으로, 재귀 호출마다 넘긴다

환경을 코드 생성기의 상태(필드)로 두지 않는다. 인자로 넘기면 범위가 재귀 구조와 똑같이 움직인다. `emitIf`에 넘기는 분기 함수도 그 시점의 `env`를 잡는다.

```fsharp
| If (cond, thenExpr, elseExpr) ->
    let c = untagBool fb (emitExpr fb env cond)
    emitIf fb c (fun () -> emitExpr fb env thenExpr) (fun () -> emitExpr fb env elseExpr)
```

### Step 2: 정적 범위와 영역 범위가 겹친다는 것을 이용한다

MLIR에서 `scf.if` 영역 안에서 정의한 값은 영역 밖에서 보이지 않는다. 언어에서도 분기 안의 `let`은 분기 밖에서 보이지 않는다. 그래서 `env`에서 꺼낸 SSA 이름은 **항상 현재 위치에서 쓸 수 있는 값**이다. 영역을 빠져나오는 값은 `scf.yield`의 결과 하나뿐이고, 그것은 새 SSA 이름이 된다.

이 대응이 깨지면(코드 생성기 버그) `mlir-opt`가 텍스트를 읽는 단계에서 바로 알려 준다(직접 확인함).

```
dom.mlir:10:25: error: use of undeclared SSA value name
  %bad = arith.addi %r, %inner : i32
```

### Step 3: 쓰이지 않는 값도 생성하고, 지우는 일은 LLVM에 맡긴다

코드 생성기는 "이 값이 쓰이는가"를 판단하지 않는다. 부수 효과(오류로 종료)가 있는지는 LLVM이 더 정확히 안다. `clang -O2` 뒤의 `calc_main`을 보면 다음과 같다(직접 확인함).

```llvm
; let x = 2 * 3 in 5    → 곱셈은 순수하므로 사라짐
define noundef i64 @calc_main() {
  ret i64 41
}

; let x = 10 / 0 in 5   → 나눗셈은 사라지고 오류 호출만 남음
define noundef i64 @calc_main() {
  tail call void @calc_error_divide_by_zero()
  ret i64 41
}
```

외부 함수 호출은 지우지 않는다. 그래서 인터프리터와 같은 의미가 저절로 유지된다.

### Step 4: 환경에 없는 이름을 분류한다

타입 검사를 통과한 프로그램에서 환경에 없는 이름은 **내장 함수**뿐이다. "Unbound variable"이 아니라 아직 지원하지 않는 기능으로 알린다.

```
Error: Not supported by the compiler yet: built-in function length
```

## Example

```fsharp
// ❌ BAD: 가변 사전 + 되돌리기를 잊음
let env = Dictionary<string, string>()
| Let (name, value, body) ->
    env[name] <- emitExpr fb value
    emitExpr fb body             // 끝난 뒤 이전 값으로 되돌리지 않음 → 범위가 샘

// ✅ GOOD: 불변 Map 을 인자로
| Let (name, value, body) ->
    let v = emitExpr fb env value
    emitExpr fb (Map.add name v env) body
```

`let x = 5 in x`가 만드는 MLIR은 상수 하나와 반환이 전부다.

```mlir
llvm.func @calc_main() -> i64 {
  %v1 = arith.constant 41 : i64     // 5
  llvm.return %v1 : i64             // x 는 %v1 그 자체
}
```

## 체크리스트

- [ ] 변수가 바뀌지 않는 언어인가? (바뀐다면 `alloca` + `mem2reg` 방식을 쓴다)
- [ ] 환경이 불변 Map이고 재귀 호출의 인자로 넘어가는가?
- [ ] `let`의 값은 새 이름을 넣기 **전** 환경으로 생성하는가? (섀도잉)
- [ ] 쓰이지 않는 `let`의 값도 생성하는가? (strict 의미)
- [ ] 환경에 없는 이름을 "아직 지원하지 않음"으로 분류하는가?

## 관련 문서

- `emit-short-circuit-with-scf-if-regions.md` - 영역 안에서 분기 코드를 만드는 법
- `implement-closures-and-let-rec.md` - 인터프리터 쪽 환경과 클로저 (컴파일러는 다음 단계에서 클로저 변환)
