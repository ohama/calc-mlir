---
order: 60
created: 2026-10-29
commit: ae29cc0
version: 0.35.0
description: ref를 넣으면 let-polymorphism이 soundness를 깨므로 계산 없이 정해지는 값만 generalize한다. level 기반 HM에서는 generalize하지 않는 것만으로는 부족하고 변수의 level을 지금 단계로 내려야 한다. REPL은 식 입력도 사슬에 넣고, 같은 이름을 두 번 쓴 타입으로 '_a를 찾는다
---

# level 기반 HM에 value restriction 넣기

> 관련 커밋: [ae29cc0](https://github.com/ohama/calc-mlir/commit/ae29cc0) (v0.35.0)

순수한 언어에서는 `let`으로 묶은 값의 타입을 언제나 generalize해도 된다. mutable reference(`ref`)가 들어오면 그렇지 않다.

```
let r = ref [] in        (* r : 'a list ref 를 ∀'a 로 만들면 *)
r := [1];                (* 'a = int 로 instantiate *)
r := ["a"];              (* 'a = string 으로 instantiate *)
!r                       (* 같은 칸에 int list 와 string list 를 넣었음 *)
```

`ref []`는 칸을 **하나** 만든다. 그런데 `∀'a`는 "쓸 때마다 다른 타입"이라는 뜻이므로, 한 칸에 서로 다른 타입을 넣는 것을 허락한다. 이 문서는 SML의 value restriction을 level 기반 HM(`20-implement-hindley-milner-with-levels.md`)에 넣는 방법과, 거기서 생긴 함정 두 가지를 다룬다.

## The Insight

**generalize해도 되는 것은 "계산하지 않고 정해지는 값"뿐이다.** `fun x -> …`는 부를 때마다 본문을 새로 계산하므로, 그 안에서 만드는 `ref`도 부를 때마다 새 칸이다. 그래서 함수의 타입은 generalize해도 된다. 반면 `ref []`, `id id`처럼 **함수 호출이 들어간 식**은 한 번 계산한 결과를 모든 사용처가 나눠 쓴다. 이런 식을 non-expansive가 아니라고(expansive) 하고, generalize하지 않는다.

level 기반 HM에서는 한 가지가 더 필요하다. **generalize하지 않는 변수는 level을 지금 단계로 내려야 한다.** 내리지 않으면 그 변수는 깊은 level에 남아 있다가, 안쪽 `let`이 generalize할 때 함께 ∀가 된다.

## Why This Matters

- **value restriction이 없으면** 위 예제가 타입 검사를 통과한다. 인터프리터는 동적 검사가 있어 `Type error`로 멈추지만, 컴파일한 코드는 int를 문자열 포인터로 읽는다.
- **generalize만 건너뛰고 level을 내리지 않으면** 구멍이 남는다. `restrict`를 빼고 실험한 결과다.

  ```
  $ calc -e 'let r = ref [] in let f x = r := [x]; x in (f 1, f "a")'
  Type:   int * string               (* 통과하면 안 됨 *)
  $ calc -e 'let r = ref [] in let f x = r := [x] in f 1; f "a"; !r'
  Type:   'a list                    (* 실제 값은 ["a"]. 아무 타입으로나 쓸 수 있음 *)
  Result: ["a"]
  ```

  `r`의 `'a`는 level 2에서 생겼다. 바깥 `let r`이 generalize하지 않고 지나가면 `'a`는 level 2에 그대로 남는다. 그 상태에서 `f`의 인자 `x`(level 2)와 unify된다. 안쪽 `let f`는 level 1에서 "level이 1보다 깊은 변수"를 generalize하므로 `'a`까지 ∀가 된다.
- **REPL은 따로 깨진다.** 입력마다 앞의 **선언**만 `let … in` 사슬로 붙여 다시 추론하면, `r := [1]` 같은 **식** 입력이 정한 타입이 다음 입력에서 사라진다. 그러면 다음 입력의 `r := ["a"]`가 통과한다.

## Recognition Pattern

- HM 언어에 `ref`, 배열, mutable 필드처럼 **칸을 만드는** 기능을 넣을 때
- level(또는 rank)로 generalize하는 추론기에서 "이 `let`은 generalize하지 않는다"는 규칙을 더할 때
- 선언을 다시 추론하는 방식의 REPL에서, 앞 입력이 정한 타입이 다음 입력에 남아야 할 때

## The Approach

### Step 1: non-expansive인 식을 문법으로 정한다

SML처럼 문법으로만 판정한다. 값을 계산하지 않아도 모양만 보고 알 수 있어야 한다.

```fsharp
let rec private isNonExpansive expr =
    match expr with
    | Number _ | Bool _ | Str _ | Unit | Var _ | Lambda _ | Ctor _ | Nil | Printf _ -> true
    | Annot (e, _) -> isNonExpansive e
    | Tuple items -> List.forall isNonExpansive items
    | Cons (h, t) -> isNonExpansive h && isNonExpansive t
    // 생성자에 값을 준 것 (Some [], Node (l, x, r)). 생성자는 칸을 만들 뿐 내용을 바꿀 수 없음
    | App (Ctor _, arg) -> isNonExpansive arg
    | Let (_, value, body) -> isNonExpansive value && isNonExpansive body
    | LetRec (_, _, _, inExpr) | LetRecs (_, inExpr) -> isNonExpansive inExpr
    | _ -> false
```

- **보수적으로:** `if`, `match`, 연산자는 모두 expansive로 둔다. generalize를 덜 하는 쪽은 타입 오류가 늘 뿐 soundness를 깨지 않는다.
- **`App (Ctor _, …)`만 예외:** 생성자 적용은 함수 호출처럼 보이지만 칸을 바꾸지 않는다. `let none = (None, [])` 같은 값이 계속 polymorphic하도록 넣는다.
- **기존 코드 확인:** 이 규칙은 `let f = id id`처럼 partial application의 결과도 monomorphic하게 만든다(OCaml, F#과 같음). 넣은 뒤 테스트 942개 가운데 깨진 것은 `!`의 타입을 이름으로 찾던 테스트 하나였다. prelude는 모두 `let f x = …` 모양이라 영향이 없었다.

### Step 2: generalize하지 않을 때는 level을 내린다

```fsharp
/// generalize 하지 않는 값의 변수를 지금 단계로 내림. 바깥 let 이 generalize 할 수 있는지는 바깥 let 이 정함
let rec private restrict (st: State) t =
    match prune t with
    | TyVar v -> v.Level <- min v.Level st.Level
    | TyCon (_, args) -> List.iter (restrict st) args
    | TyFun (a, b) ->
        restrict st a
        restrict st b
    | TyTuple items -> List.iter (restrict st) items
```

`let`의 처리는 한 줄만 바뀐다.

```fsharp
| Let (name, value, body) ->
    st.Level <- st.Level + 1
    let tv = infer' value
    st.Level <- st.Level - 1
    if isNonExpansive value then generalize st tv else restrict st tv   // 전에는 generalize st tv
    infer st (withVars [ name, tv ] ctx) body
```

level을 내린 변수는 `let`의 본문 level과 같아진다. 그래서 본문 안의 어떤 `let`도 그 변수를 generalize하지 않는다. 그러나 **그 `let` 전체를 감싼 바깥 `let`은 여전히 generalize할 수 있다.** 이것이 맞는 동작이다.

```
let mk = fun () -> let r = ref [] in r in      (* 안쪽 let r 은 restrict *)
let a = mk () in a := [1];                     (* 바깥 let mk 는 fun 이라 generalize: mk : unit -> 'a list ref *)
let b = mk () in b := [true]                   (* 부를 때마다 새 칸이므로 sound *)
```

`let (a, b) = …`의 패턴 `let`에도 같은 판정을 넣는다. 빠뜨리면 `let (r, n) = (ref [], 0)`이 구멍이 된다.

### Step 3: REPL은 식 입력도 사슬에 넣는다

REPL이 입력마다 앞의 항목을 `let … in` 사슬로 붙여 다시 추론한다면, 식 입력도 이름 없는 `let`으로 넣는다.

```fsharp
let rec private chain (items: TopItem list) (body: Expr) =
    match items with
    | [] -> body
    | TopExpr e :: rest -> Let ("$it", e, chain rest body)     // 식 입력: 사용자가 쓸 수 없는 이름
    | item :: rest -> Prelude.chain [ item ] (chain rest body)
```

`e; rest`(`Seq`)로 넣지 않는 이유는 "버리는 값이 unit이 아님" 경고가 앞 입력마다 다시 계산되기 때문이다. `let $it = e`는 경고가 없고, `e`가 expansive면 Step 2의 `restrict`를 그대로 탄다. 평가는 하지 않으므로 앞 입력의 `print`가 다시 나오지 않는다.

### Step 4: REPL에서 generalize하지 않은 변수를 `'_a`로 보여 준다

`val r : 'a list ref`로 보여 주면 사용자는 `r`이 polymorphic하다고 읽는다. OCaml은 `'_weak1`로 보여 준다. 그런데 추론이 끝난 타입만 보고는 이 변수가 generalize한 것을 instantiate한 것인지, 처음부터 generalize하지 않은 것인지 알 수 없다. 둘 다 level 0의 변수다.

**같은 이름을 두 번 쓰면 구분된다.** generalize한 변수는 쓸 때마다 새 변수가 되고, generalize하지 않은 변수는 두 번 다 **같은** 변수다.

```fsharp
let private bindingType (declarations: TopItem list) (name: string) =
    match Infer.typecheck (chain declarations (Tuple [ Var name; Var name ])) |> Infer.prune with
    | Infer.TyTuple [ first; second ] -> Infer.weakTypeToString first second   // second 에도 있는 변수는 '_a
    | t -> Infer.typeToString t
```

```
# let r = ref [];;
val r : '_a list ref = ref []
# let g = id id;;
val g : '_a -> '_a = <fun>
# r := [1];;
- : unit = ()
# r;;
- : int list ref = ref [1]
# r := ["a"];;
Error: Type mismatch: expected int list but got string list (line 1, column 6)
```

## Example

| 프로그램 | 결과 |
|---|---|
| `let r = ref [] in r := [1]; r := ["a"]` | `expected int list but got string list` |
| `let r = ref [] in let f x = r := [x]; x in (f 1, f "a")` | `expected int but got string` (Step 2가 없으면 통과) |
| `let id x = x in let f = id id in (f 1, f "a")` | `expected int but got string` |
| `let (r, n) = (ref [], 0) in r := [1]; r := [true]` | `expected int list but got bool list` |
| `let f () = ref [] in let a = f () in let b = f () in a := [1]; b := ["x"]; (!a, !b)` | `int list * string list` |
| `let xs = [] in (1 :: xs, "a" :: xs)` | `int list * string list` |
| `let p = (None, []) in …` | `None`, `[]`이 모두 polymorphic |

## 체크리스트

- [ ] 함수 호출이 들어간 `let` 값은 generalize하지 않는가? (`ref []`, `id id`)
- [ ] generalize하지 않을 때 변수의 level을 지금 단계로 내리는가? (`let r = ref [] in let f x = r := [x]; x in …`)
- [ ] 패턴 `let`(`let (a, b) = …`)에도 같은 판정을 넣었는가?
- [ ] 함수 안에서 만드는 `ref`는 바깥 `let`이 여전히 generalize하는가? (`let mk () = ref []`)
- [ ] 생성자 적용, 튜플, 리스트 리터럴은 non-expansive인가? (기존 프로그램이 깨지지 않게)
- [ ] REPL이 식 입력의 타입 제약을 다음 입력에 넘기는가?
- [ ] REPL이 generalize하지 않은 변수를 `'a`가 아닌 표시로 보여 주는가?

## 관련 문서

- `20-implement-hindley-milner-with-levels.md` - level 기반 generalization
- `45-implement-repl-by-reinferring-declarations.md` - 선언을 사슬로 붙이는 REPL
- `61-desugar-loops-into-tail-recursion.md` - 같은 릴리스의 반복문
