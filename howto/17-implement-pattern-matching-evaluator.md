---
order: 17
created: 2026-10-28
commit: 59f8c7e
version: 0.12.0
description: 패턴 매칭 평가는 matchPattern: Pattern -> Value -> Env -> Env option 하나로 끝난다. 불일치는 None, 묶을 이름은 env에 쌓고, 분기 선택·가드·let 분해가 모두 이 함수를 쓴다
---

# 트리 순회 인터프리터에 패턴 매칭 구현하기

> 관련 커밋: [59f8c7e](https://github.com/ohama/calc-mlir/commit/59f8c7e) (v0.12.0)

패턴 매칭의 핵심은 함수 하나다. **`matchPattern pattern value env`가 일치하면 이름을 추가한 환경을, 아니면 `None`을 돌려준다.** `match`의 분기 선택, `when` 가드, `let (a, b) = ...` 분해가 모두 이 함수 위에 올라간다. 패턴의 모양(리터럴, 리스트, 튜플, 중첩)은 이 함수의 `match` 케이스 하나씩이다.

## The Insight

패턴 매칭은 "구조 비교"와 "이름 묶기"를 **동시에** 하는 재귀다.

```
패턴  (n, s) :: _          값  [(1, "a"); (2, "b")]

PCons(PTuple[PVar n; PVar s], PWildcard)  vs  VList [VTuple [1; "a"]; ...]
  ├ PTuple[n; s]  vs  VTuple[1; "a"]        → { n = 1, s = "a" }
  │   ├ PVar n  vs  1                        → { n = 1 }
  │   └ PVar s  vs  "a"                      → { n = 1, s = "a" }
  └ PWildcard  vs  VList [(2, "b")]          → 그대로
결과: Some { ...env, n = 1, s = "a" }
```

하위 패턴 하나라도 실패하면 전체가 실패한다. 그래서 **`Option.bind`로 환경을 이어 붙이면** 실패가 자동으로 전파된다.

## Why This Matters

처음 구현(0.7.0)은 리스트 `match`를 `[]` 분기 하나와 `h :: t` 분기 하나로 고정했다. AST가 `Match of Expr * nilCase * head * tail * consCase`였고, 평가기는 `if 빈 리스트 then ... else ...`였다. 이 구조로는 다음 기능을 추가할 수 없었다.

- `[x]`, `x :: y :: _` 같은 중첩 패턴
- 튜플 패턴 `(0, _)`, 리터럴 패턴 `1 -> "one"`
- 분기 3개 이상, `when` 가드
- `let (a, b) = ...`

패턴을 **별도의 AST 타입**으로 분리하고 일치 검사를 함수 하나로 모으면, 패턴 종류 추가는 케이스 한 줄로 끝나고 패턴을 쓰는 곳(`match`, `let`)은 그 함수만 부르면 된다.

## Recognition Pattern

- `match`를 "리스트 전용", "분기 2개 고정"처럼 특수한 형태로 시작했는데 확장해야 할 때
- 튜플, algebraic data type(ADT), 레코드 같은 structural 값을 언어에 넣을 때
- `let` 분해, 함수 인자 패턴처럼 "패턴이 여러 곳에 나타나는" 언어를 만들 때

## The Approach

**패턴 = 값의 모양을 적은 데이터.** 값 타입의 케이스마다 그 값과 일치하는 패턴 케이스가 대응한다.

### Step 1: 패턴을 별도 타입으로 만든다

```fsharp
type Pattern =
    | PVar of string          // x: 무엇이든 일치하고 x 에 묶음
    | PWildcard               // _: 무엇이든 일치, 묶지 않음
    | PInt of int
    | PBool of bool
    | PString of string
    | PNil                    // []
    | PCons of Pattern * Pattern
    | PTuple of Pattern list

type Expr =
    ...
    | Match of scrutinee: Expr * cases: (Pattern * Expr option * Expr) list   // (패턴, 가드, 본문)
    | LetPattern of Pattern * Expr * Expr
```

- 리스트 패턴 `[a; b]`는 파서에서 `PCons(a, PCons(b, PNil))`로 바꾼다. 리스트 리터럴을 `Cons`로 바꾸는 것과 같다. 평가기는 `PNil`과 `PCons`만 알면 된다.
- `PVar "_"`로 와일드카드를 표현하지 않는다. `_`를 이름으로 묶으면 `(_, _)`가 "같은 이름 두 번" 검사에 걸리고, 식에서 `_`를 참조할 수 있게 된다.

### Step 2: `matchPattern` 하나로 일치와 묶기를 처리한다

```fsharp
/// 패턴이 값과 일치하면 묶을 이름들을 env 에 추가해서 반환. 타입이 달라도 오류가 아니라 불일치
let rec matchPattern pattern value (env: Env) : Env option =
    match pattern, value with
    | PVar name, v -> Some (Map.add name v env)
    | PWildcard, _ -> Some env
    | PInt n, VInt m -> if n = m then Some env else None
    | PBool b, VBool c -> if b = c then Some env else None
    | PString s, VString t -> if s = t then Some env else None
    | PNil, VList [] -> Some env
    | PCons (ph, pt), VList (h :: t) ->
        matchPattern ph h env |> Option.bind (matchPattern pt (VList t))
    | PTuple ps, VTuple vs when List.length ps = List.length vs ->
        List.fold2 (fun acc p v -> acc |> Option.bind (matchPattern p v)) (Some env) ps vs
    | _ -> None
```

- **마지막 `| _ -> None`이 "타입이 다른 경우"를 모두 불일치로 만든다.** `match "1" with 1 -> ...`는 오류가 아니라 다음 분기로 넘어간다. 동적 타입 언어에서는 이것이 자연스럽다. 정적 타입 언어라면 이 경우는 타입 검사기가 미리 막는다.
- `PTuple`에는 **길이 검사가 필요하다.** `List.fold2`는 길이가 다르면 `ArgumentException`을 던진다. 가드로 막지 않으면 `(a, b, c)` 패턴과 `(1, 2)` 값이 .NET 예외가 된다.
- 결과 환경은 **바깥 `env`에 이름을 추가한 것**이다. 그래서 패턴 변수가 바깥 이름을 가리고, 분기를 벗어나면 원래 환경으로 돌아간다.

### Step 3: 분기 선택과 가드

```fsharp
| Match (scrutinee, cases) ->
    let value = evalIn env scrutinee
    // 위에서부터 패턴과 가드가 모두 맞는 첫 분기
    let rec tryCases cases =
        match cases with
        | [] -> matchFailure value
        | (pattern, guard, body) :: rest ->
            match matchPattern pattern value env with
            | Some caseEnv when (match guard with None -> true | Some g -> evalBool caseEnv g) ->
                evalIn caseEnv body
            | _ -> tryCases rest
    tryCases cases
```

- **대상 식은 한 번만 평가한다.** 분기마다 다시 평가하면 side effect나 비용이 분기 수만큼 생긴다.
- **가드는 패턴이 묶은 환경(`caseEnv`)에서 평가한다.** `n when n > 3`의 `n`을 볼 수 있어야 한다.
- 가드가 `false`면 **다음 분기로 넘어간다.** 오류가 아니다.
- 일치하는 분기가 없으면 `Match failure: <값>`이다. 어떤 값이었는지 보여 주면 디버깅이 쉽다.

### Step 4: `let` 분해도 같은 함수를 쓴다

```fsharp
| LetPattern (pattern, value, body) ->
    let v = evalIn env value
    match matchPattern pattern v env with
    | Some bodyEnv -> evalIn bodyEnv body
    | None -> matchFailure v
```

`let (a, b) = e in body`는 분기가 하나인 `match e with (a, b) -> body`와 같다.

### Step 5: 같은 이름 두 번은 파서에서 막는다

`(a, a)`를 허용하면 `Map.add`가 뒤의 값으로 덮어써서 조용히 앞의 값을 잃는다. F#처럼 오류로 만든다. 값이 필요 없는 검사이므로 **파싱할 때** 한다.

```fsharp
let rec patternVars pattern =
    match pattern with
    | PVar name -> [ name ]
    | PCons (h, t) -> patternVars h @ patternVars t
    | PTuple items -> List.collect patternVars items
    | PWildcard | PInt _ | PBool _ | PString _ | PNil -> []
```

파서 액션에서 위치와 함께 오류를 내는 방법은 `16-report-parse-error-positions-with-fsyacc.md`의 Step 6에 있다.

## Example

```
match [1; 2; 3] with [] -> 0 | [x] -> x | x :: y :: _ -> x + y      → 3
match (1, 0) with (0, _) -> "a" | (_, 0) -> "b" | _ -> "c"          → "b"
match 5 with n when n > 3 -> "big" | n -> "small"                   → "big"
match 1 with x -> "first" | 1 -> "second"                           → "first"  (위에서부터)
match "1" with 1 -> "int" | _ -> "other"                            → "other"  (타입이 달라도 불일치)
let (a, b) = (1, 2, 3) in a                                         → Match failure: (1, 2, 3)
```

마지막 줄은 `PTuple`의 길이 가드 덕분에 .NET 예외가 아니라 언어의 오류로 나온다.

## 체크리스트

- [ ] 패턴이 `Expr`와 분리된 타입이고, 와일드카드가 별도 케이스다
- [ ] `matchPattern`이 `Env option`을 반환하고, 하위 패턴을 `Option.bind`로 잇는다
- [ ] 타입이 다른 패턴과 값은 불일치(`None`)다
- [ ] 튜플 패턴은 길이를 먼저 비교한다 (`List.fold2` 예외 방지)
- [ ] 대상 식은 한 번만 평가하고, 가드는 패턴이 묶은 환경에서 평가한다
- [ ] 일치하는 분기가 없으면 값을 보여 주는 `Match failure`를 낸다
- [ ] `let` 분해가 같은 `matchPattern`을 쓴다
- [ ] 한 패턴 안의 같은 이름은 파서에서 위치와 함께 막는다

## 관련 문서

- `10-handle-nested-match-ambiguity-in-fsyacc.md` - 분기 목록 문법의 중첩 `match` 충돌과 `%prec`
- `16-report-parse-error-positions-with-fsyacc.md` - 파서 액션에서 위치가 있는 오류 내기
- `08-implement-closures-and-let-rec.md` - 환경(`Map`)에 이름을 추가하는 같은 방식
