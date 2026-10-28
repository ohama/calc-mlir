---
created: 2026-10-28
description: ADT 값은 (타입 이름, 생성자 이름, 인자) 데이터로, 생성자는 환경에 든 보통 값으로 표현한다. 생성자를 함수처럼 넘길 수 있고, 선언 문법이나 내장 option에 특별한 경로가 필요 없다
---

# 동적 인터프리터에서 ADT 값 표현하기

`type shape = Circle of int | Rect of int * int`를 지원하려면 새 값 종류 두 개면 된다. **생성자로 만든 값** `VData (typeName, ctor, payload)`와 **인자를 기다리는 생성자** `VConstructor (typeName, ctor, fields)`이다. 둘 다 환경에 넣는 보통 값이다. 그래서 `Some 1`은 기존의 함수 호출 `App (Ctor "Some", 1)`로 처리되고, `map Some xs`도 따로 처리할 것 없이 동작한다.

## The Insight

생성자에는 두 얼굴이 있다.

| 생성자 | F#/OCaml에서 | 이 인터프리터의 값 |
|---|---|---|
| `None`, `Leaf` (인자 없음) | 그 자체가 값 | `VData ("option", "None", None)` |
| `Some`, `Node` (인자 있음) | 인자를 받으면 값이 되는 **함수** | `VConstructor ("option", "Some", 1)` |
| `Some 1` | 값 | `VData ("option", "Some", Some (VInt 1))` |

`VConstructor`를 함수 값으로 취급하면, 함수 호출(`apply`)에 케이스 하나만 추가하면 된다. 고차 함수, 부분 적용, 내장 함수 인자로 넘기기 같은 기존 기능을 생성자가 그대로 물려받는다.

**타입 이름을 값에 넣는 이유:** 이 언어에는 정적 타입이 없다. 그래서 "어느 타입의 생성자인가"를 값이 직접 들고 있어야 한다. 이 정보는 세 곳에서 쓰인다.
- 오류 메시지: `expected option but got int`
- `=` 검사: 다른 타입끼리 비교하면 오류
- 같은 이름 생성자의 구분: 안쪽 `type`에서 다시 선언한 같은 이름의 생성자

## Why This Matters

생성자를 특별한 AST 노드로만 처리하면(`CtorApp of string * Expr`) 이런 것들이 안 된다.

- `map Some [1; 2]`: 생성자가 값이 아니라서 넘길 수 없다
- `let mk = Node (Leaf, 1) in ...`: 부분 적용이 안 된다
- 내장 `option`: 선언 경로와 별도로 하드코딩해야 한다

반대로 F# 함수(`Value -> Value`)를 값에 넣으면 `Value`의 구조적 equality가 깨진다(`implement-builtins-as-data-values.md`). 그래서 데이터로 표현한다.

## Recognition Pattern

- 동적 타입 언어에 `type ... = A | B of ...` 같은 합 타입을 넣을 때
- `option`/`result` 같은 표준 타입을 내장할지 선언으로 제공할지 고민할 때
- 생성자를 함수처럼 넘기는 F#/OCaml 관용구(`List.map Some`)를 지원하고 싶을 때

## The Approach

### Step 1: 값 케이스 두 개를 추가한다

```fsharp
type Value =
    ...
    /// 생성자로 만든 값. None, Some 1, Rect (2, 3). typeName 은 선언한 타입 이름 (option, shape)
    | VData of typeName: string * ctor: string * payload: Value option
    /// 인자가 있는 생성자 자체 (함수처럼 호출). fields 는 선언의 튜플 원소 수 (of int * int 면 2)
    | VConstructor of typeName: string * ctor: string * fields: int
```

- 인자가 여럿인 생성자(`Rect of int * int`)도 payload는 **튜플 하나**다. 그래서 `Rect (w, h)` 패턴이 기존 튜플 패턴으로 그대로 동작한다.
- `fields`는 선언의 `of` 타입에서 튜플 원소 수만 센 것이다. 동적 타입이라 타입 자체는 검사하지 않는다.

### Step 2: `type` 선언은 환경에 생성자를 추가할 뿐이다

```fsharp
| TypeDecl (typeName, _, ctors, body) ->
    // 인자 없는 생성자는 값, 인자 있는 생성자는 함수처럼 호출하는 값으로 환경에 추가
    let bindCtor env (ctor, argType) =
        let value =
            match argType with
            | None -> VData (typeName, ctor, None)
            | Some (TTuple items) -> VConstructor (typeName, ctor, List.length items)
            | Some _ -> VConstructor (typeName, ctor, 1)
        Map.add ctor value env
    evalIn (List.fold bindCtor env ctors) body
```

생성자 이름은 대문자로 시작하고 변수는 소문자로 시작한다. 그래서 같은 환경(`Map<string, Value>`)에 넣어도 충돌하지 않는다.

### Step 3: 생성자 호출은 `apply`의 케이스 하나다

```fsharp
| VConstructor (typeName, ctor, fields) ->
    // of int * int 처럼 원소가 여럿이면 그 수만큼의 튜플을 받아야 함
    match av with
    | _ when fields = 1 -> VData (typeName, ctor, Some av)
    | VTuple items when List.length items = fields -> VData (typeName, ctor, Some av)
    | v -> failwithf "Constructor %s expects %d fields but got %s" ctor fields (describe v)
```

여기서 생성자 인자 수만큼은 실행 중에 검사한다. 그래서 `Rect 1`은 `Constructor Rect expects 2 fields but got int`, `Rect (1, 2, 3)`은 `... but got tuple of 3`이다.

### Step 4: 내장 `option`은 처음 환경에 넣는 것으로 끝난다

```fsharp
let none = VData ("option", "None", None)
let some v = VData ("option", "Some", Some v)

let builtins: Env =
    builtinArity
    |> Map.map (fun name _ -> VBuiltin (name, []))
    // 'a option = None | Some of 'a 는 선언 없이 쓸 수 있음
    |> Map.add "None" none
    |> Map.add "Some" (VConstructor ("option", "Some", 1))
```

`type 'a option = None | Some of 'a in ...`를 사용자가 쓴 것과 똑같은 값이다. 내장 함수 `tryHead`, `tryFind`는 `some`/`none` 헬퍼로 결과를 만든다.

### Step 5: `=`와 출력을 정한다

```fsharp
// 같은 타입끼리: 생성자가 다르면 false, 같으면 인자를 비교
| VData (t1, c1, p1), VData (t2, c2, p2) when t1 = t2 ->
    c1 = c2
    && (match p1, p2 with
        | Some x, Some y -> valueEquals x y
        | _ -> true)
...
| VData (t, _, _), v -> typeError t v
```

출력은 **다시 입력했을 때 같은 값**이 되도록 괄호를 붙인다.

```fsharp
| VData (_, ctor, None) -> ctor
| VData (_, ctor, Some payload) ->
    // 다시 입력했을 때 같은 값이 되도록 괄호: Some (Some 1), Some (-1). 튜플은 이미 괄호가 있음
    match payload with
    | VData (_, _, Some _) -> sprintf "%s (%s)" ctor (formatValue payload)
    | VInt n when n < 0 -> sprintf "%s (%s)" ctor (formatValue payload)
    | _ -> sprintf "%s %s" ctor (formatValue payload)
```

괄호가 없으면 `Some Some 1`은 `(Some Some) 1`로, `Some -1`은 뺄셈 `Some - 1`로 다시 읽힌다.

## Example

```
type shape = Circle of int | Rect of int * int in
let area s = match s with Circle r -> 3 * r * r | Rect (w, h) -> w * h in
map area [Circle 2; Rect (2, 3)]                       → [12; 6]

map Some [1; 2]                                        → [Some 1; Some 2]
Some (Some (-1))                                       → Some (Some (-1))   (다시 입력 가능)
Some 1 = None                                          → false
Some 1 = 1                                             → Type error: expected option but got int
type t = A in let v = A in type u = A in match v with A -> 1 | _ -> 2   → 2
```

## 체크리스트

- [ ] 생성자 값은 `(타입 이름, 생성자 이름, 인자)` 데이터이고 F# 함수를 담지 않는다
- [ ] 인자 있는 생성자는 함수처럼 호출되는 값이라 넘기기와 부분 적용이 된다
- [ ] 인자가 여럿인 생성자의 payload는 튜플 하나라서 기존 튜플 패턴을 그대로 쓴다
- [ ] `type` 선언과 내장 `option`이 같은 값 표현을 쓴다
- [ ] `=`는 같은 타입끼리만 비교하고, 출력은 다시 입력하면 같은 값이 된다

## 관련 문서

- `distinguish-constructors-from-variables-in-patterns.md` - 패턴의 생성자/변수 구분과 모르는 생성자 오류
- `implement-builtins-as-data-values.md` - 함수를 값에 넣지 않고 데이터로 표현하는 이유
- `implement-pattern-matching-evaluator.md` - 생성자 패턴이 들어가는 `matchPattern`
