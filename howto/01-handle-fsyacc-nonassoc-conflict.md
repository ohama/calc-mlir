---
order: 1
created: 2026-10-27
commit: a0ebeba
version: 0.4.0
description: fsyacc의 %nonassoc는 연쇄 비교를 막지 못한다. 충돌을 보고하고 조용히 right-associative로 파싱한다
---

# fsyacc에서 `%nonassoc`로 연쇄 비교 막기가 안 될 때

> 관련 커밋: [a0ebeba](https://github.com/ohama/calc-mlir/commit/a0ebeba) (v0.4.0)

ocamlyacc나 bison과 달리 fsyacc의 `%nonassoc`는 "같은 우선순위끼리 연달아 쓰면 문법 오류"라는 뜻으로 동작하지 않는다. 연쇄 비교를 정말 막으려면 문법을 계층으로 나누고, 허용해도 된다면 `%left`를 쓴다.

## The Insight

교과서(yacc, bison, ocamlyacc)에서 `%nonassoc`는 다음 뜻이다.

> 같은 우선순위의 연산자가 연달아 나오면 그 자리에서 **parse error**로 만든다.

이 동작은 파싱 테이블에 **error 액션**을 넣어서 구현한다. fsyacc(FsLexYacc 11.3.0)는 이 error 액션을 만들지 않는다. 같은 우선순위에서 associativity가 정해지지 않은 경우를 **shift/reduce 충돌로 보고하고, 기본 규칙대로 shift를 고른다**. shift를 고르면 결과적으로 **right-associative**가 된다.

```
1 < 2 < 3
  bison/ocamlyacc %nonassoc  → parse error
  fsyacc %nonassoc           → 1 < (2 < 3)    ← 충돌 경고만 내고 그대로 진행
```

`%nonassoc`가 아무 문제 없이 동작하는 경우도 있다. 단항 마이너스(`%nonassoc UMINUS`)나 `let ... in`(`%nonassoc IN`)처럼, **같은 우선순위끼리 맞붙는 상황이 문법상 생기지 않으면** 충돌도 생기지 않는다. 문제는 `E LT E`처럼 같은 연산자가 양쪽에 올 수 있는 이항 연산자에 `%nonassoc`를 붙였을 때만 생긴다.

## Why This Matters

**빌드가 성공한다.** 이게 제일 위험하다. 이 프로젝트에서 비교 연산자 6개를 `%nonassoc`로 선언했을 때 빌드 결과는 다음과 같았다.

```
          36 shift/reduce conflicts
          shift/reduce error at state 25 on terminal LESS between
            {[explicit nonassoc 9996] shift(42)} and
            {[explicit nonassoc 9996] reduce(Expr:Expr 'EQUALS' Expr)}
            - assuming the former because we prefer shift on equal precedences
          ...
    0 Warning(s)
    0 Error(s)
Build succeeded.
```

- 충돌이 36개 있는데도 MSBuild 기준으로는 **경고 0개, 오류 0개**다. 출력을 직접 읽지 않으면 아무도 모른다.
- 36개가 나온 이유: 같은 우선순위에 연산자가 6개(`= <> < > <= >=`)이므로, "왼쪽 연산자 × 오른쪽 연산자" 조합 6 × 6 = 36가지가 모두 충돌한다.
- 그대로 두면 `1 < 2 < 3`이 오류 없이 `1 < (2 < 3)`으로 파싱된다. 설계 의도(parse error)와도, 사람들이 기대하는 left-associative과도 다르다.

## Recognition Pattern

다음 중 하나라도 보이면 이 문제다.

- `.fsy`에 이항 연산자를 `%nonassoc`로 선언했다. 주로 비교 연산자(`< > = <>`)나 범위 연산자(`..`)가 해당한다.
- fsyacc 출력에 `explicit nonassoc` 와 `prefer shift on equal precedences` 가 함께 나온다.
- 연쇄 비교 `a < b < c` 의 AST가 `Less (a, Less (b, c))`, 즉 오른쪽으로 묶여 나온다.

## The Approach

먼저 **"연쇄 비교를 문법 단계에서 막아야 하나, 다른 단계에서 막아도 되나?"** 를 정한다.

| 선택 | 방법 | `1 < 2 < 3` 결과 | 어울리는 경우 |
|---|---|---|---|
| A. 허용하고 나중에 거르기 | `%left`로 선언 | `(1 < 2) < 3`으로 파싱되고, 타입 검사에서 오류 | 타입 검사가 있는 언어. F#도 이 방식 |
| B. 문법에서 막기 | 비교식을 별도 비단말로 분리 | `parse error` | 타입 검사가 없거나, 파서에서 바로 알려 주고 싶을 때 |
| C. `%nonassoc` 그대로 두기 | — | `1 < (2 < 3)` | **쓰지 말 것** |

이 프로젝트는 A를 골랐다. 값이 `int`와 `bool` 두 종류이므로 `(1 < 2) < 3`은 `bool < int`가 되어 평가할 때 `Type error: expected int but got bool`로 걸린다. 문법도 단순하게 유지된다.

### Step 1: 충돌이 있는지 확인한다

문법을 고칠 때마다 빌드 출력에서 충돌을 찾는다. 빌드가 성공했다는 것만으로는 충돌이 없다고 말할 수 없다.

```bash
dotnet build src/Calc | grep -i conflict
# 아무것도 안 나와야 정상
```

어떤 상태에서 충돌이 나는지 자세히 보려면 `.fsproj`의 FsYacc `OtherFlags`에 `-v`를 추가한다. 그러면 `Parser.fsyacc.output`에 상태별 항목(item)과 액션이 기록된다. 아래 Example의 `%nonassoc` 문법으로 만든 출력은 다음과 같다.

```
state 5:
  items:
    E -> E . 'LT' E
    E -> E 'LT' E .            ← 여기서 reduce 할 수도 있고
    E -> E . 'PLUS' E
  actions:
    action 'LT' (explicit nonassoc 9998):   shift 7     ← error 가 아니라 shift!
    action 'PLUS' (explicit left 9999):     shift 8
    action 'EOF' (explicit nonassoc 9998):  reduce E --> E 'LT' E
```

`E < E`까지 읽은 상태에서 다음 토큰이 또 `<`일 때 bison이라면 이 칸이 `error`가 된다. fsyacc는 이 칸을 `shift`로 채운다. 그래서 `1 < 2 < 3`이 `1 < (2 < 3)`이 된다.

### Step 2-A: `%left`로 바꾸고 이후 단계에서 거른다

```diff
 %nonassoc IN ELSE
 %left OR
 %left AND
-%nonassoc EQUALS NOTEQUAL LESS GREATER LESSEQUAL GREATEREQUAL
+%left EQUALS NOTEQUAL LESS GREATER LESSEQUAL GREATEREQUAL
 %left PLUS MINUS
```

다음에 이 코드를 읽을 사람이 다시 `%nonassoc`로 "고치지" 않도록 `.fsy`에 이유를 주석으로 남긴다.

```
// 비교 연산자는 왼쪽 결합: 1 < 2 < 3 == (1 < 2) < 3 → 평가 시 타입 오류.
// (fsyacc 는 %nonassoc 연산자가 연달아 나와도 오류로 만들지 않고
//  shift/reduce 충돌을 보고하므로 %left 사용)
```

### Step 2-B: 문법에서 막아야 한다면 계층을 나눈다

비교식의 양쪽 피연산자를 "비교가 아닌 식"으로 제한한다. 그러면 `a < b < c`를 만들 수 있는 규칙 자체가 사라진다. 우선순위 선언 없이 문법 구조로 막는 방식이다.

```
Expr:
    | Arith LT Arith      { Less($1, $3) }   // 비교는 한 번만
    | Arith               { $1 }

Arith:
    | NUM                 { Number $1 }
    | Arith PLUS Arith    { Add($1, $3) }
    | LPAREN Expr RPAREN  { $2 }             // 괄호 안에서는 다시 비교 가능
```

- 괄호를 쓰면 여전히 `(1 < 2) < 3`처럼 쓸 수 있다. 필요하면 타입 검사로 거른다.
- 단점: `&&`, `||`, `if`, `let` 등이 추가되면 계층이 늘어난다(`OrExpr → AndExpr → CmpExpr → Arith → ...`). 비단말 수가 늘어나는 만큼 문법이 길어진다.

### Step 3: AST로 결과를 확인한다

충돌이 0개라는 것만으로는 부족하다. **의도한 모양으로 묶이는지** 테스트로 고정한다.

```fsharp
test "비교는 왼쪽 결합" {
    Expect.equal
        (parse "1 < 2 < 3")
        (Less (Less (Number 1, Number 2), Number 3))
        ""
}
```

## Example

같은 입력 `1 < 2 < 3`을 세 가지 문법으로 직접 파싱해 본 결과다(FsLexYacc 11.3.0).

```
%token <int> NUM
%token LT PLUS EOF
```

| 문법 | fsyacc 충돌 | `1 < 2 < 3` 파싱 결과 |
|---|---|---|
| `%nonassoc LT` + `E: E LT E` | **1개** (`explicit nonassoc ... prefer shift`) | `(1<(2<3))` ← right-associative |
| `%left LT` + `E: E LT E` | 0개 | `((1<2)<3)` |
| 계층 분리 `E: A LT A \| A` | 0개 | `parse error` |

계층을 분리한 문법에서도 `1 + 2 < 3`은 `((1+2)<3)`으로 정상 파싱된다.

## 체크리스트

- [ ] 이항 연산자에 `%nonassoc`를 쓰지 않았다 (단항 연산자나 `in`, `else` 같은 키워드는 괜찮다)
- [ ] `dotnet build | grep -i conflict` 결과가 비어 있다
- [ ] 연쇄 연산(`a < b < c`)의 AST 모양을 테스트로 고정했다
- [ ] `%left`를 고른 이유를 `.fsy` 주석으로 남겼다
- [ ] (`%left`를 골랐다면) 이후 단계에서 연쇄 비교가 걸러지는지 확인했다. 예: 타입 오류

## 관련 문서

- 이 프로젝트의 문법 전체: `docs/grammar.md`의 "우선순위와 associativity" 절
