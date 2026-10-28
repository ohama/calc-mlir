---
order: 52
created: 2026-10-28
commit: 83c6071
version: 0.30.0
description: map, fold 같은 함수를 인터프리터(F#)와 런타임(C)에 따로 두지 않고 언어 자신으로 쓴 prelude를 프로그램 앞에 let 사슬로 붙인다. 쓰는 정의만 이름을 따라 골라 붙이고, tail recursion으로 쓰고, 예외나 overloading이 필요한 함수는 내장 함수로 남긴다. 긴 리스트에서는 느려지므로 재서 기록한다
---

# 표준 라이브러리를 언어 자신으로 쓴 prelude로 붙이기

> 관련 커밋: [83c6071](https://github.com/ohama/calc-mlir/commit/83c6071) (v0.30.0)

인터프리터와 네이티브 컴파일러가 함께 있는 언어는 내장 함수를 두 번 구현해야 한다. Calc도 `map`, `fold`, `range`가 F#(`Eval.callBuiltin`)과 C(`builtins.c`)에 하나씩, 타입은 `Infer`에 또 하나 있었다. 함수를 하나 더할 때마다 세 곳을 고치고, 두 구현이 같은지는 differential testing으로만 알 수 있었다.

OCaml의 `Stdlib`, Haskell의 `Prelude`처럼 **언어 자신으로 쓰면** 정의가 하나가 된다.

```
// src/Calc/prelude.calc
let rec fold f acc xs = match xs with [] -> acc | x :: rest -> fold f (f acc x) rest
let reverse xs = fold (fun acc x -> x :: acc) [] xs
let map f xs = reverse (fold (fun acc x -> f x :: acc) [] xs)
let sum (xs: int list) : int = fold (fun acc x -> acc + x) 0 xs
```

## The Insight

**prelude는 "프로그램 앞에 붙는 선언"일 뿐이다.** 최상위 선언을 이미 `let … in` 사슬로 풀어 쓰고 있다면(`43-design-top-level-declarations-and-sequencing-in-fsyacc.md`), prelude도 같은 사슬로 붙이면 된다. 그 뒤로는 사용자 함수와 구별되지 않는다.

```
Ast.Expr ──▶ Prelude.wrap ──▶ let rec fold … in let reverse … in let map … in <프로그램>
                                   │
                    ┌──────────────┼──────────────┐
                    ▼              ▼              ▼
              Infer (타입)    Eval (해석)    Lower → Codegen (컴파일)
```

- 타입 추론은 prelude의 타입을 다른 `let`처럼 추론하고 generalize한다. 타입을 따로 적어 둘 필요가 없다.
- closure conversion은 prelude 함수를 보통 클로저로 만든다. 런타임에 C 함수가 필요 없다.
- 프로그램이 같은 이름을 정의하면 lexical scope 규칙대로 가린다. prelude 안에서 서로 부르는 이름은 바뀌지 않는다.

## Why This Matters

- **내장 함수를 두 언어로 구현할 때:** 동작 차이가 생기기 쉽다. 함수를 부르는 순서, 짧게 끝내는 조건, overflow 같은 것이다. 이 프로젝트도 차이를 찾으려고 differential testing을 두었다(`26-write-differential-tests-for-compiler.md`).
- **prelude를 통째로 붙일 때:** 모든 프로그램이 prelude 전체를 타입 추론하고 평가하고 컴파일한다. 테스트 수백 개가 각각 MLIR 함수 수십 개를 더 만들게 된다.
- **재귀를 그대로 쓸 때:** `let rec map f xs = match xs with [] -> [] | x :: t -> f x :: map f t`는 리스트 길이만큼 스택이 자란다. 내장 함수일 때는 F# `List.map`과 C 루프라서 없던 한계가 생긴다.

## Recognition Pattern

- 같은 내장 함수를 인터프리터와 런타임(또는 여러 백엔드)에 따로 구현하고 있을 때
- 내장 함수가 다른 함수를 부르는 고차 함수일 때(`map`, `filter`, `fold`)
- 언어에 타입 표기, 최상위 선언처럼 라이브러리를 쓸 수 있는 기능이 생겼을 때

## The Approach

### Step 1: 무엇을 옮길지 정한다

언어로 쓸 수 있고, 결과와 타입이 그대로인 함수만 옮긴다. 이 프로젝트에서는 16개를 옮기고 20개를 남겼다.

| 남긴 함수 | 이유 |
|---|---|
| `head`, `tail`, `nth` | 실행 중 오류(`head: empty list`)를 내야 한다. Calc에는 아직 예외(`failwith`)가 없다 |
| `length` (string 또는 list), `sort` (int 또는 string) | 여러 타입을 받는다. Calc로 쓰면 allowed list가 있는 type variable이 generalize되지 않아, 첫 사용이 타입을 정해 버린다 |
| 문자열 함수, `toString`, `toInt`, `print` | UTF-16 처리, 값 출력처럼 언어 밖의 기능이다 |

남긴 함수가 생기는 이유는 대부분 **언어에 아직 없는 기능**이다. 예외나 type class가 생기면 더 옮길 수 있다.

### Step 2: tail recursion으로 쓴다

긴 리스트에서도 스택이 자라지 않도록, 결과를 거꾸로 모은 뒤 `reverse`한다. 함수는 앞 원소부터 호출되므로 `print`의 순서가 F# `List.map`과 같다.

```
let map f xs = reverse (fold (fun acc x -> f x :: acc) [] xs)
let rec exists f xs = match xs with [] -> false | x :: rest -> if f x then true else exists f rest
```

`exists`는 `f x || exists f rest`가 아니라 `if`로 쓴다. 인터프리터의 `||`는 결과를 `VBool`로 감싸서, 오른쪽 호출이 tail position이 아니다.

인터프리터 자체도 tail call을 해야 한다. F# 인터프리터라면 Debug 빌드에서 이것이 꺼져 있다(`53-enable-fsharp-tailcalls-in-debug-for-interpreters.md`).

### Step 3: 쓰는 정의만 이름을 따라 붙인다

프로그램에 나오는 이름에서 시작해, prelude를 **뒤에서부터** 보면서 필요한 정의를 고른다. 고른 정의가 쓰는 이름도 필요한 이름에 더한다.

```fsharp
let wrap (program: Expr) : Expr =
    let needed, _ =
        List.foldBack
            (fun item (kept, names: Set<string>) ->
                let defines, uses = (* 정의하는 이름, 본문에 나오는 Var 이름 *)
                if defines |> List.exists names.Contains then item :: kept, Set.union names uses
                else kept, names)
            (items ())
            ([], varNames program)
    chain needed program
```

`varNames`는 식에 나오는 `Var` 이름을 모두 모은다. 묶인 이름인지는 따지지 않는다. 사용자가 `let map = …`을 정의하고 쓰면 prelude의 `map`도 붙지만, 가려져서 쓰이지 않을 뿐 결과는 같다. 정확한 free variable 계산보다 단순하고, 틀려도 손해는 정의가 하나 더 붙는 것뿐이다.

`map f xs`를 쓰면 `fold`, `reverse`, `map`이 prelude의 순서대로 붙는다. 아무것도 쓰지 않는 `1`은 그대로 `Number 1`이다. 그래서 파서 테스트의 AST는 바뀌지 않는다.

### Step 4: 파싱과 붙이기를 나눈다

```fsharp
let parse = Frontend.parse                               // AST 그대로 (파서 테스트, AST: 출력)
let load (input: string) = Prelude.wrap (parse input)    // 실행, 타입 검사, 컴파일
```

- 타입 검사, 평가, 컴파일 테스트의 도우미만 `load`로 바꾼다. AST 모양을 보는 테스트는 `parse` 그대로 둔다.
- 오류 위치는 바뀌지 않는다. 사용자 노드의 위치는 사용자 입력에서 기록되고, prelude는 따로 파싱하기 때문이다.
- prelude 소스는 `EmbeddedResource`로 넣고, 한 번만 파싱해서 모든 프로그램이 같은 노드를 쓴다(`lazy`).
- REPL은 처음 상태에 prelude 전체를 선언으로 넣는다. 값은 모두 함수라서 출력 없이 바로 만들 수 있다.

### Step 5: 검사와 비용을 테스트로 고정한다

- prelude 전체가 **경고 없이** 타입 검사를 통과한다. prelude의 경고가 사용자 프로그램에 섞이면 안 된다.
- 각 함수의 타입이 예전 내장 함수와 같다. 타입 표기가 필요한 곳(`sum`의 `+`)을 이 테스트가 찾아 준다.
- prelude의 이름이 내장 함수 목록에 남아 있지 않다(F#과 C 구현을 정말 지웠는지).
- 긴 리스트(`length (range 1 1000000)`)가 인터프리터와 컴파일한 실행 파일에서 모두 통과한다.

### Step 6: 느려진 만큼 재서 기록한다

원소마다 F#이나 C 루프 대신 클로저를 부르므로 느려진다. 100만 개 리스트에 `range`, `map`, `filter`, `sum`, `exists`를 쓰는 프로그램으로 재면 다음과 같다.

| | 내장 함수 (0.29.0) | prelude (0.30.0) |
|---|---|---|
| 인터프리터 (Release) | 0.77초 | 5.0초 |
| 컴파일한 실행 파일 | 0.04초 | 0.23초 |

두 버전을 같은 조건에서 재려면 `git worktree add <폴더> HEAD`로 이전 커밋을 따로 빌드한다. 컴파일러 쪽은 알려진 함수를 direct call로 부르는 최적화로 줄일 수 있다. 정의가 하나라는 이점과 바꾼 비용이므로 CHANGELOG와 문서에 숫자를 남긴다.

## Example

```
$ calc -e 'let reverse xs = xs in map (fun x -> x) [1; 2]'
Result: [1; 2]                       ← prelude 의 map 은 prelude 의 reverse 를 씀

$ calc --no-typecheck -e 'fst 1'
Error: Match failure: 1              ← 예전에는 Type error: expected pair but got int
```

타입 검사를 건너뛰고 잘못 쓰면 오류가 prelude 코드 안에서 난다. 그래서 메시지가 달라진다. 타입 검사를 통과한 프로그램에서는 생기지 않는다.

## 체크리스트

- [ ] 옮긴 함수의 타입이 예전 내장 함수와 같은가?
- [ ] prelude 전체가 경고 없이 타입 검사를 통과하는가?
- [ ] 쓰는 정의만 붙이는가? (안 쓰는 프로그램의 AST가 그대로인가?)
- [ ] 모두 tail recursion인가? 인터프리터가 Debug에서도 tail call을 하는가?
- [ ] 사용자 정의가 prelude 안의 이름을 바꾸지 않는가?
- [ ] F#, C, 타입 표의 옛 구현을 모두 지웠는가?
- [ ] 느려진 정도를 재서 기록했는가?

## 관련 문서

- `43-design-top-level-declarations-and-sequencing-in-fsyacc.md` - 선언을 `let … in` 사슬로 풀어 쓰기
- `40-resolve-builtins-by-scope-when-compiling.md` - 범위에 없는 이름만 내장 함수
- `51-scope-annotation-type-variables-by-let-level.md` - prelude에서 쓰는 타입 표기
- `53-enable-fsharp-tailcalls-in-debug-for-interpreters.md` - Debug 빌드의 tail call
