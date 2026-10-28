---
created: 2026-10-28
description: HM 타입 추론의 let generalization은 "환경에 없는 변수 찾기" 대신 level로 한다. 변수를 이을 때 level을 낮추지 않으면 잘못된 프로그램이 통과한다
---

# level로 Hindley–Milner 타입 추론 구현하기

Hindley–Milner의 핵심은 `let`에서 타입을 **generalize**하는 것이다(`let id x = x`의 `'a -> 'a`). 교과서식 Algorithm W는 "타입의 free variable 중 환경에 없는 것"을 generalize하는데, 매번 환경 전체를 훑어야 한다. OCaml이 쓰는 방법은 **모든 type variable에 생긴 let 깊이(level)를 기록**하는 것이다. 현재 깊이보다 깊은 변수만 generalize한다. 대신 unify할 때 level을 정확히 낮춰야 하고, 이걸 빠뜨리면 잘못된 프로그램이 조용히 통과한다.

## The Insight

```
let id = (fun x -> x) in ...       // level 0 에서 let 시작
         ^^^^^^^^^^^^               // level 1 에서 추론: x 의 변수 'a 는 level 1
                                    // let 이 끝나고 level 0 으로 돌아옴
                                    // 'a 의 level(1) > 현재(0) → 바깥에서 안 쓰임 → ∀'a
```

**변수의 level = 그 변수를 볼 수 있는 가장 바깥 let의 깊이**라는 invariant만 지키면, generalization은 "현재보다 깊은 변수"를 고르는 것으로 끝난다. 이 invariant는 unification이 깬다. 깊은 변수가 얕은 변수(바깥 환경에 있는 변수)와 이어지면, 깊은 변수도 바깥에서 보이게 된다. 그래서 **이을 때 level을 낮춘다.**

```
fun f -> let g = fun y -> f y in (g 1, g true)

f : 'f (level 0, 바깥 환경)
let 안 (level 1): y : 'y (level 1), 결과 'r (level 1)
f y 를 추론하며 'f := 'y -> 'r
  → 'y, 'r 은 이제 f 를 통해 바깥에서 보임 → level 0 으로 낮춰야 함
  → 낮추지 않으면 g 의 타입 'y -> 'r 이 ∀ 로 일반화됨 → (g 1, g true) 가 통과
```

## Why This Matters

`Infer.fs`를 복사해서 **level을 낮추는 두 줄만 뺀 버전**으로 같은 식을 추론해 봤다.

| 식 | level을 낮춤 | 낮추지 않음 |
|---|---|---|
| `let id x = x in (id 1, id true)` | `int * bool` | `int * bool` |
| `fun x -> let y = x in (y 1, y true)` | 오류 | 오류 |
| `fun f -> let g = fun y -> f y in (g 1, g true)` | `Type mismatch: expected int but got bool` | **`('a -> 'b) -> 'c * 'd`** (통과) |

마지막 식에 실제 함수를 넘기면 이렇게 된다.

```
(fun f -> let g = fun y -> f y in (g 1, g true)) (fun n -> n + 1)
  낮추지 않은 추론: 'a * 'b               ← 통과
  실행:            Type error: expected int or string but got bool
```

타입 검사를 통과했는데 실행 중 타입 오류가 난다. soundness가 깨진 것이다. 단순한 예제(`id`)는 둘 다 맞게 나오므로, **level 버그는 쉬운 테스트로는 드러나지 않는다.**

## Recognition Pattern

- 타입 추론기에 `let` polymorphism을 넣을 때
- generalization이 느려서(매번 환경 전체의 free variable 계산) 개선하고 싶을 때
- "함수 인자를 거쳐서 만든 지역 함수"가 polymorphic으로 추론되는 버그를 봤을 때

## The Approach

### Step 1: type variable을 mutable record로 만든다

```fsharp
type Type =
    | TyCon of string * Type list
    | TyFun of Type * Type
    | TyTuple of Type list
    | TyVar of TypeVar

and TypeVar =
    { Id: int
      /// 단일화로 정해진 타입 (union-find 의 링크)
      mutable Link: Type option
      /// 이 변수가 생긴 let 깊이. genericLevel 이면 일반화된(∀) 변수
      mutable Level: int
      mutable Allowed: string list option }
```

- 대체(substitution) 맵 대신 **변수에 직접 링크를 단다.** unification은 `v.Link <- Some t`이고, 타입을 읽을 때는 링크를 따라간다(`prune`, 경로 압축).
- generalize된 변수는 별도의 `Scheme` 타입 없이 **`Level = genericLevel`(Int32.MaxValue)** 로 표시한다. 환경에는 그냥 `Type`이 들어간다.
- 변수 비교는 `Id`로 한다. mutable 필드가 있는 레코드를 `=`로 비교하면 구조 비교가 된다.

### Step 2: let 은 level을 올렸다 내리고 generalize한다

```fsharp
| Let (name, value, body) ->
    // let 다형성: 값의 타입을 한 단계 안에서 추론하고, 그 단계에서 생긴 변수를 ∀ 로 만듦
    st.Level <- st.Level + 1
    let tv = infer' value
    st.Level <- st.Level - 1
    generalize st tv
    infer st (withVars [ name, tv ] ctx) body
```

```fsharp
let rec private generalize (st: State) t =
    match prune t with
    | TyVar v when v.Level > st.Level && v.Level <> genericLevel && v.Allowed.IsNone -> v.Level <- genericLevel
    ...
```

`let rec`도 같다. 함수 자신의 변수를 올린 level에서 만들고, 본문을 추론한 뒤 내려와서 generalize한다.

### Step 3: unify할 때 level을 낮춘다 (빠뜨리면 안 되는 두 곳)

```fsharp
/// v 가 t 안에 있는지 확인하면서, t 안의 변수 단계를 v 의 단계 이하로 낮춤 (일반화가 맞게 되도록)
let rec private occursAdjust (v: TypeVar) t =
    match prune t with
    | TyVar other ->
        if other.Id = v.Id then true
        else
            if other.Level > v.Level then other.Level <- v.Level     // ← 1
            false
    ...

let private bindVar (v: TypeVar) t =
    match t with
    | TyVar other ->
        ...
        other.Level <- min v.Level other.Level                        // ← 2
        v.Link <- Some t
    | _ ->
        if occursAdjust v t then raise (Infinite (v, t))
        ...
```

- occurs check는 어차피 `t`를 끝까지 훑는다. 그때 level도 함께 낮추면 추가 비용이 없다.
- 변수끼리 이을 때는 살아남는 쪽(`other`)이 두 level 중 낮은 쪽을 가져야 한다.

### Step 4: 사용할 때마다 instantiate한다

```fsharp
/// ∀ 변수마다 새 변수를 만들어 복사 (허용 목록도 복사)
let private instantiate (st: State) t =
    let fresh = Dictionary<int, Type>()
    let rec go t =
        match prune t with
        | TyVar v when v.Level = genericLevel -> (* 같은 ∀ 변수는 같은 새 변수로 *)
        ...
```

`Var`, `Ctor`, 생성자 패턴에서 환경의 타입을 꺼낼 때마다 부른다.

### Step 5: 상태는 호출마다 만든다

현재 level과 변수 번호를 모듈 전역 `mutable`로 두면 **병렬로 도는 테스트끼리 level이 섞인다.** 추론 한 번마다 상태를 만들어서 넘긴다.

```fsharp
/// 추론 한 번의 상태. 테스트가 병렬로 돌기 때문에 모듈 전역 변수 대신 호출마다 만듦
type private State = { mutable Level: int; mutable NextId: int }

let typecheck (expr: Expr) : Type =
    let st = { Level = 0; NextId = 0 }
    ...
```

## Example

level 버그를 잡는 테스트는 "바깥 변수를 거쳐 만든 지역 함수"를 써야 한다.

```fsharp
expectTypeError "fun f -> (f 1, f true)" "Type mismatch: expected int but got bool"
// 단계를 낮추지 않으면 이 식이 통과한다
expectTypeError "fun f -> let g = fun y -> f y in (g 1, g true)" "Type mismatch: expected int but got bool"
// 다형성이 있어야 하는 곳
"let id x = x in (id 1, id true)", "int * bool"
"let (a, b) = (fun x -> x, 2) in (a 1, a true)", "int * bool"
```

그리고 soundness 테스트(`write-soundness-test-for-type-checker.md`)로 넓게 확인한다.

## 체크리스트

- [ ] type variable이 `Link`와 `Level`을 가진 mutable record이고, 비교는 `Id`로 한다
- [ ] `let`/`let rec`/`let` 패턴이 level을 올리고 내린 다음 generalize한다
- [ ] occurs check가 level을 낮추고, 변수끼리 이을 때 낮은 level을 남긴다
- [ ] 환경에서 타입을 꺼낼 때마다 instantiate한다
- [ ] 추론 상태가 호출마다 새로 만들어진다 (전역 `mutable` 없음)
- [ ] "바깥 변수를 거친 지역 함수"를 polymorphic으로 쓰는 식이 오류인지 테스트한다

## 관련 문서

- `handle-overloaded-operators-in-hindley-milner.md` - `+`, `<`처럼 int와 string을 함께 받는 연산자
- `write-soundness-test-for-type-checker.md` - 동적 평가 예제로 타입 추론을 검증하기
- `implement-closures-and-let-rec.md` - 같은 `let`/`let rec`의 평가 쪽 구현
