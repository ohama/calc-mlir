---
created: 2026-10-27
description: 연산자 없이 붙여 쓰는 함수 호출(f x y)은 우선순위 선언으로 표현할 수 없다. AppExpr/Atom 문법 계층으로 표현한다
---

# fsyacc로 함수 호출 `f x y` 파싱하기

`f x y`처럼 **연산자 토큰 없이 붙여 쓰는** 함수 호출은 `%left`나 `%prec`로 다룰 수 없다. 문법을 계층으로 나눠서 "호출의 인자 자리에는 원자식(atom)만 온다"고 규칙으로 정한다.

## The Insight

yacc의 우선순위 선언은 **토큰**에 붙는다. 충돌이 나면 "규칙의 마지막 토큰"과 "다음 토큰"의 우선순위를 비교해서 해소한다.

- `a + b`: `+`라는 토큰이 있으므로 `%left PLUS`로 해결된다.
- `f x`: 사이에 **토큰이 없다**. 우선순위를 붙일 대상이 없다.

그래서 함수 호출을 `Expr: Expr Expr`로 쓰면 파서는 `f x + 1`, `- f x`, `f x y` 같은 입력에서 어디서 끊어야 할지 정할 근거가 없다. 대신 문법 구조로 우선순위를 표현한다.

```
Expr     ::= AppExpr | Expr + Expr | - Expr | let ... | fun ... | ...
AppExpr  ::= AppExpr Atom        ← 왼쪽 재귀 = 왼쪽 결합
           | Atom
Atom     ::= NUMBER | IDENT | true | false | ( Expr )
```

- 이항 연산자의 피연산자는 `Expr`이고, 호출의 인자는 `Atom`뿐이다. 그래서 **호출이 어떤 연산자보다 먼저 묶인다**.
- `AppExpr Atom`은 왼쪽 재귀다. 그래서 `f x y`는 자연스럽게 `(f x) y`가 된다. 커링에 필요한 모양이다.

## Why This Matters

이 프로젝트 문법에서 `AppExpr`/`Atom` 계층을 없애고 `Expr: Expr Expr`로 바꾸면 fsyacc 결과가 다음과 같다.

```
184 shift/reduce conflicts
 15 reduce/reduce conflicts
```

- shift/reduce 충돌은 fsyacc가 shift로 "해결"하고 넘어간다. 그래서 빌드는 성공하지만 어떤 식이 어떻게 묶이는지 알 수 없다.
- reduce/reduce 충돌은 먼저 나온 규칙을 고른다. 문법 순서에 따라 의미가 바뀐다.
- 이 상태에서는 나중에 진짜 문제가 되는 충돌이 199개 속에 묻힌다.

계층으로 바꾸면 충돌은 **0개**다.

## Recognition Pattern

- ML 계열(F#, OCaml, Haskell) 문법처럼 괄호와 쉼표 없이 호출하는 문법을 만들 때
- 호출 규칙을 추가했더니 충돌이 수십~수백 개 생겼을 때
- `%prec`로 호출의 우선순위를 정하려다가 붙일 토큰이 없다는 걸 깨달았을 때

## The Approach

**"무엇이 인자 자리에 괄호 없이 올 수 있나?"** 를 먼저 정한다. 그 집합이 `Atom`이다. `Atom`이 아닌 것은 모두 괄호로 감싸야 인자가 된다.

### Step 1: 원자식을 `Atom`으로 옮긴다

리터럴, 변수, 괄호식처럼 **스스로 끝이 분명한 식**을 `Atom`으로 옮긴다.

```
Atom:
    | NUMBER                        { Number $1 }
    | TRUE                          { Bool true }
    | FALSE                         { Bool false }
    | IDENT                         { Var $1 }
    | LPAREN Expr RPAREN            { $2 }
```

`fun`, `let`, `if`는 넣지 않는다. 이들은 본문이 오른쪽 끝까지 이어지므로 끝이 분명하지 않다.

### Step 2: `AppExpr`를 만들고 `Expr`에서 연결한다

```
Expr:
    | AppExpr                       { $1 }
    | Expr PLUS Expr                { Add($1, $3) }
    | MINUS Expr %prec UMINUS       { Negate $2 }
    ...

// 함수 호출: 왼쪽 결합 (f x y == (f x) y)
AppExpr:
    | AppExpr Atom                  { App($1, $2) }
    | Atom                          { $1 }
```

기존 우선순위 선언(`%left PLUS` 등)은 그대로 둔다. 호출은 선언 없이 계층만으로 가장 높은 우선순위를 가진다.

### Step 3: 충돌 0개를 확인하고, 경계 사례를 테스트로 고정한다

```bash
dotnet build | grep -i conflict    # 비어 있어야 함
```

## Example

계층 방식에서 실제로 나오는 AST다.

| 입력 | AST | 설명 |
|---|---|---|
| `f x y` | `App (App (f, x), y)` | left-associative |
| `f x + g y` | `Add (App (f, x), App (g, y))` | 호출이 `+`보다 먼저 |
| `-f x` | `Negate (App (f, x))` | 호출이 단항 `-`보다 먼저 |
| `f (g x)` | `App (f, App (g, x))` | 괄호식은 Atom |
| `f -1` | `Subtract (f, Number 1)` | ⚠️ 뺄셈. `-1`은 Atom이 아님 |
| `f (-1)` | `App (f, Negate (Number 1))` | 음수 인자는 괄호로 |
| `ap fun x -> x` | `parse error` | `fun`은 Atom이 아님 |
| `ap (fun x -> x)` | `App (ap, Lambda (...))` | 괄호로 감싸면 됨 |

**`f -1` 주의:** F#은 `f -1`(마이너스 뒤에 공백 없음)을 호출로, `f - 1`을 뺄셈으로 해석한다. 렉서 단계에서 공백 정보를 쓰는 특별 규칙이다. 공백을 버리는 렉서로는 이 구분을 할 수 없다. 이 프로젝트는 항상 뺄셈으로 해석하고 문서에 명시했다.

이 동작을 테스트로 고정한다.

```fsharp
test "함수 호출이 이항 연산보다 우선" {
    Expect.equal
        (parse "f x + g y")
        (Add (App (Var "f", Var "x"), App (Var "g", Var "y")))
        ""
}
test "f -1 은 뺄셈" {
    Expect.equal (parse "f -1") (Subtract (Var "f", Number 1)) ""
}
```

## 체크리스트

- [ ] 호출 규칙이 `Expr Expr`가 아니라 `AppExpr Atom`이다
- [ ] `AppExpr`가 왼쪽 재귀다 (`AppExpr Atom`, `Atom AppExpr` 아님)
- [ ] `Atom`에는 끝이 분명한 식만 있다 (리터럴, 변수, 괄호식)
- [ ] `dotnet build | grep -i conflict`가 비어 있다
- [ ] `f x y`, `f x + 1`, `-f x`, `f -1`의 AST를 테스트로 고정했다

## 관련 문서

- `control-let-if-scope-with-precedence.md` - `fun x -> body`의 본문 범위를 `ARROW` 우선순위로 정하기
