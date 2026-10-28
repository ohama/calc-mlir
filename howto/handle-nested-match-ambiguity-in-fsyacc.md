---
created: 2026-10-28
description: 분기 수가 자유로운 match는 중첩될 때 dangling else와 같은 충돌을 만든다. 분기 수를 고정하거나 %prec로 shift를 명시한다
---

# fsyacc에서 중첩 `match`의 모호함 다루기

`match ... with | p1 -> e1 | p2 -> e2 ...`처럼 **분기 개수가 정해지지 않은** 문법은 `match` 안에 `match`가 들어갈 때 "다음 `|`가 어느 `match`의 분기인가?"가 모호해진다. `if-then-else`의 dangling else와 같은 문제다. fsyacc는 이것을 shift/reduce 충돌 1개로 보고하고, 빌드는 성공시킨다.

## The Insight

마지막 분기의 본문은 `Expr`이고, `Expr`은 다시 `match`일 수 있다.

```
match a with [] -> 0 | h :: t -> match b with [] -> 1 | x :: y -> 2 | ...
                                 ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^ ^
                                 안쪽 match                           이 | 는 누구 것?
```

파서가 안쪽 `match`의 분기 `x :: y -> 2`까지 읽고 `|`를 만났을 때 할 수 있는 일은 두 가지다.

- **shift**: `|`를 안쪽 `match`의 다음 분기로 받는다
- **reduce**: 안쪽 `match`를 여기서 끝내고 `|`를 바깥 `match`에 넘긴다

문법만으로는 어느 쪽인지 정할 수 없다. 이것이 충돌의 정체다. F#도 같은 문제를 안고 있어서 한 줄로 쓴 중첩 `match`는 안쪽이 뒤의 분기를 모두 가져간다. 그래서 F#에서는 "중첩 match는 괄호로 감싸라"가 관용구다.

**분기 수가 고정되어 있으면 이 질문 자체가 사라진다.** 안쪽 `match`가 분기를 두 개 다 읽었다면 다음 `|`는 바깥 것일 수밖에 없다.

## Why This Matters

이 프로젝트의 문법(0.7.0)에서 `match` 분기를 F# 스타일의 목록으로 바꾸면 이렇게 나온다.

```
Arms:
    | Arm
    | Arm BAR Arms
```

```
shift/reduce error at state 87 on terminal BAR between {noprec shift(88)} and {noprec reduce(Arms:Arm)}
 - assuming the former because we prefer shift when unable to compare precedences
1 shift/reduce conflicts
```

- 빌드는 **성공**한다. `dotnet build` 출력에서 이 한 줄을 놓치기 쉽다.
- 동작은 shift, 즉 F#과 같이 안쪽 `match`가 분기를 가져간다. 의도한 결과라도 경고가 남아 있으면 나중에 생기는 진짜 충돌이 묻힌다.
- 바깥 `match`의 분기라고 생각하고 쓴 코드가 조용히 안쪽 분기가 된다. 그러면 바깥 `match`의 분기가 모자라서 엉뚱한 곳에서 parse error가 나거나, 안쪽 `match`에 다른 분기가 붙는다.

## Recognition Pattern

- `match`, `case`, `try ... with`, `function`처럼 **`|`로 이어지는 분기 목록**을 문법에 넣을 때
- 충돌 보고에 `on terminal BAR`와 `reduce(Arms: ...)` 같은 "목록을 여기서 끝내는" 규칙이 나올 때
- `if-then-else`에서 `else`를 생략할 수 있게 만들 때 (같은 구조의 문제)

## The Approach

먼저 **언어 설계로 해결할지, 파서 설정으로 해결할지** 정한다.

| 방법 | 충돌 | 중첩 `match` 동작 | 언제 |
|---|---|---|---|
| A. 분기 수 고정 (이 프로젝트 0.7.0~0.11.0) | 0 | 분기를 다 채우면 안쪽이 끝남. 괄호 없이 중첩 가능 | 패턴 종류가 정해져 있을 때 (리스트의 `[]`/`h :: t`) |
| B. 목록 + `%prec`로 shift 명시 (이 프로젝트 0.12.0~) | 0 | 안쪽이 뒤의 분기를 모두 가져감 (F#과 같음) | 일반 패턴 매칭 |
| C. 목록 + 닫는 키워드 (`end`) | 0 | 키워드로 끝이 분명함 | 문법이 장황해져도 될 때 (OCaml의 `begin ... end`, Pascal 계열) |
| D. 목록, 아무것도 안 함 | 1 | B와 같지만 경고가 남음 | 하지 않는다 |

### Step 1: 분기 수를 고정할 수 있는지 본다

리스트에 대한 `match`는 빈 리스트와 그 밖의 경우, 두 가지뿐이다. 중첩 패턴(`1 :: t`)을 지원하지 않는다면 분기는 정확히 두 개다.

```
Expr:
    | MATCH Expr WITH OptBar MatchArms
                                    { let (nilCase, (h, t, consCase)) = $5 in Match($2, nilCase, h, t, consCase) }

// 첫 분기 앞의 | 는 생략 가능
OptBar:
    | BAR                           { () }
    |                               { () }

// 두 분기는 어느 순서로 써도 됨. 결과는 (nil 분기, cons 분기)
MatchArms:
    | NilArm BAR ConsArm            { ($1, $3) }
    | ConsArm BAR NilArm            { ($3, $1) }

NilArm:
    | LBRACKET RBRACKET ARROW Expr  { $4 }

ConsArm:
    | IDENT CONS IDENT ARROW Expr   { ($1, $3, $5) }
```

- 순서를 자유롭게 하려고 `MatchArms`에 두 규칙을 두었다. 첫 토큰(`[` 또는 `IDENT`)으로 바로 구분되므로 충돌이 없다.
- `OptBar`의 빈 규칙도 충돌이 없다. `WITH` 다음 토큰이 `BAR`면 shift하고, `[`나 `IDENT`면 빈 규칙으로 reduce한다.
- 덤으로 "분기가 하나뿐", "`[]` 분기가 둘" 같은 실수가 **parse error**로 잡힌다. 분기 누락을 평가 시점까지 미루지 않아도 된다.

### Step 2: 고정할 수 없으면 `%prec`로 shift를 명시한다

dangling else를 해결하는 표준 방법과 같다. 목록을 끝내는 규칙(`Arms: Arm`)에 `BAR`보다 낮은 우선순위를 준다.

```
%nonassoc NOBAR          // 가짜 토큰. 규칙 우선순위 표시용
%nonassoc BAR
%nonassoc IN ELSE ARROW
...

Arms:
    | Arm %prec NOBAR
    | Arm BAR Arms
```

`reduce(Arms: Arm)`의 우선순위(NOBAR)가 lookahead `BAR`보다 낮으므로 shift를 고른다. 동작은 D와 같지만 충돌 보고가 사라진다.

0.12.0에서 일반 패턴 매칭을 넣으면서 이 프로젝트도 A에서 B로 바꿨다. 분기 목록은 `Arm` 하나에 패턴과 가드를 담는다.

```
Arms:
    | Arm %prec NOBAR               { [ $1 ] }
    | Arm BAR Arms                  { $1 :: $3 }

Arm:
    | Pattern ARROW Expr            { ... }
    | Pattern WHEN Expr ARROW Expr  { ... }
```

충돌은 0개다. 대신 중첩 `match`의 의미가 바뀐다(아래 Example). 예전에 "괄호 없이 중첩 가능"이던 테스트를 "뒤의 분기는 안쪽 것"으로 고치고, CHANGELOG의 "변경"에 적었다.

`NOBAR`는 렉서가 만들지 않는 토큰이다. fsyacc에서는 `%token NOBAR`로 선언해 두어야 한다.

### Step 3: 충돌 0개를 확인하고, 중첩을 테스트로 고정한다

```bash
dotnet build | grep -i conflict    # 비어 있어야 함
```

## Example

방법 A(0.7.0~0.11.0)에서 실제로 나오는 AST다.

```fsharp
test "중첩 match: 분기가 둘뿐이므로 괄호 없이 가능" {
    Expect.equal
        (parse "match a with [] -> match b with [] -> 1 | x :: y -> 2 | h :: t -> 3")
        (Match (Var "a", Match (Var "b", Number 1, "x", "y", Number 2), "h", "t", Number 3))
        ""
}
test "match 분기가 하나면 parse error" {
    Expect.throws (fun () -> parse "match xs with [] -> 0" |> ignore) ""
}
test "같은 종류 분기 둘은 parse error" {
    Expect.throws (fun () -> parse "match xs with [] -> 0 | [] -> 1" |> ignore) ""
}
```

첫 번째 테스트에서 `| h :: t -> 3`은 바깥 `match`의 분기가 된다. 안쪽 `match`는 이미 `[]` 분기와 `x :: y` 분기를 다 가졌기 때문이다. 방법 B라면 이 입력은 안쪽 `match`에 `h :: t` 분기를 하나 더 붙이려 하고, 바깥 `match`에는 분기가 하나뿐이게 된다.

방법 B로 바꾼 뒤(0.12.0) 같은 입력의 AST다. 세 분기가 모두 안쪽 `match b`에 붙는다.

```fsharp
test "중첩 match: 뒤의 분기는 모두 안쪽 match 것 (F# 와 같음)" {
    Expect.equal
        (parse "match a with [] -> match b with [] -> 1 | x :: y -> 2 | h :: t -> 3")
        (Match (Var "a",
                [ (PNil, None,
                   Match (Var "b",
                          [ (PNil, None, Number 1)
                            (PCons (PVar "x", PVar "y"), None, Number 2)
                            (PCons (PVar "h", PVar "t"), None, Number 3) ])) ]))
        ""
}
test "중첩 match: 괄호로 감싸면 바깥 분기" { ... }
```

마지막 분기의 본문이 오른쪽 끝까지 이어지는 것은 별도의 문제다. `ARROW`를 가장 낮은 우선순위로 두어서 해결한다(`match xs with [] -> 0 | h :: t -> h + 1`의 본문은 `h + 1`).

## 체크리스트

- [ ] 분기 목록 문법을 넣은 뒤 `dotnet build | grep -i conflict`로 BAR 충돌을 확인했다
- [ ] 분기 수를 고정할 수 있는지(A) 먼저 검토했다
- [ ] 목록을 써야 한다면 `%prec`로 shift를 명시해서 충돌 보고를 없앴다(B)
- [ ] 중첩 `match`의 AST와 분기 개수 오류를 테스트로 고정했다
- [ ] 중첩 `match`가 어떻게 묶이는지 문법 문서에 적었다

## 관련 문서

- `control-let-if-scope-with-precedence.md` - 마지막 terminal로 정해지는 규칙 우선순위, 본문이 오른쪽 끝까지 이어지는 이유
- `handle-fsyacc-nonassoc-conflict.md` - fsyacc가 충돌을 보고만 하고 shift를 고르는 다른 사례
