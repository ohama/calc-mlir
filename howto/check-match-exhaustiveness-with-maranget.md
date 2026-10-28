---
created: 2026-10-28
description: 빠진 match 분기는 Maranget의 "usefulness" 검사 하나로 판정한다. 패턴 행렬의 첫 열을 타입의 생성자 목록으로 펼치고, 생성자가 다 나오지 않았으면 기본 행렬로 넘긴다. 같은 재귀가 덮지 못하는 값의 예까지 만든다. 가드가 있는 분기는 세지 않고, 실행 중 Match failure가 나는 예제는 모두 경고받는지로 soundness를 테스트한다
---

# Maranget 알고리즘으로 빠진 match 분기와 값의 예 찾기

OCaml과 F#은 `match`가 모든 값을 덮지 못하면 경고하고, 덮지 못하는 값의 예까지 보여 준다.

```
Warning 8 [partial-match]: this pattern-matching is not exhaustive.
Here is an example of a case that is not matched: None
```

이것은 Luc Maranget의 "Warnings for pattern matching"(JFP 2007)에 나온 **usefulness** 검사로 만든다. "패턴 행렬 P가 덮지 못하는 값이 있는가"를 재귀로 묻고, 있으면 그 값을 돌려준다.

## The Insight

**패턴을 "생성자 + 하위 패턴" 하나로 통일하면 알고리즘은 30줄이다.**

```fsharp
type private Pat =
    | Any                         // 변수, _
    | Con of string * Pat list    // true, [], ::, tuple, Some, 사용자 생성자, "int:5", "str:a"
```

타입마다 **생성자 전부(signature)** 를 알면 된다. 값이 무한히 많은 타입은 `None`이다.

| 타입 | 생성자 |
|---|---|
| `bool` | `true`, `false` |
| `'a list` | `[]`, `::` (하위 타입: `'a`, `'a list`) |
| `'a option` | `None`, `Some` (`'a`) |
| 튜플 | `tuple` 하나 (원소 타입들) |
| 사용자 타입 `'a tree` | 선언한 생성자들. 인자 타입의 `'a`를 실제 타입으로 바꿈 |
| `int`, `string` | 없음 (무한) |

`missing rows types`는 행렬 `rows`가 덮지 못하는 값의 예(열마다 하나)를 돌려준다.

1. **열이 없으면:** 행이 하나도 없으면 아무 값이나 덮지 못한 것이므로 `Some []`이고, 행이 있으면 `None`입니다.
2. **첫 열에 타입의 생성자가 모두 나왔으면:** 생성자 `c`마다 "첫 열이 `c`인 값"만 남긴 행렬(**specialize**)로 재귀합니다. `c`의 하위 패턴들이 새 열이 됩니다. 어느 하나라도 예가 나오면 `c (그 예)`가 답입니다.
3. **나오지 않은 생성자가 있으면(또는 무한 타입이면):** 첫 열이 `_`인 행만 남긴 **기본 행렬**로 나머지 열을 검사합니다. 예가 나오면 첫 열에는 나오지 않은 생성자를 놓습니다.

## Why This Matters

- **"마지막 분기가 `_`인가"만 본 경우:** `(true, _) | (false, _)`처럼 `_` 없이 다 덮는 경우를 잘못 경고합니다. 반대로 중첩 패턴(`Some (Some _) | None`)에서 빠진 `Some None`을 놓칩니다.
- **값의 예가 없는 경우:** "not exhaustive"만 보고는 무엇이 빠졌는지 찾기 어렵습니다. 예가 있으면 바로 분기를 더할 수 있습니다.
- **가드를 센 경우:** `| x when x > 0 -> …`만 있는 `match`를 다 덮었다고 판단합니다. 그러면 경고 없이 Match failure가 납니다.

## Recognition Pattern

- ADT와 `match`가 있는 언어에 정적 검사를 더할 때
- 실행 중 `Match failure`를 실행 전에 알리고 싶을 때
- 같은 틀로 쓰이지 않는 분기(OCaml Warning 11) 검사도 만들 계획일 때. 분기 i가 "앞 분기들의 행렬에 대해 유용한가"를 물으면 됩니다.

## The Approach

### Step 1: 타입 추론이 끝난 뒤에 검사한다

`match`를 추론하는 동안에는 대상의 타입이 아직 다 정해지지 않았을 수 있다(`fun x -> match x with ...`). 추론 중에는 (대상 타입, 패턴과 가드 여부, 위치)만 모아 두고, 모든 unification과 기본 타입 결정이 끝난 뒤 한꺼번에 검사한다. 사용자 타입의 생성자 목록도 `type` 선언을 추론할 때 모아 둔다.

```fsharp
st.Matches.Add((ts, cases |> List.map (fun (p, g, _) -> p, Option.isSome g), Errors.positionOf expr, "match"))
...
let typecheckWithWarnings expr =
    let t = infer st (initialContext st) expr
    applyDefaults st t
    t, checkMatches st
```

### Step 2: usefulness 검사

```fsharp
let rec private missing st (rows: Pat list list) (types: Type list) : Pat list option =
    match types with
    | [] -> if List.isEmpty rows then Some [] else None
    | t :: rest ->
        let heads = rows |> List.choose (function Con (name, _) :: _ -> Some name | _ -> None) |> Set.ofList
        let specialize name arity =
            rows |> List.choose (function
                | Con (n, args) :: tail when n = name -> Some (args @ tail)
                | Any :: tail -> Some (List.replicate arity Any @ tail)
                | _ -> None)
        let defaultRows = rows |> List.choose (function Any :: tail -> Some tail | _ -> None)
        match signature st t with
        | Some ctors when ctors |> List.forall (fun (name, _) -> heads.Contains name) ->
            ctors |> List.tryPick (fun (name, argTypes) ->
                missing st (specialize name argTypes.Length) (argTypes @ rest)
                |> Option.map (fun w -> Con (name, List.take argTypes.Length w) :: List.skip argTypes.Length w))
        | ctors ->
            missing st defaultRows rest
            |> Option.map (fun w -> (* 첫 열: _ 또는 나오지 않은 생성자 *) head :: w)
```

### Step 3: 값의 예를 읽기 좋게 고른다

- **열에 생성자가 하나도 없을 때:** 어떤 값이든 되므로 `_`를 씁니다. 처음에는 "나오지 않은 첫 생성자"를 골라서 `None :: []`처럼 쓸데없이 구체적인 예가 나왔습니다. `_`로 바꾸니 `None :: _`가 되었습니다.
- **무한 타입:** 나오지 않은 상수를 고릅니다. `int`는 0, 1, 2, … 중 처음 빠진 것, `string`은 `""`, `"a"`, `"aa"`, … 중 처음 빠진 것입니다.
- **출력:** 소스 문법으로 씁니다. 끝이 `[]`인 cons 사슬은 `[_]`, 인자 있는 생성자 인자는 괄호(`Some (Some _)`)로 씁니다.

| 패턴 | 값의 예 |
|---|---|
| `\| Some x -> x` | `None` |
| `\| (true, _) -> 1 \| (false, 0) -> 2` | `(false, 1)` |
| `\| [a; b] -> a \| [] -> 0` | `[_]` |
| `\| Some (Some _) -> 1 \| None -> 0` | `Some None` |
| `\| Node (Leaf, _) -> 0 \| Leaf -> 1` | `Node (Node _, _)` |
| `\| 0 -> 0 \| 1 -> 1` | `2` |

### Step 4: 가드가 있는 분기는 행렬에서 뺀다

가드는 실행해 봐야 안다. OCaml과 F#처럼 가드가 있는 분기는 덮는 데 세지 않는다. `match 1 with | x when x > 0 -> 1`은 `_`를 예로 경고한다.

### Step 5: soundness를 테스트한다

경고가 없는 프로그램은 실행 중에 `Match failure`가 나면 안 된다. 평가기 예제를 모두 돌려서, Match failure가 나는 예제는 모두 경고를 받았는지 확인한다.

```fsharp
for input in inputs do
    match (try Some (typecheckWithWarnings ast) with TypeError _ -> None) with
    | Some (_, ws) ->
        let failed = try Eval.eval ast |> ignore; false with ex -> ex.Message.StartsWith "Match failure"
        if failed then Expect.isNonEmpty ws input
    | None -> ()
```

평가 예제 263개 중 경고가 난 것은 하나였고, 실제로 덮지 못하는 패턴이었다. `let (h :: t, n) = ...`의 예는 `([], _)`이다.

## Example

```
$ calc -e "match [1; 2] with | [a; b] -> a | [] -> 0"
Warning: this match is not exhaustive. Example of a value not matched: [_] (line 1, column 1)
  1 | match [1; 2] with | [a; b] -> a | [] -> 0
    | ^
Type:   int
Result: 1
```

경고는 표준 에러로 나가고 실행은 계속된다(OCaml, F#과 같음).

## 체크리스트

- [ ] 검사를 타입 추론이 끝난 뒤에 하는가? (대상 타입이 다 정해진 뒤)
- [ ] 타입마다 생성자 전부를 알고, 사용자 타입의 인자 타입을 실제 타입으로 바꾸는가?
- [ ] `int`, `string`처럼 무한한 타입은 기본 행렬로만 판정하는가?
- [ ] 가드가 있는 분기를 행렬에서 뺐는가?
- [ ] 값의 예가 읽기 좋은가? (빈 열은 `_`, 리스트 문법, 괄호)
- [ ] soundness 테스트가 있는가? (Match failure가 나면 반드시 경고)

## 관련 문서

- `implement-pattern-matching-evaluator.md` - 인터프리터의 패턴 매칭
- `compile-match-with-scf-if-conditions.md` - 컴파일러의 패턴 매칭과 Match failure 경로
- `track-source-positions-without-changing-ast.md` - 경고의 위치
- `write-soundness-test-for-type-checker.md` - 같은 방식의 soundness 테스트
