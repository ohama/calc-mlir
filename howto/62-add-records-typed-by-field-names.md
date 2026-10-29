---
order: 62
created: 2026-10-29
commit: 51ec291
version: 0.36.0
description: 레코드는 필드 이름으로 타입을 찾는다. 레코드 타입과 필드 타입을 튜플 하나로 묶어 instantiate해서 type parameter를 맞추고, 값은 쓴 순서대로 계산해 선언한 순서의 블록에 넣는다. 필드 읽기를 ! 보다 강하게 하려면 문법 계층을 나눠야 충돌이 없다
---

# 필드 이름으로 타입을 정하는 레코드 넣기

> 관련 커밋: [51ec291](https://github.com/ohama/calc-mlir/commit/51ec291) (v0.36.0)

`type point = { x: int; y: int }`, `{ x = 1; y = 2 }`, `p.x`, `{ p with x = 1 }`을 HM 타입 추론, 인터프리터, 네이티브 컴파일러가 있는 언어에 넣는다. 생성자가 하나인 ADT와 비슷하지만 다른 점이 셋 있다.

- `p.x`에는 타입을 알려 주는 생성자가 없다.
- 필드를 아무 순서로 쓸 수 있다.
- 결과를 필드 이름과 함께 출력해야 한다.

## The Insight

**필드 이름이 생성자 역할을 한다.** `fun r -> r.x`에서 `r`의 타입은 아직 모른다. 하지만 `x`라는 필드를 선언한 레코드 타입은 안다. OCaml처럼 "필드 이름 → 레코드 타입" 표를 범위에 두면, `r.x`, `{ x = …; … }`, `{ r with x = … }`가 모두 생성자 적용처럼 타입을 정한다.

**순서는 두 가지다.** 사용자가 쓴 순서와 선언한 순서다. 계산은 쓴 순서대로 하고(눈에 보이는 부수 효과의 순서), 값은 선언한 순서로 놓는다(출력, 비교, 칸 번호). 두 순서를 섞지 않는 것이 인터프리터와 컴파일러의 결과를 같게 만든다.

## Why This Matters

- **type parameter를 필드마다 따로 instantiate하면** `type 'a box = { value: 'a; label: string }`에서 레코드 타입 `'a box`와 필드 `value : 'a`의 `'a`가 서로 다른 변수가 되어 아무 값이나 들어간다.
- **값을 선언한 순서로 계산하면** `{ y = f 1; x = f 2 }`의 출력 순서가 인터프리터(쓴 순서)와 컴파일러(칸 순서)에서 달라진다.
- **`!`와 `.`를 같은 문법 규칙에 두면** fsyacc가 충돌을 보고한다. 이 충돌은 빌드를 멈추지 않으므로 메시지를 찾아야 보인다.

  ```
  1 shift/reduce conflicts
  shift/reduce error at state 229 on terminal DOT between {noprec shift(238)} and {noprec reduce(Atom:'BANG' Atom)}
    - assuming the former because we prefer shift when unable to compare precedences
  ```

## Recognition Pattern

- 이름 붙은 필드, 구조체, 객체 리터럴처럼 **이름으로 칸을 찾는** 값을 HM 언어에 넣을 때
- 사용자가 쓴 순서와 저장 순서가 다를 수 있는 식(키워드 인자, 레코드, named field 생성자)
- 후위 연산자(`.f`, `[i]`)를 전위 연산자(`!`, `-`)와 함께 문법에 넣을 때

## The Approach

### Step 1: 범위에 "필드 이름 → 레코드 타입"을 둔다

```fsharp
type private Context =
    { Vars: Map<string, Type>
      Types: Map<string, string * int>
      /// 필드 이름 → (레코드 타입, 선언한 순서의 필드와 타입). 타입 매개변수는 ∀ 변수
      Fields: Map<string, Type * (string * Type) list> }
```

선언은 ADT처럼 고유 이름(`point@3`)을 만들고, type parameter를 ∀ 변수로 만든다. 그다음 필드마다 같은 항목을 넣는다. 같은 필드 이름을 가진 타입을 다시 선언하면 `Map.add`가 덮어쓰므로 마지막 선언이 이긴다(OCaml과 같음).

### Step 2: 레코드 타입과 필드 타입을 한 번에 instantiate한다

```fsharp
let private recordType (st: State) (ctx: Context) (field: string) =
    match Map.tryFind field ctx.Fields with
    | Some (t, fieldTypes) ->
        // 튜플 하나로 묶어 instantiate: 같은 ∀ 변수는 같은 새 변수가 됨
        match instantiate st (TyTuple (t :: List.map snd fieldTypes)) with
        | TyTuple (t :: types) -> t, List.zip (List.map fst fieldTypes) types
        | _ -> failwith "unreachable"
    | None -> typeError (sprintf "Unknown record field: %s" field)
```

`instantiate`를 따로 만들 필요가 없다. 기존 함수는 한 번 부르는 동안 같은 ∀ 변수를 같은 새 변수로 바꾼다. 그래서 묶어서 한 번만 부르면 된다.

세 가지 식은 모두 이 함수로 시작한다.

```fsharp
| Record fields ->                          // { x = e1; y = e2 }
    let t, fieldTypes = recordType st ctx (fst fields.Head)
    for f, e in fields do expect e (fieldOf t fieldTypes f) (infer' e)
    // 빠진 필드: Missing record field: y
    t
| Field (e, field) ->                       // e.x
    let t, fieldTypes = recordType st ctx field
    expect e t (infer' e)
    fieldOf t fieldTypes field
| RecordUpdate (e, fields) ->               // { e with x = v }
    let t, fieldTypes = recordType st ctx (fst fields.Head)
    expect e t (infer' e)
    for f, x in fields do expect x (fieldOf t fieldTypes f) (infer' x)
    t
```

- **어느 필드로 찾는가:** 첫 필드로 타입을 정하고, 나머지 필드가 그 타입에 없으면 `The record field y does not belong to type p`를 낸다. 필드 집합으로 타입을 고르는 방법(F#)은 더 복잡하고 오류 메시지가 흐려진다.
- **value restriction:** non-expansive 값만 쓴 레코드 식은 non-expansive로 둔다(`60-add-value-restriction-to-hm-with-levels.md`). 필드를 바꿀 수 없기 때문이다. 그래야 `let empty = { value = [] }`가 polymorphic하게 남는다.

### Step 3: 계산은 쓴 순서, 저장은 선언한 순서

인터프리터는 값을 쓴 순서대로 계산한 뒤 선언한 순서로 다시 놓는다.

```fsharp
| Record fields ->
    let typeName, names = recordTypeOf env (fst fields.Head)    // 환경의 "." + 필드 이름
    let values = fields |> List.map (fun (f, e) -> f, evalIn env e)
    VRecord (typeName, names |> List.map (fun n -> n, List.find (fun (f, _) -> f = n) values |> snd))
```

인터프리터도 선언한 순서를 알아야 한다. 그래서 타입 선언이 환경에 `".x"` 같은 이름으로 필드 목록을 넣는다. `.`는 렉서가 이름에 넣지 않으므로 사용자 변수와 겹치지 않는다.

컴파일러는 값을 쓴 순서대로 임시 이름에 묶고, 블록에는 선언한 순서로 넣는다.

```fsharp
| Record fields ->
    let names = scope.Fields[fst fields.Head]                    // 선언한 순서
    let block = CRecord (names, names |> List.map (fun n -> CVar ("$field_" + n)))
    List.foldBack (fun (f, e) acc -> CLet ("$field_" + f, lower e, acc)) fields block
```

`{ e with x = v }`는 `e`를 `$record`에 묶은 뒤 같은 방법으로 만든다. 바꾸지 않는 필드는 `$record`의 칸에서 꺼낸다. 이렇게 하면 `e`, 그다음 새 값이 쓴 순서대로 계산된다.

### Step 4: 컴파일한 블록에는 필드 이름 문자열을 하나만 둔다

```
[헤더 7 | ((1 + n) << 8), "x;y" 의 주소, 값1, 값2, ...]
```

- **필드 읽기는 번호로:** `p.x`는 선언한 순서의 번호를 컴파일할 때 정해서 칸 `2 + i`를 바로 읽는다. 실행 중에 이름을 찾지 않는다.
- **이름은 출력에만:** 결과 출력(`{ x = 1; y = 2 }`)에만 쓰므로 `;`로 이은 C 문자열 전역 하나로 충분하다. 생성자 이름 표를 같이 써도 겹치지 않는다. 필드 이름은 소문자로 시작하고 `;`가 들어 있기 때문이다.
- **비교:** 같은 타입끼리만 비교하므로(타입 검사가 보장) 값 칸을 차례로 비교하면 된다.

### Step 5: 후위 `.`는 문법 계층을 나눠 넣는다

OCaml은 `!p.r`을 `!(p.r)`로 읽는다. `.`가 `!`보다 강하다. 두 규칙을 같은 기호에 두면 Why This Matters의 충돌이 난다. `.`를 한 단계 아래 기호로 내린다.

```
Atom:
    | Primary                       { $1 }
    | BANG Atom                     { App(Var "!", $2) }

Primary:
    | IDENT ...
    | Primary DOT IDENT             { Field($1, $3) }          // a.b.c == (a.b).c
    | LBRACE RecordFields RBRACE    { Record ... }
    | LBRACE Primary WITH RecordFields RBRACE
```

- **충돌이 없는 이유:** `BANG Primary` 다음에 `DOT`가 오면 `Primary DOT IDENT`로 shift할 수밖에 없다. `Atom` 뒤에는 `DOT`가 올 수 없기 때문이다.
- **`with` 앞의 식:** `with` 앞에는 `Primary`(이름, 괄호, 필드 읽기)만 둔다. `{ x = 1 }`과 `{ p with … }`는 `{ IDENT` 다음 토큰(`=` 또는 `with`)으로 구분된다.
- **fsyacc의 충돌 보고:** fsyacc는 충돌이 있어도 빌드를 성공시킨다. 문법을 바꾸면 빌드 출력에서 `conflict`를 찾는다.

## Example

```
$ calc -e 'type 'a box = { value: 'a; label: string } in let b = { value = [1]; label = "l" } in (b.value, { b with label = "m" })'
Type:   int list * int list box
Result: ([1], { value = [1]; label = "m" })
```

| 입력 | 결과 |
|---|---|
| `{ y = f "y" 1; x = f "x" 2 }` (`f`는 첫 인자를 기록) | 기록 `"yx"`, 값 `{ x = 2; y = 1 }` |
| `fun r -> r.x` (선언 없음) | `Unknown record field: x` |
| `type p = { x: int; y: int } in { x = 1 }` | `Missing record field: y` |
| `type p = { x: int } in type q = { x: string } in fun r -> r.x` | `q -> string` (마지막 선언) |
| `type p = { x: int } in (1).x` | `expected p but got int` |
| `!p.r` | `!(p.r)` |

## 체크리스트

- [ ] 레코드 타입과 필드 타입을 한 번에 instantiate하는가? (`'a box`의 `'a`가 하나인가)
- [ ] 같은 필드 이름의 타입을 다시 선언하면 뒤의 것이 이기는가? (타입 추론, 인터프리터, 컴파일러 모두)
- [ ] 값은 쓴 순서대로 계산하고 선언한 순서로 저장하는가? (`{ y = …; x = … }`, `{ e with … }`)
- [ ] 빠진 필드, 다른 타입의 필드, 같은 필드 두 번을 오류로 막는가?
- [ ] 필드 읽기는 컴파일할 때 정한 번호로 하는가?
- [ ] 후위 `.`를 넣은 뒤 fsyacc 출력에 `conflict`가 없는가?

## 관련 문서

- `19-represent-adt-values-in-dynamic-interpreter.md` - ADT 값과 생성자
- `60-add-value-restriction-to-hm-with-levels.md` - 레코드 식의 non-expansive 판정
- `07-parse-function-application-in-fsyacc.md` - 함수 호출을 문법 계층으로 묶기
- `01-handle-fsyacc-nonassoc-conflict.md` - fsyacc의 충돌 보고
