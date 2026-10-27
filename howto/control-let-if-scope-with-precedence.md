---
created: 2026-10-27
description: fsyacc 규칙의 우선순위는 마지막 terminal을 따른다. IN과 ELSE를 가장 낮게 선언해 let 본문과 else 분기가 오른쪽 끝까지 이어지게 한다
---

# fsyacc 우선순위로 `let`과 `if`의 본문 범위 정하기

`let x = 1 in x + 2`가 `let x = 1 in (x + 2)`로 파싱될지 `(let x = 1 in x) + 2`로 파싱될지는 문법 규칙이 아니라 **`IN` 토큰의 우선순위**가 정한다.

## The Insight

yacc 계열 도구에서 **규칙(production)에도 우선순위가 있다**. 규칙의 우선순위는 그 규칙 안에 나오는 **마지막 terminal의 우선순위**를 따른다. `%prec`로 직접 지정할 수도 있다.

```
Expr: LET IDENT EQUALS Expr IN Expr    // 마지막 terminal = IN   → 규칙 우선순위 = IN
    | IF Expr THEN Expr ELSE Expr      // 마지막 terminal = ELSE → 규칙 우선순위 = ELSE
    | MINUS Expr %prec UMINUS          // %prec 로 직접 지정
```

`let x = 1 in x` 까지 읽은 상태에서 다음 토큰이 `+`라고 하자. 파서는 둘 중 하나를 골라야 한다.

- **shift**: `+`를 읽고 본문을 `x + ...`로 늘린다.
- **reduce**: `let` 식을 여기서 끝낸다.

이 선택은 **규칙의 우선순위(IN)와 다음 토큰의 우선순위(PLUS)를 비교**해서 정해진다.

| IN의 우선순위 | 결과 | `let x = 1 in x + 2` |
|---|---|---|
| 선언하지 않음 | shift/reduce 충돌. fsyacc는 기본으로 shift를 고름 | `let x = 1 in (x + 2)` (충돌 경고 포함) |
| **가장 낮게** (`%nonassoc IN`을 맨 위에) | PLUS가 더 높으므로 shift. 충돌 없음 | `let x = 1 in (x + 2)` |
| 가장 높게 | IN이 더 높으므로 reduce. 충돌 없음 | `(let x = 1 in x) + 2` (*) |

(*) "충돌 없음"은 fsyacc로 직접 확인했다. 파싱 결과는 직접 실행해 보지 않았고, yacc의 우선순위 해소 규칙(규칙 우선순위가 더 높으면 reduce)에서 유도한 것이다.

## Why This Matters

`IN`과 `ELSE`의 우선순위를 선언하지 않으면, 이 프로젝트 문법(이항 연산자 12개)에서는 **shift/reduce 충돌이 24개** 난다. 12개 연산자 × `let`과 `if` 두 규칙이다.

```
24 shift/reduce conflicts
shift/reduce error ... on terminal PLUS between {[explicit left 9997] shift(35)}
  and {noprec reduce(Expr:'LET' 'IDENT' 'EQUALS' Expr 'IN' Expr)} - assuming the former
```

- `noprec reduce(...)`: 이 규칙에 우선순위가 없다는 뜻이다. 마지막 terminal인 `IN`에 우선순위가 없기 때문이다.
- fsyacc가 shift를 고르므로 **결과 자체는 맞다**. 하지만 충돌 경고 24개가 쌓여 있으면, 나중에 생기는 **진짜 문제 충돌을 알아볼 수 없다**.
- 반대로 `IN`을 높게 두면 충돌은 없지만 `let x = 1 in x + 2`가 `3`이 아니라 `(let x = 1 in x) + 2`가 된다. 지금 예에서는 결과가 우연히 같지만, `let x = 1 in x * 0 + 5` 같은 식에서는 의미가 달라진다.

## Recognition Pattern

- 끝을 나타내는 토큰 없이 **뒤에 식이 오는 구문**을 추가할 때. 예: `let ... in e`, `if ... else e`, `fun x -> e`, `match ... with | p -> e`
- 충돌 메시지에 `noprec reduce(Expr: 'LET' ...)`처럼 그 구문의 규칙이 등장할 때
- `-let x = 1 in x + 2`처럼 앞에 연산자가 붙은 경우에 본문이 어디까지 이어지는지 헷갈릴 때

## The Approach

**"이 구문의 마지막 식은 어디까지 이어져야 하나?"** 를 먼저 정한다. ML 계열(F#, OCaml)의 관례는 **"갈 수 있는 데까지 오른쪽으로"** 이다. 그러려면 이 구문의 마지막 terminal을 **가장 낮은 우선순위**로 선언한다.

### Step 1: 규칙의 마지막 terminal을 찾는다

| 구문 | 규칙 | 마지막 terminal |
|---|---|---|
| `let x = e1 in e2` | `LET IDENT EQUALS Expr IN Expr` | `IN` |
| `if c then e1 else e2` | `IF Expr THEN Expr ELSE Expr` | `ELSE` |

`then` 분기는 `THEN`과 `ELSE` 사이에 갇혀 있으므로 범위 문제가 생기지 않는다. 범위가 문제 되는 곳은 **맨 끝 식**뿐이다.

### Step 2: 그 terminal을 우선순위 선언 맨 위(가장 낮음)에 둔다

```
%nonassoc IN ELSE          // 가장 낮음 → 본문/else 분기가 오른쪽 끝까지
%left OR
%left AND
%left EQUALS NOTEQUAL LESS GREATER LESSEQUAL GREATEREQUAL
%left PLUS MINUS
%left STAR SLASH
%nonassoc UMINUS NOT       // 가장 높음
```

이 경우 `%nonassoc`는 문제가 없다. `IN`끼리 맞붙는 상황(`... IN Expr . IN`)이 문법상 생기지 않기 때문이다. 이항 연산자에 `%nonassoc`를 쓸 때의 함정은 `handle-fsyacc-nonassoc-conflict.md`를 참고한다.

### Step 3: 충돌 0개를 확인하고, 경계 사례를 테스트로 고정한다

```bash
dotnet build | grep -i conflict    # 비어 있어야 함
```

## Example

이 프로젝트에서 `%nonassoc IN ELSE`를 가장 낮게 두었을 때의 실제 파싱 결과다.

| 입력 | AST |
|---|---|
| `let x = 1 in x + 2` | `Let ("x", Number 1, Add (Var "x", Number 2))` |
| `if false then 1 else 2 + 3` | `If (Bool false, Number 1, Add (Number 2, Number 3))` |
| `1 + let x = 2 in x * 3` | `Add (Number 1, Let ("x", Number 2, Multiply (Var "x", Number 3)))` |
| `-let x = 1 in x + 2` | `Negate (Let ("x", Number 1, Add (Var "x", Number 2)))` |
| `(let x = 1 in x) + 10` | `Add (Let ("x", Number 1, Var "x"), Number 10)` ← 범위를 줄이려면 괄호 |

`-let x = 1 in x + 2`에서 `UMINUS`가 가장 높은데도 `Negate`가 `let` 전체를 감싸는 이유는 이렇다. 파서가 `-` 다음에 `let`을 만나면 먼저 `let` 식 전체를 끝까지 읽는다. `Negate` 규칙을 reduce하는 것은 그다음이다. `+`를 만나는 시점의 결정은 `UMINUS`가 아니라 `IN`과 `PLUS`의 비교로 정해진다.

테스트로 고정한다.

```fsharp
test "let 본문은 오른쪽으로 최대한 확장" {
    Expect.equal
        (parse "let x = 1 in x + 2")
        (Let ("x", Number 1, Add (Var "x", Number 2)))
        ""
}
test "괄호로 let 범위 제한" {
    Expect.equal
        (parse "(let x = 1 in x) + 10")
        (Add (Let ("x", Number 1, Var "x"), Number 10))
        ""
}
```

## 체크리스트

- [ ] 새 구문의 **마지막 terminal**이 무엇인지 확인했다
- [ ] 그 terminal을 의도에 맞는 위치에 선언했다 (오른쪽 끝까지 이어지게 하려면 가장 낮게)
- [ ] `dotnet build | grep -i conflict`가 비어 있다
- [ ] `e op <구문>`, `<구문> ... op e`, 괄호로 감싼 경우의 AST를 테스트로 고정했다

## 관련 문서

- `handle-fsyacc-nonassoc-conflict.md` - 이항 연산자에 `%nonassoc`를 쓰면 안 되는 이유
