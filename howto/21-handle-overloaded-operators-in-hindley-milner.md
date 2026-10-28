---
order: 21
created: 2026-10-28
commit: ac66f6c
version: 0.14.0
description: HM에는 오버로딩이 없다. int와 string 모두에 쓰는 +는 allowed list가 있는 type variable로 표현한다. 이 변수를 generalize하지 않으면 F# 기본 동작, generalize하면 F# inline과 같은 결과가 나온다
---

# Hindley–Milner에서 `+` 같은 오버로딩 연산자 다루기

> 관련 커밋: [ac66f6c](https://github.com/ohama/calc-mlir/commit/ac66f6c) (v0.14.0)

이 언어의 `+`는 int 덧셈이면서 문자열 연결이다. 순수한 HM에는 "int 또는 string" 같은 타입이 없다. 그래서 **allowed list(`Allowed`)가 있는 type variable**을 만든다. 이 변수는 unify할 때 목록에 있는 타입만 받고, `let`에서 **generalize하지 않는다**. 끝까지 정해지지 않으면 목록의 첫 타입(int)으로 정한다. F#도 이렇게 동작한다는 것을 확인했다.

## The Insight

선택지는 셋이다.

| 방법 | `let add x y = x + y` | 비용 |
|---|---|---|
| 연산자를 나눈다 (OCaml: `+`와 `^`) | `int -> int -> int` | 언어가 바뀜 (`"a" ^ "b"`) |
| 타입 클래스 (Haskell: `Num a =>`) | `'a -> 'a -> 'a when 'a : Num` | 제약을 스킴에 싣고, instantiate하고, 컴파일러는 사전(dictionary)을 넘김 |
| **allowed list 변수, generalization 안 함 (F# 기본, 이 프로젝트)** | 처음 쓰인 곳이 정함. 없으면 `int` | 변수에 필드 하나, generalization에서 조건 하나 |
| allowed list 변수, generalize함 (F# `inline`과 같은 결과) | 쓰는 곳마다 따로 정함 | 위와 같음. 컴파일러라면 쓰는 곳마다 코드를 따로 만들어야 함 |

allowed list 변수는 unify할 때 목록을 검사하므로, generalize하든 안 하든 **`add true false`는 거부된다.** 둘의 차이는 "같은 `add`를 int와 string에 함께 쓸 수 있는가"뿐이다. 이 프로젝트는 F# 기본 동작에 맞춰 generalize하지 않았다.

## Why This Matters

F#이 실제로 이렇게 동작한다. `dotnet fsi`로 확인한 결과다.

```fsharp
let add x y = x + y
add 1 2                         // 3
```
```fsharp
let add x y = x + y
add "a" "b"                     // "ab"   ← 첫 사용(string)이 add 의 타입을 정함
```
```fsharp
let add x y = x + y
(add 1 2, add "a" "b")          // error FS0001: This expression was expected to have type ...
```
```fsharp
let inline add x y = x + y
(add 1 2, add "a" "b")          // (3, "ab")   ← inline 이면 쓰는 곳마다 따로 정함
```

allowed list 변수를 generalize하지 않으면 앞의 세 경우에서 F#과 같은 결과가 나온다. 사용자에게 익숙한 동작을 큰 구현 없이 얻는다.

generalization 조건(`v.Allowed.IsNone`)만 뺀 버전으로 같은 식을 추론해 봤다.

| 식 | generalization 안 함 (현재) | generalize함 |
|---|---|---|
| `let add x y = x + y in add` | `int -> int -> int` | `int -> int -> int` |
| `let add x y = x + y in add true false` | `expected int or string but got bool` | `expected int or string but got bool` |
| `let add x y = x + y in (add 1 2, add "a" "b")` | `expected int but got string` | `int * string` |
| `... (add 1 2, add "a" "b", add [1] [2])` | 오류 | `expected int or string but got int list` |

generalize해도 soundness는 유지된다. `instantiate`가 allowed list까지 복사하기 때문이다. 이 인터프리터는 실행 중에 `+`가 값의 타입을 보고 동작을 고르므로, generalize한 버전도 그대로 실행된다. 컴파일러라면 사정이 다르다. 쓰는 곳마다 다른 덧셈 코드를 만들어야 하는데, F#이 `inline`을 요구하는 이유가 이것이다.

## Recognition Pattern

- 정적 타입 추론을 동적 언어에 나중에 붙이는데, 기존 연산자가 여러 타입을 받을 때 (`+`, `<`, `length`)
- HM 구현에서 "int 또는 string" 같은 타입을 표현해야 할 때
- 타입 클래스까지 구현하기에는 과한 작은 언어

## The Approach

### Step 1: type variable에 allowed list를 단다

```fsharp
and TypeVar =
    { ...
      /// +, <, length, sort 처럼 일부 타입만 허용하는 변수 (타입 생성자 이름 목록). 이런 변수는 일반화하지 않음
      mutable Allowed: string list option }
```

목록에는 **타입 생성자 이름**을 쓴다. `["int"; "string"]`, `["string"; "list"]`(`length`). `"list"`는 원소 타입과 상관없이 모든 리스트를 허용한다는 뜻이다.

### Step 2: unification에서 목록을 검사하고, 변수끼리는 교집합을 만든다

```fsharp
let private bindVar (v: TypeVar) t =
    match t with
    | TyVar other ->
        // 두 변수를 잇기: 허용 목록은 교집합, 단계는 낮은 쪽
        let allowed =
            match v.Allowed, other.Allowed with
            | None, a
            | a, None -> a
            | Some a, Some b ->
                match a |> List.filter (fun x -> List.contains x b) with
                | [] -> raise (NotAllowed (a, t))
                | both -> Some both
        other.Allowed <- allowed
        ...
    | _ ->
        ...
        match v.Allowed, t with
        | Some allowed, TyCon (name, _) when List.contains name allowed -> ()
        | Some allowed, _ -> raise (NotAllowed (allowed, t))
        | None, _ -> ()
        v.Link <- Some t
```

오류 메시지는 목록을 그대로 보여 준다: `Type mismatch: expected int or string but got bool`.

### Step 3: 연산자마다 새 allowed list 변수를 만든다

```fsharp
/// +, <: int 끼리 또는 string 끼리
let sameIntOrString a b =
    let t = newVarAt st st.Level (Some [ "int"; "string" ])
    unifyAt t (infer' a)
    unifyAt t (infer' b)
    t

| Add (a, b) -> sameIntOrString a b
| Less (a, b) | Greater (a, b) | ... ->
    sameIntOrString a b |> ignore
    tBool
```

내장 함수는 타입 안에 allowed list가 있는 ∀ 변수를 둔다. instantiate할 때 목록도 복사된다.

```fsharp
let intOrString = gen (Some [ "int"; "string" ])
let stringOrList = gen (Some [ "string"; "list" ])
"length", stringOrList ^-> tInt
"sort", listOf intOrString ^-> listOf intOrString
```

내장 함수의 타입은 미리 정해 둔 것이라 ∀ 변수에 목록이 붙어 있어도 된다. **추론으로 만든 let은 목록 있는 변수를 generalize하지 않는다**는 점이 다르다.

### Step 4: generalization에서 제외한다

```fsharp
| TyVar v when v.Level > st.Level && v.Level <> genericLevel && v.Allowed.IsNone -> v.Level <- genericLevel
```

조건 하나(`v.Allowed.IsNone`)가 이 방식의 전부다.

### Step 5: 끝까지 정해지지 않은 변수는 첫 번째 허용 타입으로 정한다

```fsharp
/// 아직 정해지지 않은 허용 목록 변수는 첫 번째 허용 타입으로 정함 (F# 가 + 를 int 로 정하는 것과 같음)
let rec private applyDefaults (st: State) t =
    match prune t with
    | TyVar ({ Allowed = Some (first :: _) } as v) ->
        v.Link <- Some (if first = "list" then listOf (newVar st) else TyCon (first, []))
    ...
```

`fun x y -> x + y`는 `int -> int -> int`가 된다. 목록 순서가 기본값이 되므로 `["int"; "string"]`처럼 기본으로 삼을 타입을 앞에 둔다.

## Example

```
fun x y -> x + y                                  → int -> int -> int     (기본값)
fun x -> x + "!"                                  → string -> string      (사용이 정함)
let add x y = x + y in add "a" "b"                → string
let add x y = x + y in (add 1 2, add "a" "b")     → Type mismatch: expected int but got string
true + 1                                          → Type mismatch: expected int or string but got bool
[1] < [2]                                         → Type mismatch: expected int or string but got int list
length                                            → string -> int         (기본값)
length [1]                                        → int
```

## 체크리스트

- [ ] 오버로딩 연산자의 피연산자 타입을 allowed list가 있는 새 변수로 묶는다
- [ ] unification이 목록을 검사하고, 변수끼리는 교집합을 만든다 (비면 오류)
- [ ] 추론한 let 에서 allowed list 변수를 generalize할지 정했다 (안 하면 F# 기본, 하면 F# `inline`과 같은 결과)
- [ ] 끝까지 정해지지 않은 변수는 목록의 첫 타입으로 정한다
- [ ] "같은 let 함수를 두 타입으로 쓰기"가 오류인지 테스트한다 (F#과 같은 동작)

## 관련 문서

- `20-implement-hindley-milner-with-levels.md` - level 기반 generalization (이 문서의 generalization 조건이 들어가는 곳)
- `22-write-soundness-test-for-type-checker.md` - allowed list 검사를 빠뜨렸을 때 드러나는 실행 중 오류를 찾는 테스트
