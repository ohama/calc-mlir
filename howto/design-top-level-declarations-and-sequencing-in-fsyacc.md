---
created: 2026-10-28
description: 식 하나짜리 언어에 in 없는 최상위 선언과 e1; e2를 더할 때, 선언 뒤의 식은 ;;로 구분해야 LALR 문법에 충돌이 없다. ;는 SeqExpr 규칙을 따로 두고 가장 낮은 %prec로 시퀀스를 끝까지 잇는다. 리스트의 ;는 SeqExpr를 쓰지 않아 구분자로 남는다. 선언은 파서에서 let ... in 사슬로 풀어 써서 뒤 단계는 바꾸지 않는다
---

# fsyacc에 최상위 선언과 `;` 순서 실행 넣기

식 하나를 계산하던 언어로 여러 줄짜리 프로그램을 쓰려면 두 가지가 필요하다.

```
let greet name = "Hello, " + name      // in 없는 최상위 선언
let rec fact n = if n = 0 then 1 else n * fact (n - 1)
;;
println (greet "Calc");                // e1; e2
fact 10
```

OCaml과 같은 규칙으로 넣으면 문법 충돌 없이 들어간다. 다른 규칙을 고르면 충돌이 수십 개 생긴다.

## The Insight

**식에는 끝을 알리는 토큰이 없다.** 함수 호출은 `f x y`처럼 원자(atom)를 이어 붙이는 것이다. 그래서 선언 `let x = f` 다음 줄에 `g`가 오면, 파서는 `g`가 `f g`의 인자인지 새 식의 시작인지 알 수 없다.

```
let x = f
g          ← f g ?  아니면  let x = f;; g ?
```

OCaml은 이 문제를 **`;;`** 로 푼다. 선언 바로 뒤에는 다른 선언(`let`, `type`)이나 `;;`만 올 수 있고, 식은 `;;` 다음에 온다. F#은 들여쓰기(offside rule)로 푸는데, 렉서가 들여쓰기를 추적해야 한다.

직접 재 보았다. 선언 바로 뒤에 식을 허용하는 규칙 한 줄을 더하면 fsyacc가 충돌 9개를 보고한다.

```
shift/reduce error at state 44 on terminal IDENT between {noprec shift(131)}
  and {noprec reduce(Expr:AppExpr)} - assuming the former because we prefer shift ...
        9 shift/reduce conflicts
```

`IDENT`, `LPAREN`, `LBRACKET`, `CTOR`처럼 원자를 시작하는 토큰마다 "호출을 이어 갈지, 선언을 끝낼지" 충돌이 하나씩 생긴다.

## Why This Matters

- **충돌을 무시한 경우:** fsyacc는 충돌이 있어도 빌드를 멈추지 않고 shift를 고릅니다. 그러면 `let x = f\ng`가 조용히 `f g`가 되고, 오류 메시지는 엉뚱한 곳(`Unbound variable`)을 가리킵니다.
- **`;`를 `Expr`의 이항 연산자로 넣은 경우:** 리스트 리터럴 `[a; b]`의 `;`와 겹칩니다. 직접 재 보니 shift/reduce 충돌 3개와 reduce/reduce 충돌 27개가 나왔습니다. reduce/reduce 충돌은 우선순위로도 풀 수 없습니다.
- **`;` 규칙에 우선순위를 주지 않은 경우:** 충돌이 14개 나옵니다. `let x = 1 in a; b`가 `let x = 1 in (a; b)`인지 `(let x = 1 in a); b`인지를 정해야 하기 때문입니다.

## Recognition Pattern

- 식 기반 언어에 "프로그램 = 선언들"을 더할 때
- 식 안에 순서 실행, 문장 구분자를 넣는데 같은 기호를 다른 곳(리스트, 레코드)에서도 쓸 때
- 빌드 출력에 `shift/reduce`가 늘어났을 때

## The Approach

### Step 1: 선언 뒤의 식은 `;;` 다음에만

```
Items:
    |                               { [] }
    | SEMISEMI Items                { $2 }
    | SeqExpr                       { [ TopExpr $1 ] }
    | SeqExpr SEMISEMI Items        { TopExpr $1 :: $3 }
    | TopDecl AfterDecl             { $1 :: $2 }

// 선언 바로 뒤: 식은 올 수 없음
AfterDecl:
    |                               { [] }
    | SEMISEMI Items                { $2 }
    | TopDecl AfterDecl             { $1 :: $2 }
```

`TopDecl`은 `LET IDENT EQUALS SeqExpr`처럼 `in`이 없다. 식 `let x = e in body`와 앞부분이 같지만, 다음 토큰이 `IN`이면 식, `LET`·`TYPE`·`SEMISEMI`·`EOF`이면 선언이다. 토큰 하나로 갈리므로 LALR(1)이 처리한다.

### Step 2: 선언은 파서에서 `let … in` 사슬로 풀어 쓴다

```fsharp
let rec makeProgram (items: TopItem list) : Expr =
    match items with
    | [] -> Unit                                         // 끝이 선언이면 ()
    | [ TopExpr e ] -> e                                 // 식 하나만: 예전과 같은 AST
    | TopExpr e :: rest -> Seq(e, makeProgram rest)
    | TopLet (name, value, pos) :: rest -> located pos (Let(name, value, makeProgram rest))
    ...
```

이러면 평가기, 타입 추론, 컴파일러는 새 구문을 몰라도 된다. 식 하나짜리 프로그램은 AST가 예전과 똑같아서 기존 파서 테스트도 그대로 통과한다. 풀어 쓴 `Let` 노드에는 선언의 시작 위치를 기록해서 오류 위치도 맞게 한다.

### Step 3: `;`는 별도 규칙과 가장 낮은 `%prec`

```
%nonassoc BELOWSEMI          // 가장 낮음
%nonassoc SEMI
...

SeqExpr:
    | Expr %prec BELOWSEMI          { $1 }
    | Expr SEMI SeqExpr             { Seq($1, $3) }
```

`SeqExpr`는 `;`가 이어질 수 있는 곳에서만 쓴다. `let`의 오른쪽과 본문, `fun`의 본문, `match` 분기, 괄호 안, 최상위가 그 자리다. `SeqExpr: Expr` 규칙이 모든 토큰보다 약하므로, 어떤 연산자나 `;`가 와도 "이어 간다(shift)"가 이긴다. 그래서 `let x = 1 in a; b`는 `let x = 1 in (a; b)`가 된다(OCaml과 같음).

### Step 4: 리스트와 `if`는 `Expr`를 그대로 쓴다

- **리스트:** `ListItems: Expr | Expr SEMI ListItems`는 `SeqExpr`를 쓰지 않으므로 `[a; b]`의 `;`는 구분자로 남습니다. 리스트 안에서 순서 실행이 필요하면 괄호로 감쌉니다(`[(a; b)]`).
- **`if`:** `if`의 분기는 `Expr`라서 `if c then a else b; d`는 `(if c then a else b); d`가 됩니다(OCaml과 같음).

### Step 5: 다른 설계와 충돌 수를 비교해 본다

설계를 고를 때는 문법 파일을 복사해 한 곳씩 바꾸고 fsyacc를 직접 돌려 충돌 수를 센다.

```bash
Y=~/.nuget/packages/fslexyacc/11.3.0/build/fsyacc/net6.0/fsyacc.dll
dotnet $Y --module Parser variant.fsy -o out.fs 2>&1 | grep -E "[0-9]+ (shift/reduce|reduce/reduce) conflicts"
```

| 설계 | 충돌 |
|---|---|
| 지금 문법 (`;;`, `SeqExpr` + `BELOWSEMI`) | 0 |
| 선언 바로 뒤에 식 허용 (`;;` 없이) | shift/reduce 9 |
| `SeqExpr: Expr`에 `%prec` 없음 | shift/reduce 14 |
| `;`를 `Expr`의 이항 연산자로 | shift/reduce 3, reduce/reduce 27 |
| 괄호를 `( SeqExpr )`로 (튜플 규칙과 함께) | 0 |

마지막 줄은 짐작과 달랐다. 괄호 안 `SeqExpr`가 튜플의 `,`와 부딪칠 거라 예상했는데, 재 보니 충돌이 없었다. 이 프로젝트는 `( Expr ; SeqExpr )`로 썼다. `(e)`가 예전처럼 `e` 그대로 남기 때문이다. 예상은 반드시 재서 확인한다.

## Example

```
$ calc examples/hello.calc
Hello, Calc!
3628800
areas: [12; 12]
24
48
```

```fsharp
parse "let x = 1\nlet y = 2\n;; x + y"
// Let ("x", Number 1, Let ("y", Number 2, Add (Var "x", Var "y")))
parse "let x = f\ng"
// Let ("x", App (Var "f", Var "g"), Unit)     ← ;; 가 없으면 이어짐 (OCaml 과 같음)
```

## 체크리스트

- [ ] 선언 뒤의 식에 구분자(`;;`)가 필요한가? 충돌 수로 확인했는가?
- [ ] 선언을 기존 AST로 풀어 써서 뒤 단계를 바꾸지 않았는가?
- [ ] `;`가 이어지는 자리에서만 `SeqExpr`를 쓰고, 리스트 구분자와 겹치지 않는가?
- [ ] `SeqExpr: Expr` 규칙에 가장 낮은 `%prec`를 주었는가?
- [ ] 빌드 출력에 `conflict`가 0개인가?
- [ ] 예전 단일 식 프로그램의 AST가 그대로인가?

## 관련 문서

- `control-let-if-scope-with-precedence.md` - 규칙의 마지막 terminal과 우선순위
- `handle-fsyacc-nonassoc-conflict.md` - fsyacc가 충돌을 보고하는 방식
- `handle-nested-match-ambiguity-in-fsyacc.md` - `%prec`로 shift를 고르는 다른 예
