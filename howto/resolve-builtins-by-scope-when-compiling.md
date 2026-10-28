---
created: 2026-10-28
description: 내장 함수 이름은 let으로 가릴 수 있으므로 "범위에 없는 이름"만 내장 함수다. 낮추는 단계가 변수 범위를 따라가면, 인자가 다 모인 호출은 런타임 함수 직접 호출로, 나머지는 인자를 하나씩 받는 클로저로 만들 수 있고, 커링 클로저는 클로저 변환이 공짜로 만들어 준다
---

# 변수 범위로 내장 함수를 가려내 컴파일하기

인터프리터는 내장 함수를 **처음 환경에 든 값**(`VBuiltin`)으로 둔다. 그래서 `length`는 보통 변수처럼 `let length = 5 in length`로 가릴 수 있다. 컴파일러가 이름만 보고 `length`를 내장 함수로 바꾸면 이 프로그램이 틀린다.

해결책은 **낮추는 단계(`Lower`)가 변수 범위를 따라가는 것**이다. 범위에 없는 이름만 내장 함수다.

```fsharp
let rec private lower (state) (ctors) (scope: Set<string>) (expr: Expr) : CExpr =
    let isBuiltin name = not (Set.contains name scope) && Eval.builtinArity.ContainsKey name
    match expr with
    | Var name when isBuiltin name -> builtinValue state name
    | Var name -> CVar name
    | Let (name, value, body) -> CLet (name, lower ... scope value, lower ... (Set.add name scope) body)
    ...
```

## The Insight

**내장 함수의 쓰임은 세 가지이고, 둘은 이미 있는 장치로 해결된다.**

| 쓰임 | 예 | 컴파일 |
|---|---|---|
| 인자가 다 모인 호출 | `substr s 1 3` | 런타임 함수 `calc_builtin_substr(s, 1, 3)`를 바로 호출 |
| 값으로 쓰거나 부분 적용 | `map length xs`, `substr "abc"` | 인자를 하나씩 받는 클로저 `fun $b0 -> fun $b1 -> fun $b2 -> 호출` |
| 가린 이름 | `let length = ... in length x` | 보통 변수 |

두 번째 경우의 커링 클로저는 직접 만들 필요가 없다. 안쪽 람다가 바깥 인자를 쓰는 식을 만들어 **기존 클로저 변환에 넘기면**, 캡처 목록이 저절로 계산된다.

```fsharp
let private builtinValue (state: State) (name: string) =
    let arity = Eval.builtinArity[name]
    let parameters = [ for i in 0 .. arity - 1 -> sprintf "$b%d" i ]
    let call = CBuiltin (name, parameters |> List.map CVar)
    List.foldBack (fun param body -> state.AddFunction(param, None, body)) parameters call
```

`substr` 값의 결과는 다음과 같다(테스트로 고정함).

```
lambda_0: Param = "$b2", Captured = ["$b0"; "$b1"]     // 호출
lambda_1: Param = "$b1", Captured = ["$b0"]
lambda_2: Param = "$b0", Captured = []                 // 바깥
```

`$`는 렉서가 식별자로 받지 않으므로 사용자 이름과 겹치지 않는다.

## Why This Matters

- **이름만 보고 바꾼 경우:** `let length = fun x -> 42 in length [1]`이 인터프리터에서는 `42`인데 컴파일하면 `1`이 됩니다.
- **범위를 코드 생성기(SSA 환경)에서만 안 경우:** 클로저 변환이 먼저 일어나므로, 람다 안의 `length`가 "캡처할 자유 변수"로 들어갑니다. 그러면 클로저를 만드는 바깥에서 그 이름을 찾지 못해 실패합니다.
- **항상 클로저로 만든 경우:** 맞기는 하지만, `length s` 한 번에 클로저 할당과 간접 호출이 생깁니다.
- **값으로 쓰는 경우를 빼먹은 경우:** `map length xs`, `fold (fun a b -> a + b) 0`처럼 흔한 코드를 컴파일하지 못합니다.

## Recognition Pattern

- 기본 함수(prelude, 내장 함수)를 일반 변수처럼 가릴 수 있는 언어를 컴파일할 때
- 커링된 언어에서 런타임 함수(여러 인자)를 부를 때
- 코드 생성기에 "이 이름은 내장인가?" 분기가 생길 때 → 그 판단은 범위를 아는 단계에서 해야 한다

## The Approach

### Step 1: 이름을 묶는 모든 곳에서 범위를 넓힌다

빠뜨리기 쉬운 곳까지 모두 적는다.

| 식 | 범위에 더하는 이름 |
|---|---|
| `let x = e in body` | `body`에 `x` (`e`에는 아님) |
| `let rec f x = body in e` | `body`에 `f`, `x`. `e`에 `f` |
| `fun x -> body` | `body`에 `x` |
| `match` 분기, `let (a, b) = ...` | 가드와 본문에 패턴이 묶는 이름 |

### Step 2: 호출 모양을 펼쳐서 인자 수를 센다

`f a b c`는 `App(App(App(f, a), b), c)`다. 머리와 인자 목록으로 펼친다. 머리가 내장 함수이고 인자가 arity 이상이면 직접 호출하고, 남는 인자는 결과에 차례로 적용한다.

```fsharp
let call = CBuiltin (name, args |> List.take arity |> List.map lower)
args |> List.skip arity |> List.fold (fun f a -> CApply (f, lower a)) call
// fst (fun x -> x + 1, 0) 41  →  CApply (CBuiltin ("fst", [...]), 41)
```

인자는 왼쪽부터 계산한다. 인터프리터가 `App`을 하나씩 평가하는 순서와 같다.

### Step 3: 런타임 고차 함수는 생성 코드와 같은 호출 규약으로 클로저를 부른다

`map`, `fold` 같은 런타임 함수는 사용자 클로저를 불러야 한다. 생성한 코드의 호출 규약 `(클로저, 인자) -> 결과`를 C에서 그대로 쓴다.

```c
value calc_apply(value f, value arg) {
    value (*code)(value *, value) = (value(*)(value *, value))CALC_FIELD(f, 1);
    return code((value *)f, arg);
}
```

결과가 정해지면 멈추는 함수(`exists`, `forall`, `tryFind`)는 그 시점에 멈춰야 한다. `exists (fun x -> 10 / x > 1) [1; 0]`은 0으로 나누기 전에 `true`가 된다.

### Step 4: 세 경우를 모두 테스트한다

```
substr "hello" 1 3                       → "ell"           (직접 호출)
let f = substr "hello" in (f 1 3, f 0 2) → ("ell", "he")    (부분 적용)
map length ["a"; "bb"; ""]               → [1; 2; 0]        (값으로)
let length = fun x -> 42 in length [1]   → 42               (가린 이름)
(head, fst (fun x -> x + 1, 0) 41)       → (<fun>, 42)      (값 출력, 남는 인자)
```

## Example

```fsharp
// ❌ BAD: 이름만 보고 내장 함수로
| App (Var "length", a) -> CBuiltin ("length", [ lower a ])

// ✅ GOOD: 범위에 없을 때만
| Var name when isBuiltin name -> builtinValue state name
| App _ when (match spine expr with
              | Var name, args -> isBuiltin name && args.Length >= Eval.builtinArity[name]
              | _ -> false) -> ...
```

## 체크리스트

- [ ] 이름을 묶는 모든 곳(`let`, `let rec`, `fun`, 패턴)에서 범위를 넓히는가?
- [ ] 내장 함수 판정을 클로저 변환보다 먼저(같은 단계에서) 하는가?
- [ ] 인자가 다 모인 호출만 직접 호출하고, 남는 인자는 결과에 적용하는가?
- [ ] 값으로 쓰는 경우가 커링 클로저가 되고, 출력과 비교가 인터프리터와 같은가?
- [ ] 런타임 고차 함수가 생성 코드와 같은 호출 규약으로 클로저를 부르는가?

## 관련 문서

- `implement-builtins-as-data-values.md` - 인터프리터 쪽: 내장 함수를 데이터 값으로
- `implement-closure-conversion-to-top-level-functions.md` - 커링 클로저를 만들어 주는 클로저 변환
- `compile-adt-constructors-with-name-pointers.md` - 생성자를 값으로 쓸 때도 같은 방식
