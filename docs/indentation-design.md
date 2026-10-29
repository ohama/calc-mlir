# indentation 문법 설계 (F# light syntax)

F#처럼 들여쓰기로 코드 블록을 구분하는 문법을 넣기 위한 조사와 설계입니다. 0.38.0에서 `src/Calc/LayoutFilter.fs`로 구현했습니다. 쓰는 법은 [grammar.md](grammar.md)의 "indentation 문법"에 있습니다.

**설계와 달라진 점**

- **규칙 6의 오류는 넣지 않았습니다.** 블록의 열이 둘러싼 context보다 왼쪽이면 오류로 하려 했지만, 그러면 `map (fun x ->` 다음 줄에 본문을 쓰는 흔한 모양을 막게 됩니다. 블록의 열은 첫 토큰의 열 그대로 씁니다.
- **괄호 안 offside 오류도 넣지 않았습니다.** 괄호 안에서 왼쪽에 쓴 줄은 이어지는 줄입니다.
- **then, else의 몸:** 문법에서 then, else의 몸은 `;`를 받지 않는 식입니다. 그래서 같은 줄의 실제 `;`가 then/else 블록을 닫습니다(`if c then a else b; d`).
- **,와 :도 닫습니다.** 둘 다 괄호 안의 블록을 닫습니다(`(fun x -> x, 1)`, `(fun x -> x : int -> int)`).
- **match의 |:** `match`가 있는 줄의 머리까지 왼쪽에 둘 수 있습니다. 그 줄이 `|`로 시작하면(안쪽 `match`) 이 예외가 없습니다.
- **입력 끝:** 렉서가 입력 끝에 닿으면 `lexbuf.IsPastEndOfStream`이 켜지고, 파서는 그때부터 토큰 함수를 부르지 않습니다. 그래서 입력 끝 앞에 넣은 가상 토큰을 돌려줄 때 이 값을 끕니다.

## 1. 목표

지금 문법(F#의 verbose syntax, OCaml)으로 쓴 프로그램:

```
let rec fact n =
    if n = 0 then 1
    else n * fact (n - 1)
;;
let area shape =
    let pi = 3 in
    match shape with
    | Circle r -> pi * r * r
    | Rect (w, h) -> w * h
;;
println (fact 5);
println (area (Rect (2, 3)))
```

목표 문법(F#과 같음):

```
let rec fact n =
    if n = 0 then 1
    else n * fact (n - 1)

let area shape =
    let pi = 3
    match shape with
    | Circle r -> pi * r * r
    | Rect (w, h) -> w * h

println (fact 5)
println (area (Rect (2, 3)))
```

달라지는 점은 다섯 가지입니다.

| verbose | light | 규칙 |
|---|---|---|
| `let x = e in body` | `let x = e` 다음 줄에 같은 열로 `body` | 같은 열의 다음 줄이 `in`을 대신함 |
| `e1; e2` | `e1` 다음 줄에 같은 열로 `e2` | 같은 열의 다음 줄이 `;`를 대신함 |
| 최상위 `;;` | 0열에서 시작하는 다음 항목 | 0열의 새 줄이 `;;`를 대신함 |
| 중첩 `match`의 괄호 | 바깥 `match`의 열에 쓴 `\|` | 들여쓰기가 분기의 주인을 정함 |
| `module M = struct … end` | `module M =` 다음 줄부터 들여쓴 선언 | 들여쓰기가 모듈의 끝을 정함 |

지금 문법은 그대로 받습니다(F#도 verbose syntax를 받음). 한 줄에 쓴 프로그램의 뜻은 바뀌지 않아야 합니다.

## 2. 조사

| 언어 | 방법 | 블록을 정하는 것 | 이 언어에 맞는 점과 맞지 않는 점 |
|---|---|---|---|
| **F#** (light syntax) | 렉서와 파서 사이의 token filter(`LexFilter.fs`)가 **offside rule**로 가상 토큰을 넣음. 파서 문법은 verbose syntax 그대로 | context 스택. `let`, `if`, `match`, `(`, `=`, `->`, `then` 같은 토큰이 context를 쌓고, 줄의 첫 토큰이 context의 열보다 왼쪽이면(offside) 닫음 | 이 언어가 F#의 문법을 따르고 fsyacc를 쓰므로 가장 잘 맞음. 다만 실제 구현은 14만 자가 넘는 파일이고 예외 규칙이 많음 |
| **Haskell** (layout, Haskell 2010 보고서 10.3절) | `where`, `let`, `do`, `of` 뒤에서 가상 `{` `;` `}`를 넣는 알고리즘 L | 블록의 첫 토큰의 열. 같은 열이면 `;`, 왼쪽이면 `}` | `parse-error(t)` 규칙(파서가 실패하면 `}`를 넣음)이 있어 yacc 계열 파서와 맞물리기 어려움. 같은 열 → `;`, 왼쪽 → 닫기라는 핵심 규칙은 그대로 씀 |
| **Python** | 렉서가 줄 머리의 들여쓰기 스택으로 `INDENT`, `DEDENT`를 만들고 문법이 블록을 받음. 괄호 안에서는 줄바꿈을 무시 | 줄 머리의 공백 수 | 문장 언어라 블록이 줄 단위. 식 안의 블록(`if … then` 값, `fun` 본문)과 한 줄 코드를 다루지 못함. "괄호 안에서는 들여쓰기 무시"는 참고 |
| **Scala 3** (optional braces) | `=`, `then`, `do`, `:` 뒤에 줄바꿈과 들여쓰기가 오면 indentation region을 열고 `<indent>`/`<outdent>` 토큰을 넣음. `end` 표시는 선택 | 다음 줄의 들여쓰기 | 블록을 여는 토큰을 정해 두고 그 뒤에만 region을 여는 방법이 단순하고 예측하기 쉬움. 한 줄 코드는 그대로 |
| **Go**, JavaScript ASI | 줄 끝에서 조건이 맞으면 `;`를 넣음. Go는 "줄의 마지막 토큰이 이름, 리터럴, 닫는 괄호 등일 때" | 들여쓰기가 아니라 줄바꿈 | 들여쓰기 언어는 아니지만 "앞 토큰이 식을 끝낼 수 있고 다음 토큰이 식을 시작할 수 있을 때만 구분자를 넣음"이라는 조건이 쓸모 있음 |
| **OCaml** | 없음 (`in`, `;;`, `begin … end`, 괄호) | — | 지금의 이 언어 |

F#의 `LexFilter.fs`에서 확인한 것:

- **context 종류:** `CtxtLetDecl`, `CtxtIf`, `CtxtThen`, `CtxtElse`, `CtxtMatch`, `CtxtMatchClauses`, `CtxtFun`, `CtxtFunction`, `CtxtTry`, `CtxtWhile`, `CtxtFor`, `CtxtDo`, `CtxtParen`, `CtxtSeqBlock`, `CtxtModuleBody` 등이 있습니다.
- **가상 토큰:**
  - `OBLOCKBEGIN`/`OBLOCKEND`: 블록의 괄호입니다.
  - `OBLOCKSEP`: 블록 안의 구분자입니다. 같은 열의 새 줄에서 넣습니다.
  - `ODECLEND`: light `let`의 끝으로, `in` 자리에 넣습니다.
  - `OEND`: `fun`, `match` 분기를 닫습니다.
  - `ORIGHT_BLOCK_END`: 블록의 한쪽 끝입니다.
- **`in` 처리:** `CtxtLetDecl` 위에 `in`이 오면 context를 닫습니다. 블록 안의 `let`이면 `in`을 `ODECLEND`로 바꿉니다.
- **줄 머리의 infix 연산자:** `+`, `|>`, `::`, `&&` 같은 infix 연산자로 시작하는 줄은 앞 줄을 잇습니다. 블록 열보다 "연산자 길이 + 1"만큼 왼쪽에 있어도 블록을 닫지 않습니다(permitted undentation). `=`, `<`, `>`는 이 예외에서 뺍니다.
- **탭:** F#은 들여쓰기에 탭을 쓰면 오류입니다.

## 3. 방법 선택: F#처럼 token filter

| 방법 | 장점 | 단점 |
|---|---|---|
| **A. token filter (F#)** | 문법(`Parser.fsy`)을 거의 바꾸지 않음. verbose syntax가 그대로 동작. 가상 토큰은 문법의 기존 토큰(`IN`, `SEMI`, `SEMISEMI`, `DONE`, `STRUCT`, `END`)과 블록 괄호 두 개 | filter 안의 규칙을 정확히 정해야 함 |
| B. 문법에 `INDENT`/`DEDENT` (Python) | 렉서가 단순 | 블록을 받는 문법을 모든 식 자리에 다시 써야 함. verbose와 light가 문법을 나눠 가짐. 식 안의 블록에 맞지 않음 |
| C. 파서를 손으로 다시 쓰기 | 들여쓰기 정보를 바로 씀 | fsyacc로 쓴 문법과 충돌 처리, 위치 기록을 모두 다시 만듦 |

**A를 고릅니다.** F#이 이 방법으로 verbose와 light를 함께 받고, 이 언어의 문법은 F#의 verbose syntax와 같은 모양입니다.

## 4. 설계

### 4.1 위치

```
문자열 → Lexer.tokenize → LayoutFilter (새로) → Parser.start → (Modules.resolve) → Ast.Expr
```

- **새 모듈:** `src/Calc/LayoutFilter.fs`를 둡니다. `LexBuffer<char> -> token` 함수를 받아서 같은 모양의 함수를 돌려줍니다. `Frontend.parse`와 `parseItems`가 `Lexer.tokenize` 대신 이 함수를 파서에 넘깁니다.
- **위치 정보:** filter는 토큰을 미리 읽어 둡니다(다음 토큰의 줄과 열을 봐야 하므로). 그래서 토큰마다 `(token, StartPos, EndPos)`를 저장합니다. 파서에 돌려줄 때 `lexbuf.StartPos`/`EndPos`를 그 값으로 되돌립니다. fsyacc는 토큰을 받은 뒤 `lexbuf`의 위치를 읽기 때문입니다.
  - 가상 토큰의 위치는 바로 다음 실제 토큰의 시작입니다(빈 범위). parse error 메시지가 실제 토큰을 가리킵니다.
- **보간 문자열:** `$"…"`의 구멍은 지금처럼 filter 없는 `Lexer.tokenize`로 파싱합니다(한 줄 식).
- **열 계산:** 줄 머리의 열은 줄의 첫 토큰의 `StartPos.Column`입니다. 주석은 토큰이 아니므로 열에 영향을 주지 않습니다. 앞 토큰보다 줄 번호가 크면 "새 줄의 첫 토큰"입니다.

### 4.2 context 스택

| context | 쌓는 토큰 | 열 | 닫는 것 |
|---|---|---|---|
| `Top` | 시작 | 0 | 입력 끝 |
| `Block(col, kind)` | 블록을 여는 토큰 다음의 첫 토큰 | 그 토큰의 열 | 열보다 왼쪽인 새 줄(offside), 짝이 맞는 닫는 토큰 |
| `Let(col)` | `let` | `let`의 열 | `in`, offside, 같은 열의 새 줄(가상 `in`) |
| `If(col)` | `if` | `if`의 열 | `else` 블록이 끝날 때 |
| `Match(col)`, `Try(col)`, `Function(col)` | `match`, `try`, `function` | 그 토큰의 열 | offside |
| `Paren(kind)` | `(`, `[`, `{` | 안의 첫 토큰의 열 | 짝이 맞는 `)`, `]`, `}` (offside로는 닫지 않음, Python과 같음) |
| `Loop(col)` | `while`, `for` | 그 토큰의 열 | `done`, offside (가상 `done`) |
| `Module(col)` | `module M =` + 줄바꿈 | 다음 줄의 열 | offside (가상 `end`) |

**블록을 여는 토큰**은 다음과 같고, 그 뒤의 식을 가상 괄호 `OBLOCKBEGIN … OBLOCKEND`로 감쌉니다.

- `let … =`의 `=`
- `fun …`과 `match`/`function`/`try` 분기의 `->`
- `then`, `else`
- `do`, `try`, `in`

`type t =` 뒤의 생성자 목록은 식이 아니므로 감싸지 않습니다.

### 4.3 가상 토큰

| 넣는 것 | 문법의 토큰 | 언제 |
|---|---|---|
| 블록의 괄호 | 새 토큰 `OBLOCKBEGIN`, `OBLOCKEND` | 블록을 열 때와 닫을 때. 문법에는 `Atom: OBLOCKBEGIN SeqExpr OBLOCKEND` 규칙 하나만 더함 (괄호처럼 AST에 남지 않음) |
| `in` | `IN` | 블록 안의 `let`(아직 `in`이 없는)과 같은 열에서 새 줄이 시작할 때 |
| `;` | `SEMI` | 블록 안에서 같은 열의 새 줄. 앞 항목이 `let`이 아닐 때 |
| `;;` | `SEMISEMI` | `Top`과 `Module`에서 같은 열(0열)의 새 줄 |
| `done` | `DONE` | `while … do`, `for … do`의 블록이 offside로 끝났는데 `done`이 없을 때 |
| `struct` / `end` | `STRUCT`, `END` | `module M =` 다음 토큰이 새 줄에 있을 때 / 그 모듈 블록이 offside로 끝날 때 |

### 4.4 규칙

토큰 `t`를 받을 때마다 다음 순서로 처리합니다.

1. **offside (새 줄에서만):** `t`가 새 줄의 첫 토큰이고 열이 맨 위 context의 열보다 왼쪽입니다.
   - 열이 `t`의 열 이하인 context가 나올 때까지 context를 꺼냅니다.
   - 꺼낼 때마다 닫는 가상 토큰을 냅니다. `Block`이면 `OBLOCKEND`, `Loop`이면 `DONE`, `Module`이면 `END`입니다.
   - `Paren`은 꺼내지 않습니다. 괄호 안이 offside이면 오류입니다(R7).
2. **닫는 토큰:** `t`가 `in`, `)`, `]`, `}`, `else`, `with`, `done`, `end`, `and`, `;;`이면, 짝이 되는 context까지 꺼냅니다.
   - 짝: `in` → `Let`, `)` → `(`, `else` → `If`의 `then` 블록, `with` → `Match`/`Try`의 머리, `and` → 앞 `let rec`의 `Let`, `;;` → `Top`
   - `|`는 같은 줄이면 가장 안쪽 분기 블록 하나만 닫습니다. 그래서 한 줄의 중첩 `match`는 지금처럼 뒤 분기가 안쪽 `match`의 것입니다.
3. **구분자 (새 줄에서만):** `t`의 열이 맨 위 `Block`, `Top`, `Module`, `Paren`의 열과 같고, 두 조건이 모두 맞으면 구분자를 넣습니다.
   - 조건 1: 앞 토큰이 **식을 끝낼 수 있는** 토큰입니다. 이름, 리터럴, `)`, `]`, `}`, `done`, `end`, `_`.
   - 조건 2: `t`가 **식이나 선언을 시작할 수 있는** 토큰입니다. 이름, 리터럴, `(`, `[`, `{`, `let`, `if`, `match`, `fun`, `function`, `try`, `while`, `for`, `not`, `!`, `type`, `exception`, `module`, `open`.
   - 넣는 것은 `Let`이 열려 있으면 `IN`, 블록이면 `SEMI`, `Top`/`Module`이면 `SEMISEMI`, 리스트나 레코드의 `Paren`이면 `SEMI`(원소 구분자)입니다.
   - Go의 세미콜론 규칙과 같은 생각입니다. `let x =`나 `a +`처럼 줄 끝의 토큰이 뒤를 기다리면 넣지 않습니다. 그래서 verbose syntax의 여러 줄 코드(`… in` 다음 줄)도 그대로입니다.
4. **이어지는 줄 (구분자를 넣지 않음):** 다음 토큰으로 시작하는 줄은 앞 줄을 잇습니다.
   - infix 연산자: `+ - * / % = <> < > <= >= && || |> >> :: :=`
   - `|`, `then`, `else`, `with`, `->`, `in`, `and`, `do`, `to`, `downto`, `of`, `,`, `;`, 닫는 괄호
   - F#처럼 블록 열보다 "연산자 길이 + 1"만큼 왼쪽에 있는 연산자 줄도 offside로 보지 않습니다.
5. **더 깊은 줄:** 열이 블록 열보다 오른쪽인 새 줄은 앞 줄을 잇습니다(`f a` 다음 줄의 `b`는 인자).
6. **블록 열기:** 블록을 여는 토큰 다음의 첫 토큰이 블록의 열을 정합니다. 그 열이 둘러싼 context의 열 이하이면 오류입니다(`let x =` 다음 줄의 `1`이 `let`보다 왼쪽).
7. **오류:**
   - 탭으로 들여쓴 줄: `Tabs are not allowed in indentation`
   - 블록의 마지막 줄이 `let`이라 몸이 없음: `The block following this let is unfinished`
   - 괄호 안이 offside: `Possible incorrect indentation: this token is offside of the context started at line L, column C`

### 4.5 예

`·`는 가상 토큰입니다.

```
let f x =                     let f x = ·begin
    let y = x + 1                 let y = ·begin x + 1 ·end ·in
    y * 2                         y * 2 ·end
                              ·;;
println (f 1)                 println (f 1)
```

```
match a with                  match a with
| A ->                        | A -> ·begin
    match b with                  match b with
    | X -> 1                      | X -> ·begin 1 ·end
    | Y -> 2                      | Y -> ·begin 2 ·end ·end
| B -> 3                      | B -> ·begin 3 ·end
```

마지막 `| B`는 0열이라 안쪽 `match`(4열)의 offside입니다. 그래서 안쪽 분기 블록과 바깥 `A` 분기 블록을 닫고 바깥 `match`의 분기가 됩니다. 지금은 괄호가 필요한 경우입니다(`10-handle-nested-match-ambiguity-in-fsyacc.md`).

```
let xs =                      let xs = ·begin
    [ 1                           [ 1 ·;
      2                             2 ·;
      3 ]                           3 ] ·end
```

```
if c then                     if c then ·begin
    a                             a ·;
    b                             b ·end
else                          else ·begin
    d                             d ·end
```

```
let total =                   let total = ·begin
    xs                            xs
    |> map f                      |> map f
    |> sum                        |> sum ·end
```

마지막 예에서 `|>`는 infix 연산자라서 앞 줄을 잇습니다.

### 4.6 한 줄 코드와 기존 프로그램

- **한 줄 안에서는 구분자를 넣지 않습니다.** 넣는 가상 토큰은 블록 괄호뿐이고, 블록은 `in`, `)`, `else` 같은 닫는 토큰이나 입력 끝에서 닫힙니다. 그래서 한 줄 프로그램의 AST는 바뀌지 않아야 합니다.
- **확인 방법:** 테스트에 있는 모든 입력을 filter를 켜고 끄고 파싱해서 AST가 같은지 비교합니다. 대상은 `EvalTests.cases`, 파서 테스트, REPL 테스트, `examples/`, `prelude.calc`입니다. 이것을 첫 단계의 테스트로 둡니다.
- **바뀔 수 있는 것:** verbose로 쓴 여러 줄 코드 가운데, 같은 열의 새 줄이 "식을 끝낼 수 있는 토큰 뒤에 식을 시작하는 토큰"인 경우입니다. 예를 들어 함수 호출을 두 줄에 같은 열로 쓴 경우입니다.

  ```
  f
  x
  ```

  지금은 `f x`이고, light syntax에서는 `f; x`입니다(F#도 같음). 위의 비교 테스트가 이런 경우를 찾아 줍니다.

### 4.7 REPL

입력 하나(`;;`까지)를 filter에 통과시킵니다. 입력의 0열이 `Top`입니다. 입력 끝의 `;;`는 지금처럼 입력을 끝냅니다.

## 5. 정할 것

| 질문 | 추천 | 다른 선택 |
|---|---|---|
| 기본으로 켤까? | **켬.** F#처럼 verbose도 받으므로, 4.6의 비교 테스트가 통과하면 기존 프로그램이 그대로 동작 | 파일 첫 줄의 `#light`나 `--light` 옵션으로 켬 (안전하지만 두 문법을 계속 안내해야 함) |
| `module M =` + 들여쓰기 | **넣음** (F#과 같음). `struct … end`도 계속 받음 | `struct … end`만 |
| 리스트와 레코드의 줄바꿈 구분자 | **넣음** (F#과 같음, `[ 1⏎ 2 ]`) | `;`를 계속 써야 함 |
| 들여쓰기가 틀렸을 때 | **오류** (위치를 알려 줌) | F#처럼 경고하고 계속 |
| `while`, `for`의 `done` | **생략 가능** (offside로 닫음, F#과 같음) | 계속 필요 |
| prelude와 examples를 light로 다시 쓸까? | 첫 릴리스에서는 그대로 두고(verbose 호환 확인용), 다음 릴리스에서 다시 씀 (0.39.0에서 다시 씀) | 바로 다시 씀 |

## 6. 단계

| 단계 | 내용 | 확인 |
|---|---|---|
| 1 | `LayoutFilter`의 틀: 토큰을 미리 읽고 위치를 되돌림. 아직 아무것도 넣지 않음 | 모든 테스트 통과. 오류 위치가 그대로 |
| 2 | context 스택, 블록 괄호, 닫는 토큰 (규칙 1, 2, 6) | 4.6의 AST 비교 테스트 (모든 기존 입력) |
| 3 | 같은 열의 구분자: `IN`, `SEMI`, `SEMISEMI` (규칙 3, 4, 5) | 1장의 목표 프로그램, 4.5의 예, 이어지는 줄 |
| 4 | 분기: `match`, `function`, `try`의 `\|`와 중첩 | 4.5의 중첩 `match`, 쓰이지 않는 분기 경고의 위치 |
| 5 | 괄호 안(리스트, 레코드, 튜플), `module M =`, `done` 생략 | 각 예제의 AST |
| 6 | 오류(탭, 끝나지 않은 `let`, 괄호 안 offside), REPL, 문서 | 오류 메시지와 위치 테스트 |

각 단계마다 인터프리터와 컴파일러의 differential testing은 그대로 돌아갑니다. filter는 파서 앞에서만 동작하기 때문입니다.

## 7. 범위 밖

- F#의 모든 permitted undentation (예: `fun` 본문을 `fun`보다 왼쪽에 쓰는 경우, 함수 인자를 여러 줄에 맞춰 쓰는 특수 규칙)
- `begin … end`, `elif`
- `#indent "off"`
- 들여쓰기 경고(F#의 FS0058)를 경고로만 두고 계속하는 것

## 8. 위험

- **규칙의 상호작용:** offside, 닫는 토큰, 구분자의 순서가 틀리면 드문 입력에서 엉뚱한 가상 토큰이 들어갑니다. 그래서 단계마다 AST 비교 테스트와, filter가 낸 토큰 열을 보여 주는 테스트(`·begin` 같은 표기)를 둡니다.
- **오류 메시지:** 가상 토큰 때문에 parse error가 `unexpected 'in'`처럼 사용자가 쓰지 않은 토큰을 말할 수 있습니다. 가상 토큰이 원인이면 메시지를 바꿔야 합니다(예: `unexpected end of block`).
- **위치:** 미리 읽기 때문에 `lexbuf`의 위치를 되돌리지 않으면 모든 오류 위치가 한 토큰씩 밀립니다. 단계 1의 테스트가 이것을 확인합니다.

## 참고

- F# 컴파일러 `src/Compiler/SyntaxTree/LexFilter.fs` (dotnet/fsharp)
- F# 언어 명세 15.1절 "Lightweight Syntax"
- Haskell 2010 Language Report 10.3절 "Layout"
- Python Language Reference 2.1.8 "Indentation"
- Scala 3 Reference "Optional Braces"
- The Go Programming Language Specification "Semicolons"
