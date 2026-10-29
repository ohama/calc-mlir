---
order: 67
created: 2026-10-29
commit: f5d0ac4
version: 0.41.0
description: 배열 문법([| |], a.[i], a.[i] <- v)은 AST 노드 대신 사용자가 쓸 수 없는 이름의 내장 함수 호출로 바꾸면 타입 추론, 평가기, Lower를 고치지 않는다. 그런 이름과 점이 있는 이름은 C 함수 이름으로 바꾸고, 같은 C 함수는 MLIR에 한 번만 선언한다. 나머지 함수는 prelude의 모듈로 쓴다
---

# 배열 문법을 내장 함수 호출로 넣기

> 관련 커밋: [f5d0ac4](https://github.com/ohama/calc-mlir/commit/f5d0ac4) (v0.41.0)

인터프리터, HM 타입 추론, 네이티브 컴파일러가 있는 언어에 F#의 배열을 넣는다.

- **문법:** `[| 1; 2 |]`, `a.[i]`, `a.[i] <- v`
- **함수:** `Array.length`, `Array.init`, `Array.map`

새 AST 노드는 만들지 않았다. 바뀐 곳은 렉서와 파서, 내장 함수 표, C 런타임이다.

## The Insight

**문법은 그대로 두고, 뜻은 내장 함수에 맡긴다.** 파서가 세 가지 문법을 내장 함수 호출로 바꾼다. 함수 이름은 사용자가 쓸 수 없는 것을 고른다.

```
[| a; b |]      ==>  App (Var "[||]", [a; b])                  // 리스트를 받아 배열을 만듦
a.[i]           ==>  App (App (Var ".[]", a), i)
a.[i] <- v      ==>  App (App (App (Var ".[]<-", a), i), v)
```

내장 함수는 모든 단계가 이미 다룬다.

- **타입 추론:** 처음 환경에 타입을 넣으면 된다(`".[]", 'a array -> int -> 'a`).
- **평가기:** `callBuiltin`에 한 줄씩 더한다.
- **Lower:** 인자가 다 모인 호출을 런타임 호출로 만든다. 값으로 쓰면 클로저가 된다.
- **가리기:** 렉서가 `[||]`, `.[]`를 이름으로 만들 수 없으므로 사용자가 이 함수를 가릴 수 없다.

0.35.0의 `!r`, `r := v`도 같은 방법이었다(`60-add-value-restriction-to-hm-with-levels.md`).

## Why This Matters

- **AST 노드로 넣으면 고칠 곳이 많다.** 렉서와 파서 다음에 Prelude의 이름 수집, 모듈 이름 바꾸기, 타입 추론, 평가기, Lower, Core IR, Codegen이 모두 새 노드를 알아야 한다. 레코드는 그렇게 넣었다(`62-add-records-typed-by-field-names.md`). 배열 연산은 "값 몇 개를 받아 값을 돌려주는 것"이라 노드가 필요 없다.
- **C 함수 이름이 될 수 없는 이름이 생긴다.** Codegen은 내장 함수 `name`을 `calc_builtin_<name>`으로 불렀다. `.[]`, `Array.length`는 C 이름이 아니다.
- **두 이름이 같은 C 함수를 쓰면 MLIR이 거부한다.** 배열 리터럴과 `Array.ofList`는 하는 일이 같다.

  ```
  error: redefinition of symbol named 'calc_builtin_Array_ofList'
  ```

## Recognition Pattern

- 새 문법이 "값을 받아 값을 내는 연산"일 때 (인덱스, 대입, 컬렉션 리터럴, 연산자)
- 백엔드가 여럿이라 AST 노드를 더하는 비용이 클 때
- 표준 라이브러리 함수를 `Module.function` 이름으로 두고 싶을 때

## The Approach

### Step 1: 렉서와 파서에서 내장 함수 호출로 바꾼다

```
| "[|"          { LBRACKBAR }
| "|]"          { BARRBRACK }
| "<-"          { LARROW }
```

```
Primary:
    | Primary DOT LBRACKET SeqExpr RBRACKET   { App(App(Var ".[]", $1), $4) }
    | LBRACKBAR BARRBRACK                     { App(Var "[||]", Nil) }
    | LBRACKBAR ListItems BARRBRACK           { App(Var "[||]", makeList $2) }
Expr:
    | Expr LARROW Expr                        { makeArraySet pos $1 $3 }   // %right COLONEQUALS LARROW
```

- **`makeArraySet`:** 왼쪽이 `App (App (Var ".[]", a), i)`인지 보고, 아니면 `The left side of <- must be an array element a.[i]` 오류를 낸다. 파서가 이미 만든 노드의 모양으로 판단하므로 문법 규칙을 따로 둘 필요가 없다.
- **가장 긴 일치의 부작용:** `<-`를 토큰으로 만들면 `x<-1`이 `x < -1`이 아니라 `x <- 1`이 된다(F#과 같음). 바꾸기 전에 테스트 입력에서 `<-` 문자열을 찾아 영향을 확인한다.

### Step 2: 내장 함수 표에 타입과 동작을 더한다

```fsharp
// Infer: 처음 환경
"[||]", listOf a ^-> arrayOf a
".[]", arrayOf a ^-> tInt ^-> a
".[]<-", arrayOf a ^-> tInt ^-> a ^-> tUnit
"Array.length", arrayOf a ^-> tInt

// Eval: callBuiltin
| ".[]", [ VArray items; i ] ->
    let i = int' i
    if i < 0 || i >= items.Length then failwith "Index was outside the bounds of the array."
    items[i]
```

- **value restriction은 그대로 맞는다.** `let a = [||]`는 함수 호출(`App`)이므로 non-expansive가 아니다. 그래서 generalize하지 않는다.
- **이름으로 타입을 확인하는 테스트:** "모든 내장 함수에 타입이 있음" 같은 테스트는 이름을 입력으로 쓴다. 쓸 수 없는 이름은 문법으로 바꿔 넣는다(`".[]" -> "fun a i -> a.[i]"`).

### Step 3: C 함수 이름으로 바꾸고, 선언은 C 이름마다 한 번

```fsharp
let private builtinSymbol (name: string) =
    match name with
    | "[||]" | "Array.ofList" -> "calc_builtin_Array_ofList"
    | ".[]" -> "calc_builtin_array_get"
    | ".[]<-" -> "calc_builtin_array_set"
    | _ -> "calc_builtin_" + name.Replace(".", "_")

for symbol, arity in
    Eval.builtinArity
    |> Seq.filter (...)
    |> Seq.map (fun (KeyValue (name, arity)) -> builtinSymbol name, arity)
    |> Seq.distinct do
    sprintf "llvm.func @%s(%s) -> i64" symbol (...)
```

C 런타임의 배열은 블록 `[헤더, 원소...]`(종류 8)이고, 범위 검사는 런타임 함수가 한다. 인터프리터와 같은 문장으로 끝내야 differential testing이 통과한다.

### Step 4: 나머지 함수는 prelude의 모듈로 쓴다

```
module Array =
    let init (n: int) f = Array.ofList (map f (range 0 (n - 1)))
    let iter f a =
        for i = 0 to Array.length a - 1 do
            f a.[i]
    let map f a = Array.ofList (map f (Array.toList a))
```

- **prelude도 모듈을 펼친다.** prelude를 파싱한 뒤 모듈 이름 바꾸기(`63-implement-modules-as-renaming-after-parsing.md`)를 거치게 하면, 이 모듈은 `Array.init`, `Array.map` 같은 이름의 선언이 된다.
- **프로그램 쪽:** 프로그램이 쓴 `Array.init`은 선언하지 않은 모듈의 이름이라 바뀌지 않는다. `Prelude.wrap`이 이름으로 그 정의를 찾아 붙인다.
- **모듈 안의 `map`:** 리스트의 `map`이다. 모듈의 `map`은 그 정의 다음부터만 보인다. 그래서 같은 이름을 쓰는 정의는 순서에 주의한다.
- **남는 제한:** prelude의 모듈은 프로그램의 모듈 표에 없다. 그래서 `open Array`는 `Unknown module: Array`다.

## Example

```
let a = [| 3; 1; 2 |]
a.[0] <- 10
println (a, Array.length a, a.[1] + a.[2])      (* ([|10; 1; 2|], 3, 3) *)
let squares = Array.init 5 (fun i -> i * i)     (* [|0; 1; 4; 9; 16|] *)
a.[5]                                           (* Error: Index was outside the bounds of the array. *)
```

| 단계 | 고친 것 |
|---|---|
| 렉서, 파서 | 토큰 3개, 규칙 4개, `makeArraySet` |
| 타입 추론, 평가기 | 내장 함수 7개의 타입과 동작, 값 `VArray` |
| Lower | 없음 |
| Codegen | `builtinSymbol`, 선언 중복 제거 |
| C 런타임 | 블록 종류 8의 출력과 비교, 런타임 함수 6개 |
| prelude | `module Array` |

## 체크리스트

- [ ] 새 문법이 값 연산이면 사용자가 쓸 수 없는 이름의 내장 함수 호출로 바꿨는가?
- [ ] 대입의 왼쪽처럼 모양이 정해진 자리는 파서가 만든 노드의 모양으로 확인하는가?
- [ ] 새 토큰이 가장 긴 일치로 기존 입력의 뜻을 바꾸지 않는지 확인했는가? (`x<-1`)
- [ ] C 이름이 될 수 없는 내장 함수 이름을 런타임 함수 이름으로 바꾸는가?
- [ ] 같은 C 함수를 쓰는 내장 함수를 MLIR에 한 번만 선언하는가?
- [ ] 런타임 오류 문장이 인터프리터와 컴파일러에서 같은가?
- [ ] `Module.f` 이름의 표준 함수를 prelude의 모듈로 썼다면 prelude도 모듈을 펼치는가?

## 관련 문서

- `52-write-standard-library-as-prelude-in-the-language.md` - prelude와 `Prelude.wrap`
- `63-implement-modules-as-renaming-after-parsing.md` - 모듈을 `M.f` 이름의 선언으로 펼치기
- `40-resolve-builtins-by-scope-when-compiling.md` - Lower가 내장 함수를 범위로 찾는 방법
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
