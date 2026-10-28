---
order: 45
created: 2026-10-28
commit: 32d4e7c
version: 0.26.0
description: HM 타입 추론이 있는 언어의 REPL은 타입 환경을 따로 들고 다니지 않아도 된다. 지금까지의 선언을 let ... in 사슬로 새 입력 앞에 붙여 다시 추론하고, 값은 이전 환경을 이어서 쓴다. 추론은 부수 효과가 없어 다시 해도 되고, 평가는 다시 하면 print가 되풀이되기 때문이다
---

# 선언을 다시 추론하는 방식으로 REPL 만들기

> 관련 커밋: [32d4e7c](https://github.com/ohama/calc-mlir/commit/32d4e7c) (v0.26.0)

REPL(OCaml toplevel, `dotnet fsi`)은 입력마다 결과를 보여 주고, 앞에서 정의한 이름을 다음 입력에서도 쓸 수 있게 한다.

```
# let id y = y;;
val id : 'a -> 'a = <fun>
# (id 1, id true);;
- : int * bool = (1, true)
```

이를 위해 보통은 타입 추론기에 "지금까지의 타입 환경"을 넘겨받는 API를 따로 만든다. 그런데 이미 `let … in`을 추론할 수 있다면 더 쉬운 길이 있다. **타입과 값을 다르게 다룬다.**

| | 방식 | 이유 |
|---|---|---|
| 타입 | 지금까지의 선언을 새 입력 앞에 `let … in` 사슬로 붙여 **다시 추론** | 추론은 부수 효과가 없어 몇 번을 다시 해도 결과가 같다 |
| 값 | 이전 평가 환경(`Map<string, Value>`)을 **이어서** 씀 | 다시 평가하면 앞 입력의 `print`가 되풀이되고, 비싼 계산도 다시 한다 |

## The Insight

**추론기의 입력을 바꾸면 추론기를 바꿀 필요가 없다.** 다음 두 입력이 있다고 하자.

```
# let id y = y;;
# (id 1, id true);;
```

두 번째 입력의 타입은 다음 식의 타입과 같다.

```
let id y = y in (id 1, id true)
```

let-polymorphism은 `let … in`이 이미 처리한다. 그래서 `id`가 입력을 넘어서도 `'a -> 'a`로 유지된다. 타입 선언도 마찬가지다. `type 'a tree = …;;` 다음 입력은 `type 'a tree = … in (새 입력)`을 추론하면 된다. 추론기는 한 줄도 바꾸지 않았다.

값은 다르다. `let x = println "once"; 5;;`를 다음 입력마다 다시 평가하면 `once`가 매번 찍힌다. 그래서 평가는 입력마다 한 번만 하고, 결과를 환경에 더해 다음 입력에 넘긴다.

```fsharp
type State =
    { Declarations: TopItem list   // 타입: 다시 추론할 때 앞에 붙임
      Env: Eval.Env }              // 값: 이어서 씀
```

## Why This Matters

- **타입 환경 API를 따로 만든 경우:** generalization의 level, 사용자 타입의 고유 이름, allowed list 변수의 기본값 같은 추론기 내부 상태를 REPL이 들고 다녀야 합니다. 추론기의 불변식이 깨지기 쉽습니다.
- **값도 다시 평가한 경우:** 앞 입력의 출력이 되풀이되고, 오래 걸리는 정의(`let table = 큰 계산;;`)가 입력마다 다시 실행됩니다.
- **경고를 그대로 보여 준 경우:** 앞 선언에서 이미 보여 준 "빠진 분기" 경고가 입력할 때마다 다시 나옵니다.

## Recognition Pattern

- 식 기반 언어(ML 계열)에 REPL을 더할 때
- 추론기가 이미 `let … in`과 타입 선언을 처리할 때
- 평가에 부수 효과(출력, 파일)가 있을 때

## The Approach

### Step 1: 입력을 최상위 항목 목록으로 파싱한다

프로그램 파일은 항목들을 식 하나(`let … in` 사슬)로 풀어 쓰지만, REPL은 항목마다 결과를 보여 줘야 한다. 파서에 항목 목록을 그대로 돌려주는 두 번째 시작 기호(`startItems`)를 둔다(`46-export-types-for-a-second-fsyacc-start-symbol.md`).

### Step 2: 선언 목록을 식 앞에 붙이는 함수

```fsharp
let private chain (declarations: TopItem list) (body: Expr) =
    List.foldBack (fun item rest ->
        match item with
        | TopLet (name, value, _) -> Let (name, value, rest)
        | TopLetRec (name, param, b, _) -> LetRec (name, param, b, rest)
        | TopLetPattern (pattern, value, _) -> LetPattern (pattern, value, rest)
        | TopType (name, typeParams, ctors, _) -> TypeDecl (name, typeParams, ctors, rest)
        | TopExpr _ -> rest)            // 식 항목은 이름을 묶지 않으므로 뺌
        declarations body
```

### Step 3: 항목마다 무엇을 추론하고 무엇을 평가하는지 정한다

| 항목 | 타입 | 값 | 보여 주는 것 |
|---|---|---|---|
| 식 `e` | `chain 앞선언 e` | `evalIn env e` | `- : int = 3` |
| `let x = e` | `chain (앞선언 @ [이것]) (Var x)` | `evalIn env e`를 `x`로 더함 | `val x : int = 10` |
| `let rec f p = b` | `chain (…) (Var f)` | `VRecClosure (f, p, b, env)` | `val f : int -> int = <fun>` |
| `let (a, b) = e` | 이름마다 `chain (…) (Var a)` | `matchPattern` | `val a : …`, `val b : …` |
| `type t = …` | `chain (…) Unit` (선언이 올바른지만) | 생성자를 환경에 더함 | `type t` |

### Step 4: 경고는 새로 생긴 것만

새 입력을 붙인 사슬의 경고에서, 앞 선언만으로 된 사슬의 경고를 **개수로** 뺀다. 같은 모양의 경고가 새 선언에서 또 나와도 빠뜨리지 않는다.

```fsharp
let seen = warningsOf baseExpr |> List.countBy id |> dict |> Dictionary
[ for w in warningsOf expr do
      match seen.TryGetValue w with
      | true, n when n > 0 -> seen[w] <- n - 1     // 앞에서 이미 보여 준 것
      | _ -> yield w ]
```

### Step 5: 오류가 나면 상태를 바꾸지 않는다

항목 하나가 실패하면 오류를 보여 주고, 그 항목과 뒤 항목은 버린다. 상태는 마지막으로 성공한 항목까지만 바뀐다. `let y = head [];;`가 실패한 뒤 `y;;`를 입력하면 `Unbound variable: y`다.

### Step 6: 비용을 잰다

다시 추론하는 비용은 선언 수에 비례하므로, 전체로는 선언 수의 제곱으로 늘어난다. `let xN = N;;`을 넣어 잰 결과는 다음과 같다(dotnet 시작 시간 포함).

| 선언 수 | 전체 시간 |
|---|---|
| 100 | 0.13초 |
| 300 | 0.23초 |
| 1,000 | 0.87초 |

대화형으로 쓰는 범위에서는 충분하다. 수만 줄을 붙여 넣는 용도라면 그때 타입 환경을 들고 다니는 방식으로 바꾼다.

## Example

```
# let x = println "once"; 5;;
once
val x : int = 5
# x + 1;;
- : int = 6                     ← once 가 다시 찍히지 않음
# let f o = match o with Some v -> v;;
Warning: this match is not exhaustive. Example of a value not matched: None (line 1, column 11)
...
val f : 'a option -> 'a = <fun>
# f (Some 1);;
- : int = 1                     ← 경고를 되풀이하지 않음
```

## 체크리스트

- [ ] 타입은 다시 추론하고 값은 이어서 쓰는가?
- [ ] 식 항목은 사슬에서 빼는가? (이름을 묶지 않음)
- [ ] 경고를 개수로 빼서 새로 생긴 것만 보여 주는가?
- [ ] 실패한 항목의 정의가 상태에 남지 않는가?
- [ ] 선언이 많을 때의 비용을 쟀는가?

## 관련 문서

- `43-design-top-level-declarations-and-sequencing-in-fsyacc.md` - REPL 입력과 같은 최상위 문법
- `44-redirect-interpreter-output-per-test-with-asynclocal.md` - REPL 출력을 `TextWriter`로 받기
- `20-implement-hindley-milner-with-levels.md` - 다시 추론에 쓰는 추론기
