---
order: 49
created: 2026-10-28
commit: 92e9d88
version: 0.28.0
description: 쓰이지 않는 match 분기는 Maranget의 usefulness U(P, q) 하나로 찾는다. 분기 q가 앞의 가드 없는 분기 행렬 P가 덮지 못하는 값과 맞을 수 없으면 쓰이지 않는다. 빠진 분기 검사와 같은 펼치기(specialize)와 기본 행렬을 쓰고, 위치는 분기 튜플에 기록해 _ 분기도 가리킨다
---

# usefulness로 쓰이지 않는 match 분기 찾기

> 관련 커밋: [92e9d88](https://github.com/ohama/calc-mlir/commit/92e9d88) (v0.28.0)

```
match true with true -> 1 | false -> 2 | _ -> 3
                                           ^ 이 분기는 절대 선택되지 않음
```

OCaml은 Warning 11(`this match case is unused`), F#은 FS0026(`This rule will never be matched`)으로 이런 분기를 알려 준다. 대부분 오타나 순서 실수다. 예를 들어 `| _ ->`를 앞에 두었거나, 더 구체적인 패턴을 일반적인 패턴 뒤에 두었을 때 생긴다.

## The Insight

**빠진 분기 검사와 쓰이지 않는 분기 검사는 같은 질문의 두 모양이다.** Maranget의 usefulness `U(P, q)`는 "행렬 P의 어떤 행도 맞지 않으면서 q에는 맞는 값이 있는가"를 묻는다.

| 검사 | 질문 |
|---|---|
| 빠진 분기 | `U(모든 가드 없는 분기, _)`: 어떤 분기에도 맞지 않는 값이 있는가? |
| 쓰이지 않는 분기 i | `U(분기 1..i-1 중 가드 없는 것, 분기 i)`: 앞 분기들이 놓친 값 중에 분기 i가 맞는 값이 있는가? 없으면 쓰이지 않음 |

빠진 분기 검사를 이미 만들었다면(`42-check-match-exhaustiveness-with-maranget.md`), 펼치기(specialize)와 기본 행렬을 그대로 쓰고 q의 첫 열을 경우별로 다루기만 하면 된다.

## Why This Matters

- **"앞에 `_`가 있는가"만 본 경우:** `| [] -> 0 | x :: _ -> x | [y] -> y`의 `[y]`처럼, `_` 없이 앞 분기들이 합쳐서 덮는 경우를 놓칩니다.
- **가드를 덮는 데 센 경우:** `| x when x > 0 -> 1 | 1 -> 2`에서 두 번째 분기를 쓰이지 않는다고 잘못 경고합니다. 가드는 실패할 수 있습니다.
- **위치를 패턴 노드에서만 찾은 경우:** `_`는 값이 하나뿐인 노드라 위치가 없습니다. 가장 흔한 쓰이지 않는 분기인 `| _ ->`를 가리키지 못하고 `match` 전체를 가리키게 됩니다.

## Recognition Pattern

- 빠진 분기 경고는 있는데 쓰이지 않는 분기 경고가 없을 때
- 패턴 순서 실수로 생긴 버그를 실행 전에 잡고 싶을 때
- or 패턴, 상수 패턴, 중첩 패턴이 섞여 있을 때

## The Approach

### Step 1: U(P, q)는 q의 첫 열에 따라 세 경우

```fsharp
let rec private useful st (rows: Pat list list) (q: Pat list) (types: Type list) : bool =
    let rows = List.collect expand rows                 // 행렬 쪽 or 패턴은 행 여러 개로
    match q, types with
    | [], _ -> List.isEmpty rows                        // 열이 없음: 남은 행이 없어야 q 가 새 값과 맞음
    | Or (a, b) :: qtail, _ -> useful st rows (a :: qtail) types || useful st rows (b :: qtail) types
    | qhead :: qtail, t :: rest ->
        match qhead with
        | Con (name, args) ->                           // q 가 생성자: 그 생성자로 펼침
            useful st (specialize name args.Length) (args @ qtail) (argTypes @ rest)
        | Any ->
            match signature st t with
            | Some ctors when (P 의 첫 열에 생성자가 모두 나옴) ->
                ctors |> List.exists (fun (name, argTypes) ->
                    useful st (specialize name argTypes.Length) (List.replicate argTypes.Length Any @ qtail) (argTypes @ rest))
            | _ -> useful st (기본 행렬: 첫 열이 _ 인 행만) qtail rest
```

- **q의 첫 열이 생성자 `c`:** P에서 첫 열이 `c`이거나 `_`인 행만 남기고, `c`의 하위 패턴을 열로 펼친다.
- **q의 첫 열이 `_`:** P의 첫 열에 그 타입의 생성자가 모두 나오면, 생성자마다 나눠 묻는다. 아니면 첫 열이 `_`인 행만으로 나머지 열을 묻는다.
- **q의 첫 열이 `p1 | p2`:** 둘 중 하나라도 쓸모가 있으면 쓸모가 있다.

`int`, `string` 상수(`"int:5"`)도 생성자처럼 펼친다. 타입의 생성자 목록(`signature`)이 없으므로 하위 열은 없다.

### Step 2: 분기를 차례로 보며 앞 분기를 쌓는다

```fsharp
let mutable before = []
for p, guarded, casePosition in cases do
    if not (useful st before [ toPat p ] [ t ]) then
        yield { Message = "this match case is unused"; Position = casePosition |> Option.orElse position }
    if not guarded then before <- before @ [ [ toPat p ] ]     // 가드가 있는 분기는 덮는 데 세지 않음
```

가드가 있는 분기 자체도 검사한다. `| _ -> 0 | x when x > 0 -> 1`의 두 번째 분기는 가드와 상관없이 선택되지 않는다.

### Step 3: 위치는 분기 튜플에 기록한다

파서의 분기 규칙이 만드는 튜플 `(패턴, 가드, 본문)`은 분기마다 새로 만들어지는 객체다. 여기에 패턴의 시작 위치를 기록하면, 패턴이 `_`여도 그 분기를 가리킬 수 있다(`41-track-source-positions-without-changing-ast.md`의 위치 표).

```
Arm:
    | Pattern ARROW SeqExpr         { at parseState (checkPattern (parseState.InputStartPosition 1) $1, None, $3) }
```

```fsharp
cases |> List.map (fun ((p, g, _) as case) -> p, Option.isSome g, Errors.positionOf case)
```

### Step 4: 기존 예제에 잘못된 경고가 없는지 본다

평가 예제 263개에 돌려 경고를 모두 확인했다. 나온 경고는 하나였다. `match 1 with x -> "first" | 1 -> "second"`의 두 번째 분기인데, 첫 분기가 이긴다는 것을 시험하려고 일부러 넣은 예제라 실제로 쓰이지 않는다. 잘못된 경고는 없었다.

## Example

| 예 | 경고하는 분기 | 이유 |
|---|---|---|
| `\| true -> 1 \| false -> 2 \| _ -> 3` | `_` | `true`, `false`가 모두 덮음 |
| `\| [] -> 0 \| x :: _ -> x \| [y] -> y` | `[y]` | `x :: _`가 덮음 |
| `\| Some _ -> 1 \| Some 2 -> 2 \| None -> 0` | `Some 2` | `Some _`가 덮음 |
| `\| 1 \| 2 -> 0 \| 2 -> 1 \| _ -> 9` | `2` | or 패턴이 덮음 |
| `\| x when x > 0 -> 1 \| 1 -> 2 \| _ -> 3` | 없음 | 가드는 실패할 수 있음 |

```
$ calc -e "match 1 with _ -> 0 | 1 -> 1"
Warning: this match case is unused (line 1, column 23)
  1 | match 1 with _ -> 0 | 1 -> 1
    |                       ^
```

## 체크리스트

- [ ] 빠진 분기 검사와 같은 펼치기, 기본 행렬, 생성자 목록을 쓰는가?
- [ ] q의 첫 열이 or 패턴이면 두 쪽 중 하나라도 쓸모 있으면 쓸모 있다고 보는가?
- [ ] 가드가 있는 앞 분기를 덮는 데 세지 않는가?
- [ ] `_` 분기도 위치를 가리키는가? (분기 튜플에 위치 기록)
- [ ] 기존 예제에 잘못된 경고가 없는지 확인했는가?

## 관련 문서

- `42-check-match-exhaustiveness-with-maranget.md` - 같은 알고리즘으로 빠진 분기 찾기
- `41-track-source-positions-without-changing-ast.md` - 노드 참조로 위치 기록하기
- `47-compile-or-patterns-with-multi-result-scf-if.md` - or 패턴
