---
order: 8
created: 2026-10-27
commit: 8f5e26c
version: 0.6.0
description: 함수 값은 정의 시점의 환경을 함께 저장한 클로저다. let rec은 호출할 때 자기 자신을 환경에 넣는 재귀 클로저로 구현한다
---

# 인터프리터에 클로저와 `let rec` 구현하기

> 관련 커밋: [8f5e26c](https://github.com/ohama/calc-mlir/commit/8f5e26c) (v0.6.0)

함수를 값으로 다루려면 **본문과 정의 시점의 환경을 함께 저장**해야 한다. 이것이 클로저다. 재귀 함수는 환경 안에서 자기 자신을 찾아야 하는데, 불변 `Map` 환경으로는 "자기 자신을 담은 환경"을 바로 만들 수 없다. 그래서 **호출할 때 자기 자신을 환경에 넣는** 방식으로 해결한다.

## The Insight

**1. 함수 본문은 "어디서 정의됐나"의 환경에서 평가한다 (lexical scope).**

```
let x = 1 in
let f y = x + y in      ← f 가 만들어질 때 x = 1
let x = 100 in
f 1                     ← 호출할 때 x = 100 이지만, 결과는 1 + 1 = 2
```

호출하는 곳의 환경(`x = 100`)을 쓰면 동적 범위가 된다. 그러면 결과가 `101`이 되고, 함수의 의미가 호출 위치에 따라 바뀐다. 이를 막으려면 `Lambda`를 평가할 때 **그 순간의 `env`를 값 안에 저장**한다.

**2. `let rec`은 순환 참조 문제다.**

`let rec fact n = ... fact (n - 1) ...`의 클로저는 자기 환경 안에 `fact`가 있어야 한다. 즉 "클로저 ⊃ 환경 ⊃ 그 클로저"라는 순환 구조다. F#의 불변 `Map`과 판별 유니온으로는 이런 순환 값을 한 번에 만들 수 없다.

해결 방법은 **순환을 미리 만들지 않는 것**이다. 클로저에 자기 이름만 기억시켜 두고, **호출할 때마다** 환경에 자기 자신을 넣는다.

```fsharp
| VRecClosure (name, param, body, closureEnv) ->
    let env = closureEnv |> Map.add name fv |> Map.add param av   // fv = 이 클로저 자신
    evalIn env body
```

## Why This Matters

- **클로저 없이** 함수 본문만 저장하면 동적 범위가 된다. partial application(`add 1`)이 반환한 함수가 `x`를 잃어버린다.
- **`let`과 `let rec`을 구분하지 않으면** 섀도잉 의미가 깨진다. ML 계열에서 `let f n = f (n + 1)`의 본문에 있는 `f`는 **바깥** `f`를 가리킨다. 예전 정의를 감싸서 새 정의를 만드는 흔한 패턴이다.
- **반대로 착각하기도 쉽다.** 이 프로젝트에서 "`let rec f n = n in let rec f n = f (n + 1) in f 1`은 2"라는 테스트를 썼다. 실제로는 안쪽 `f`가 `rec`이라서 자기 자신을 끝없이 호출했다. 결국 테스트 호스트가 `Stack overflow.`로 죽었다.

## Recognition Pattern

- 언어에 `fun`/람다, higher-order function, partial application을 넣을 때
- 함수가 반환한 함수가 바깥 변수를 못 찾는다(`Unbound variable`) → 클로저에 환경을 저장하지 않은 것
- 함수 결과가 호출 위치의 변수 값에 따라 달라진다 → 호출자 환경을 쓰고 있는 것 (동적 범위)
- 재귀 함수가 `Unbound variable: <자기 이름>`을 낸다 → `let rec` 처리가 없는 것

## The Approach

### Step 1: 값 타입에 클로저를 추가한다

```fsharp
type Value =
    | VInt of int
    | VBool of bool
    /// fun param -> body, 정의된 시점의 환경을 함께 저장 (정적 범위)
    | VClosure of param: string * body: Expr * env: Map<string, Value>
    /// let rec name param = body. 호출할 때 name 을 자기 자신으로 환경에 추가
    | VRecClosure of name: string * param: string * body: Expr * env: Map<string, Value>

type Env = Map<string, Value>
```

`Value`가 `Map<string, Value>`를 품는 재귀 타입이다. `Env` 약어는 `Value` 뒤에 정의되므로, `Value` 안에서는 `Map<string, Value>`를 직접 적는다.

### Step 2: 람다는 현재 환경을 붙잡는다

```fsharp
| Lambda (param, body) -> VClosure (param, body, env)
```

### Step 3: 호출은 클로저의 환경에서 본문을 평가한다

```fsharp
| App (f, arg) ->
    // 값에 의한 호출: 함수와 인자를 먼저 평가한 뒤 본문을 평가
    let fv = evalIn env f
    let av = evalIn env arg
    apply fv av

and apply fv av =
    match fv with
    | VClosure (param, body, closureEnv) ->
        evalIn (Map.add param av closureEnv) body          // env(호출자)가 아니라 closureEnv
    | VRecClosure (name, param, body, closureEnv) ->
        let env = closureEnv |> Map.add name fv |> Map.add param av
        evalIn env body
    | v -> typeError "function" v
```

`Map.add`의 순서를 정한다. `name`을 먼저, `param`을 나중에 넣는다. 그러면 `let rec f f = ...`처럼 인자 이름이 함수 이름과 같을 때 인자가 우선한다.

### Step 4: `let rec`은 환경에 재귀 클로저를 넣는다

```fsharp
| LetRec (name, param, body, inExpr) ->
    evalIn (Map.add name (VRecClosure (name, param, body, env)) env) inExpr
```

`let`은 다르게 처리한다. 바인딩 값을 **바깥 환경**에서 평가한다. 그래서 본문의 같은 이름은 바깥 것을 가리킨다.

```fsharp
| Let (name, value, body) ->
    let v = evalIn env value              // name 은 아직 env 에 없음
    evalIn (Map.add name v env) body
```

### Step 5: 의미를 테스트로 고정한다

| 테스트 | 기대값 | 확인하는 것 |
|---|---|---|
| `let x = 1 in let f y = x + y in let x = 100 in f 1` | `2` | lexical scope |
| `let add x y = x + y in let inc = add 1 in inc 41` | `42` | partial application이 `x`를 기억함 |
| `let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 10` | `3628800` | 재귀 |
| `let f x = f x in f 1` | `Unbound variable: f` | `rec` 없이는 재귀 안 됨 |
| `let f n = n in let f n = f (n + 1) in f 1` | `2` | `let`의 본문은 바깥 `f`를 봄 |

## Example

클로저에 실제로 환경이 저장되는지 값 자체를 꺼내 확인한다.

```fsharp
test "클로저는 정의 시점의 환경을 저장" {
    match calc "let y = 5 in fun x -> x + y" with
    | VClosure ("x", _, env) -> Expect.equal (Map.tryFind "y" env) (Some (VInt 5)) ""
    | v -> failtestf "클로저가 아님: %A" v
}
```

**다른 구현 방법과 비교**

| 방법 | 장점 | 단점 |
|---|---|---|
| 호출할 때 자기 자신 삽입 (이 문서) | 불변 환경 유지, 구현 간단 | 직접 재귀 하나만 지원. mutual recursion(`let rec f ... and g ...`)은 확장이 필요 |
| mutable 참조로 나중에 연결 (`ref`로 환경을 만든 뒤 채우기) | mutual recursion으로 확장하기 쉬움 | mutable state가 생김 |
| 지연 값 (`Lazy`) 환경 | 순환 구조를 자연스럽게 표현 | 평가 시점을 추적하기 어려움 |

## 체크리스트

- [ ] `Lambda`를 평가할 때 현재 `env`를 클로저에 저장한다
- [ ] 호출할 때 **호출자 환경이 아니라 클로저 환경**을 쓴다
- [ ] `let`은 바인딩 값을 바깥 환경에서 평가한다 (`rec` 없으면 자기 자신을 못 봄)
- [ ] `let rec`은 호출 시점에 자기 자신을 환경에 넣는다
- [ ] lexical scope, partial application, 재귀, `rec` 없는 재귀 실패, 섀도잉을 테스트로 고정했다
- [ ] 재귀 테스트를 추가하기 전에 종료 조건을 CLI로 먼저 확인했다

## 관련 문서

- `09-handle-deep-recursion-stack-overflow.md` - 재귀가 깊어질 때 .NET 스택 한계 다루기
- `07-parse-function-application-in-fsyacc.md` - `f x y` 호출 문법
