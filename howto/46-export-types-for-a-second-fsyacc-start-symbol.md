---
order: 46
created: 2026-10-28
commit: 32d4e7c
version: 0.26.0
description: fsyacc 헤더(%{ %})에 정의한 타입은 생성되는 Parser.fsi에 들어가지 않는다. 시작 기호가 그 타입을 돌려주면 FS0039와 FS0034로 빌드가 깨진다. 시작 기호의 결과 타입은 파서보다 먼저 컴파일되는 모듈(Ast)에 둔다
---

# fsyacc 시작 기호를 하나 더 둘 때 결과 타입은 파서 밖에 두기

> 관련 커밋: [32d4e7c](https://github.com/ohama/calc-mlir/commit/32d4e7c) (v0.26.0)

파서 하나로 두 가지 결과를 얻고 싶을 때가 있다. 이 프로젝트에서는 프로그램 파일용(식 하나, `start`)과 REPL용(최상위 항목 목록, `startItems`)이 필요했다. fsyacc는 `%start`에 시작 기호를 여러 개 적으면 시작 기호마다 함수를 만든다.

```
%start start startItems
%type <Ast.Expr> start
%type <Ast.TopItem list> startItems
```

처음에는 항목 타입 `TopItem`을 파서 헤더(`%{ … %}`)에 두었다. 그 헤더의 다른 도우미 함수가 이 타입을 쓰고 있었기 때문이다. 그런데 이 타입을 시작 기호의 결과로 쓰는 순간 빌드가 깨진다.

## The Insight

**fsyacc가 만드는 `Parser.fsi`(시그니처 파일)에는 토큰 타입과 시작 함수만 들어간다.** 헤더 코드는 `Parser.fs`에만 들어간다. 그래서 헤더에 정의한 타입과 함수는 모듈 밖에서 보이지 않는다. 그런데 시작 함수의 시그니처는 `.fsi`에 적혀야 하므로, 그 결과 타입을 `.fsi`가 알아야 한다.

헤더에 타입을 두고 재현해 보았다.

```
Parser.fsi(146,106): error FS0039: The type 'HeaderItem' is not defined.
Parser.fs(1911,5): error FS0034: Module 'Parser' contains
    val startItems<'a> : lexer: (LexBuffer<'a> -> token) -> lexbuf: LexBuffer<'a> -> HeaderItem list
  but its signature specifies
    val startItems<'cty,'a> : (LexBuffer<'cty> -> token) -> LexBuffer<'cty> -> 'a
```

첫 오류가 원인이다. 두 번째 오류는 그 결과로 시그니처의 타입을 정하지 못해서 생긴다. 메시지만 보면 제네릭 문제처럼 보이지만, 실제 원인은 타입이 `.fsi`에 없다는 것이다.

## Why This Matters

- **두 번째 오류만 보고 고치려 한 경우:** 타입 매개변수 수를 맞추려고 시간을 씁니다. 실제 원인은 첫 오류(FS0039)입니다.
- **시작 기호를 늘리지 않으려고 우회한 경우:** 예를 들어 `start`의 결과에서 항목을 거꾸로 복원하는 코드를 짜게 됩니다. 정보가 이미 사라진 뒤라 정확하지 않습니다.

## Recognition Pattern

- fsyacc 문법에 `%start`를 하나 더 두거나, 시작 기호의 결과 타입을 바꿀 때
- `Parser.fsi`에서 `FS0039: The type '...' is not defined`가 날 때
- 헤더(`%{ %}`)에 정의한 것을 파서 밖에서 쓰고 싶을 때

## The Approach

### Step 1: 결과 타입을 파서보다 먼저 컴파일되는 파일로 옮긴다

```fsharp
// Ast.fs (Parser.fsi 보다 먼저 컴파일됨)
type TopItem =
    | TopLet of string * Expr * FSharp.Text.Lexing.Position
    | TopLetRec of string * string * Expr * FSharp.Text.Lexing.Position
    | TopLetPattern of Pattern * Expr * FSharp.Text.Lexing.Position
    | TopType of string * string list * (string * TypeExpr option) list * FSharp.Text.Lexing.Position
    | TopExpr of Expr
```

헤더에서는 `open Ast`로 그대로 쓴다. 헤더의 도우미 함수(`makeProgram`)는 파서 안에서만 쓰므로 헤더에 남겨도 된다.

### Step 2: 시작 기호를 더하고 `%type`에 정식 이름을 쓴다

```
%start start startItems
%type <Ast.Expr> start
%type <Ast.TopItem list> startItems
%%
start:
    | Items EOF                     { makeProgram $1 }
startItems:
    | Items EOF                     { $1 }
```

두 시작 기호가 같은 규칙(`Items`)을 공유하므로 문법 충돌은 늘지 않는다(이 프로젝트에서 0개 그대로).

### Step 3: 오류 처리를 시작 기호마다 복사하지 않는다

파서 오류를 위치가 있는 예외로 바꾸는 코드는 시작 함수를 인자로 받게 하면 하나로 충분하다.

```fsharp
let private parseWith (start: (LexBuffer<char> -> Parser.token) -> LexBuffer<char> -> 'a) (input: string) : 'a =
    let lexbuf = LexBuffer<char>.FromString input
    try start Lexer.tokenize lexbuf
    with Errors.ParseFailure -> ... // 위치를 붙인 SyntaxError

let parse (input: string) : Expr = parseWith Parser.start input
let parseItems (input: string) : TopItem list = parseWith Parser.startItems input
```

### Step 4: 생성된 `.fsi`로 확인한다

빌드 뒤 `Parser.fsi`에 무엇이 들어갔는지 본다. 헤더의 함수가 없다는 것도 함께 확인할 수 있다.

```bash
grep -n "val start" src/Calc/Parser.fsi
# val start : (...) -> (Ast.Expr)
# val startItems : (...) -> (Ast.TopItem list)
grep -c "makeProgram" src/Calc/Parser.fsi     # 0: 헤더 함수는 시그니처에 없음
```

## Example

```fsharp
// ❌ 헤더에 둔 타입: Parser.fsi 가 모름 → FS0039, FS0034
%{
type HeaderItem = | HeaderLet of string * Expr
%}
%type <HeaderItem list> startItems

// ✅ 먼저 컴파일되는 Ast.fs 에 두고 정식 이름으로
%type <Ast.TopItem list> startItems
```

## 체크리스트

- [ ] 시작 기호의 결과 타입이 파서보다 먼저 컴파일되는 파일에 있는가?
- [ ] `%type`에 모듈 이름까지 쓴 정식 이름을 썼는가?
- [ ] 파서 오류 처리를 시작 기호마다 복사하지 않았는가?
- [ ] 빌드 출력의 `conflict`가 늘지 않았는가?

## 관련 문서

- `16-report-parse-error-positions-with-fsyacc.md` - 시작 함수를 감싸는 오류 처리
- `45-implement-repl-by-reinferring-declarations.md` - 두 번째 시작 기호를 쓰는 REPL
- `12-escape-quotes-and-braces-in-fslex-actions.md` - fslex/fsyacc가 액션 코드를 다루는 방식
