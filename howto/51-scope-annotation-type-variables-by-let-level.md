---
order: 51
created: 2026-10-28
commit: 61914f3
version: 0.29.0
description: 타입 표기의 'a는 "아직 모르는 타입"이지만, 이름 → 변수 표를 프로그램 전체에 하나만 두면 generalize된 변수를 다른 곳이 unify해서 soundness가 깨진다. 표를 let 정의마다 열고, 변수는 그 정의의 level에서 만든다. 인자 표기는 let x = (x : t)로 풀어 쓰되 범위를 열지 않는다
---

# 타입 표기의 'a를 let 정의의 level로 범위 정하기

> 관련 커밋: [61914f3](https://github.com/ohama/calc-mlir/commit/61914f3) (v0.29.0)

타입 표기 `(e : t)`의 구현은 대부분 간단하다. `t`를 타입으로 바꾸고 `e`의 타입과 unify하면 된다. 어려운 부분은 `'a`다.

```
let id (x: 'a) : 'a = x in (id 1, id "a")       → int * string    ('a 두 개는 같은 타입)
fun (x: 'a) (y: 'a) -> (x, y)                   → 'a -> 'a -> 'a * 'a
let f (x: 'a) = x + 1 in f                      → int -> int      (OCaml: 통과, F#: 경고 FS0064)
```

OCaml에서 표기의 `'a`는 "polymorphic하라"는 강제가 아니다. unification variable에 이름을 붙인 것이다. 그래서 같은 이름은 같은 변수여야 한다. 그러려면 **이름 → 변수 표를 어디에 두고 언제 비울지** 정해야 한다.

## The Insight

**`'a`의 범위는 generalization의 단위와 같아야 한다.** HM에서 generalization은 `let` 정의가 끝날 때 일어나고, 그 정의의 level보다 깊은 변수만 `∀`가 된다(`20-implement-hindley-milner-with-levels.md`). 그러면 이렇게 정하면 된다.

1. `let` 정의의 값을 추론할 때 새 범위(표)를 연다. 범위는 그 정의의 level을 기억한다.
2. 표기에서 처음 보는 `'a`는 **가장 안쪽 범위의 level**에서 새 변수로 만든다. 지금 level이 아니다.
3. 이미 바깥 범위에 있는 `'a`는 그 변수를 쓴다.
4. 정의가 끝나면 범위를 닫는다. 그 안의 변수는 보통 변수처럼 generalize된다.

바깥 정의의 `'a`는 바깥 level에 있으므로, 안쪽 `let`이 끝나도 generalize되지 않는다. 이것이 맞는 동작이다. 안쪽에서 바깥 `x`와 같은 타입이라고 적은 것이기 때문이다.

```
let f (x: 'a) =                  ← 범위 A (level 1): 'a 를 level 1 에서 만듦
    let g (y: 'a) = (x, y) in    ← 범위 B (level 2): 'a 는 A 의 것. level 1 이라 g 에서 generalize 안 됨
    g
in f                             → 'a -> 'a -> 'a * 'a

let f (x: 'a) =
    let id (y: 'b) = y in        ← 'b 는 B 에서 처음: level 2, id 가 끝나면 generalize
    (id 1, id true, x)
in f                             → 'a -> int * bool * 'a
```

## Why This Matters

- **표를 프로그램 전체에 하나 둔 경우:** `let id (x: 'a) = x in …`에서 `'a`의 변수는 `id`가 끝날 때 generalize된다(`∀`). 그 뒤 다른 정의에서 `(y: 'a)`를 쓰면 표에서 **같은 변수**를 꺼내 int와 unify한다. 그러면 `id`의 type scheme이 `int -> int`로 바뀌어 버린다. 이미 `id "a"`를 통과시킨 뒤라면 soundness가 깨진다.
- **표기를 만날 때마다 새 표를 만든 경우:** `fun (x: 'a) (y: 'a) -> …`의 두 `'a`가 다른 변수가 된다. 적은 뜻과 다르다.
- **새 변수를 지금 level에서 만든 경우:** 안쪽 `let`에서 처음 쓴 바깥 이름이 안쪽에서 generalize된다. 반대로 바깥 범위에서 만든 변수가 너무 깊은 level에 있으면, 바깥 정의가 끝날 때 함께 generalize되지 않을 수 있다. 변수의 level은 **범위의 level**이어야 한다.
- **OCaml을 그대로 따른 경우:** OCaml은 `'a`의 범위가 최상위 구문 전체다. Calc 프로그램은 대부분 `let … in` 하나짜리 큰 식이다. 그래서 `let id (x: 'a) = x in (id 1, id "a")`가 OCaml에서는 오류가 된다(최상위 구문 안의 `'a`는 generalize되지 않음). 범위를 `let` 정의로 좁히면 이 식이 통과하고, 안전하다.

## Recognition Pattern

- HM 기반 언어에 타입 표기를 넣을 때
- 표기에 이름 있는 type variable(`'a`, `<T>`)이 있을 때
- "같은 이름은 같은 타입"과 let-polymorphism을 함께 지켜야 할 때

## The Approach

### Step 1: 인자 표기는 다시 묶는 let으로 풀어 쓴다

AST에는 새 노드 `Annot of Expr * TypeExpr` 하나만 더한다. 인자 표기는 파서에서 본문 앞의 `let`으로 바꾼다.

```
fun (x: int) -> e        ==  fun x -> let x = (x : int) in e
let f x : int = e        ==  let f x = (e : int)
let x : int = e          ==  let x = (e : int)
```

```fsharp
let paramBody (param: string, annot: Expr option) (body: Expr) =
    match annot with
    | Some a -> Let(param, a, body)          // a = Annot (Var param, t)
    | None when param = "()" -> Match(Var "()", [ PUnit, None, body ])
    | None -> body
```

`Lambda`의 모양이 그대로라서 인터프리터, closure conversion, 코드 생성을 고치지 않아도 된다. 인터프리터와 `Lower`는 `Annot (e, _)`를 `e`로 다루기만 하면 된다. `let x = x`는 코드를 만들지 않는다(`29-compile-immutable-let-to-ssa-environment.md`).

### Step 2: 범위 스택을 추론 상태에 둔다

```fsharp
type private State =
    { ...
      /// 타입 표기의 'a 가 가리키는 변수. let 정의마다 범위 하나 (단계, 이름 → 변수), 안쪽 범위가 앞
      mutable TypeVarScopes: (int * Dictionary<string, Type>) list
      NamedVars: List<string * Type * (int * int) option> }

let private namedTypeVar (st: State) (node: obj) (name: string) =
    match st.TypeVarScopes |> List.tryPick (fun (_, vars) -> tryGet vars name) with
    | Some t -> t                                   // 바깥 범위까지 찾음
    | None ->
        let level, vars = List.head st.TypeVarScopes
        let t = newVarAt st level None              // 지금 level 이 아니라 범위의 level
        vars[name] <- t
        st.NamedVars.Add((name, t, Errors.positionOf node))
        t
```

맨 바깥에는 level 0의 범위를 하나 둔다. 정의 밖에서 쓴 `'a`(`fun (x: 'a) -> x`가 프로그램 전체일 때)는 level 0이라 generalize되지 않는다. 마지막 결과 타입에는 `'a`로 보인다.

### Step 3: let 정의의 값을 추론할 때 범위를 연다. 인자 let은 열지 않는다

```fsharp
| Let (name, value, body) ->
    st.Level <- st.Level + 1
    // 인자 표기가 만든 let x = (x : t) 는 범위를 열지 않음: fun (x: 'a) (y: 'a) 의 두 'a 는 같음
    let isParam = match value with Annot (Var v, _) -> v = name | _ -> false
    let tv = withTypeVarScope st (not isParam) (fun () -> infer' value)
    st.Level <- st.Level - 1
    generalize st tv
```

Step 1에서 풀어 쓴 `let`도 `Let` 노드라는 점을 조심한다. 이 `let`이 범위를 열면 인자마다 범위가 생겨 두 `'a`가 달라진다. `let rec`은 함수 본문에, `let rec … and`는 그룹 전체에 범위 하나를 연다.

### Step 4: 표기는 unify만 한다

```fsharp
| Annot (e, te) ->
    let t = convertType ctx (namedTypeVar st expr) te    // type 선언과 같은 변환, 'a 만 다르게
    expect e t (infer' e)                                // 맞지 않으면 e 를 가리킴
    t
```

`type` 선언과 같은 `convertType`을 쓰고, `'a`를 찾는 함수만 넘겨 바꾼다. `type` 선언에서는 선언의 매개변수에 없는 `'a`가 오류(`Unbound type variable`)다. 표기에서는 새 변수가 된다.

### Step 5: 다른 타입으로 정해진 'a를 경고한다 (F# FS0064)

추론이 다 끝난 뒤 `NamedVars`의 변수를 본다. 변수가 아니라 구체적인 타입이 되었으면, 적은 사람의 의도(polymorphic)와 다르다.

```
$ calc -e "let f (x: 'a) = x + 1 in f 2"
Warning: the type variable 'a has been constrained to be type int (line 1, column 11)
```

OCaml은 이 경우에 경고하지 않는다. 하지만 `'a`를 "generic"으로 읽는 사람이 많아서, F#처럼 알려 주는 쪽이 친절하다. 결과 타입에서 기본값(`+`는 int)으로 정해진 것도 경고에 반영된다. 그래서 끝까지 정해지지 않았을 때만 `int or string`으로 보인다.

## Example

| 식 | 타입 | 이유 |
|---|---|---|
| `let id (x: 'a) : 'a = x in (id 1, id "a")` | `int * string` | `id`가 끝나면 `'a` generalize |
| `fun (x: 'a) (y: 'a) -> (x, y)` | `'a -> 'a -> 'a * 'a` | 인자 let은 범위를 열지 않음 |
| `let f (x: 'a) = let g (y: 'a) = (x, y) in g in f` | `'a -> 'a -> 'a * 'a` | 안쪽 `'a`는 바깥 것 |
| `let f (x: 'a) = let id (y: 'b) = y in (id 1, id true, x) in f` | `'a -> int * bool * 'a` | `'b`는 안쪽 정의의 것 |
| `let f (x: 'a) (y: 'a) = (x, y) in f 1 "a"` | 오류 | 같은 `'a` |
| `let f (x: 'a) = x + 1 in f` | `int -> int` + 경고 | `'a`가 int로 정해짐 |

## 체크리스트

- [ ] 이름 → 변수 표를 generalization 단위(`let` 정의)마다 여는가?
- [ ] 새 변수를 지금 level이 아니라 범위의 level에서 만드는가?
- [ ] 바깥 범위에 있는 이름을 먼저 찾는가?
- [ ] 인자 표기를 풀어 쓴 `let`이 범위를 열지 않는가?
- [ ] `let id (x: 'a) = x in (id 1, id "a")`와 `fun (x: 'a) (y: 'a) -> …`를 모두 테스트하는가?
- [ ] 표기를 지운 프로그램과 실행 결과가 같은가? (차등 테스트)

## 관련 문서

- `20-implement-hindley-milner-with-levels.md` - level 기반 generalization
- `29-compile-immutable-let-to-ssa-environment.md` - `let x = x`가 코드를 만들지 않는 이유
- `41-track-source-positions-without-changing-ast.md` - 표기와 경고의 위치
