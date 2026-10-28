# 문법 (Grammar)

Calc 언어의 어휘 구조, 구문, 의미를 정리한 문서입니다. 구현은 다음 파일에 있습니다.

| 단계 | 파일 |
|---|---|
| 어휘 (토큰) | `src/Calc/Lexer.fsl` |
| 구문 (파서) | `src/Calc/Parser.fsy` |
| AST | `src/Calc/Ast.fs` |
| 의미 (평가) | `src/Calc/Eval.fs` |

## 1. 어휘 구조

### 토큰

| 토큰 | 패턴 | 예 |
|---|---|---|
| `NUMBER` | `[0-9]+` | `0`, `42` |
| `STRING` | `"`로 시작해서 `"`로 끝남 (아래 "문자열 리터럴" 참고) | `"hello"`, `""`, `"a\"b"` |
| `IDENT` | `[A-Za-z_][A-Za-z0-9_]*` (키워드 제외) | `x`, `foo_1`, `_tmp` |
| 키워드 | `let` `rec` `in` `fun` `if` `then` `else` `true` `false` `not` `match` `with` | |
| `EQUALS` | `=` | |
| `ARROW` | `->` | |
| `NOTEQUAL` `LESS` `GREATER` `LESSEQUAL` `GREATEREQUAL` | `<>` `<` `>` `<=` `>=` | |
| `AND` `OR` | `&&` `\|\|` | |
| `PLUS` `MINUS` `STAR` `SLASH` | `+` `-` `*` `/` | |
| `LPAREN` `RPAREN` | `(` `)` | |
| `LBRACKET` `RBRACKET` `SEMI` | `[` `]` `;` | |
| `CONS` | `::` | |
| `BAR` | `\|` | |
| `EOF` | 입력의 끝 | |

### 규칙

- **공백**: 스페이스, 탭, 줄바꿈(`\n`, `\r\n`)은 토큰을 구분하는 역할만 하고 무시됩니다.
- **가장 긴 일치**: 렉서는 가장 길게 일치하는 패턴을 고릅니다. 길이가 같으면 `Lexer.fsl`에서 앞에 있는 규칙이 이깁니다.
  - 키워드 규칙이 `IDENT` 규칙보다 앞에 있으므로 `let`, `if`, `true` 등은 키워드입니다.
  - 키워드보다 길게 일치하는 `letx`, `iffy`, `trueish`, `notx`는 `IDENT`입니다.
  - `<=`는 `<`와 `=`로 나뉘지 않고 `LESSEQUAL` 하나가 됩니다. `<>`, `>=`, `->`, `::`, `||`도 같습니다.
  - `[]`는 토큰 하나가 아니라 `LBRACKET`과 `RBRACKET`입니다. 그래서 `[ ]`처럼 사이에 공백이 있어도 됩니다.
  - `2x`는 `NUMBER 2`와 `IDENT x`로 나뉩니다.
- **대소문자 구분**: 키워드는 소문자만 인정합니다. `Let`, `True`는 `IDENT`입니다.
- **숫자 리터럴**: 음이 아닌 정수만 리터럴입니다. `-5`는 `MINUS`와 `NUMBER 5`로 나뉘고, 파서가 단항 마이너스로 처리합니다.
  - 32비트 `int` 범위를 넘는 리터럴(예: `2147483648`)은 렉서 단계에서 오류가 납니다.
- **그 밖의 문자**: 위 패턴에 맞지 않는 문자는 `Unexpected character: <문자>` 오류를 냅니다. `&`나 `:` 하나만 쓴 경우도 여기에 해당합니다.
- **주석**: 아직 지원하지 않습니다.

### 문자열 리터럴

`"`와 `"` 사이의 문자를 그대로 값으로 씁니다. 문자열 안에서는 키워드, 연산자, 공백이 모두 그냥 문자입니다. `--unicode`로 렉서를 만들었으므로 한글과 이모지도 쓸 수 있습니다.

| 이스케이프 | 뜻 |
|---|---|
| `\"` | `"` |
| `\\` | `\` |
| `\n` | 줄바꿈 (LF) |
| `\t` | 탭 |
| `\r` | 캐리지 리턴 (CR) |

- 그 밖의 이스케이프(`\q` 등)는 `Invalid escape sequence: \q` 오류입니다.
- 닫는 `"`가 없으면 `Unterminated string` 오류입니다. `"abc\"`도 마지막 `"`가 이스케이프되므로 닫히지 않은 문자열입니다.
- 문자열 안에 실제 줄바꿈을 넣어도 됩니다. 그대로 값에 포함됩니다.

## 2. 구문

### EBNF

```ebnf
program = expr EOF ;

expr    = app
        | "let" IDENT "=" expr "in" expr
        | "let" IDENT params "=" expr "in" expr          (* 함수 정의 *)
        | "let" "rec" IDENT params "=" expr "in" expr    (* 재귀 함수 정의 *)
        | "fun" params "->" expr                         (* 람다 *)
        | "if" expr "then" expr "else" expr
        | "match" expr "with" [ "|" ] arms               (* 리스트 패턴 매칭 *)
        | expr binop expr
        | "-" expr
        | "not" expr ;

app     = app atom                                       (* 함수 호출 *)
        | atom ;

atom    = NUMBER
        | STRING
        | "true" | "false"
        | IDENT
        | "(" expr ")"
        | "[" [ expr { ";" expr } ] "]" ;                (* 리스트 리터럴 *)

arms    = nilArm "|" consArm
        | consArm "|" nilArm ;                           (* 순서 무관, 각각 정확히 하나 *)
nilArm  = "[" "]" "->" expr ;
consArm = IDENT "::" IDENT "->" expr ;

params  = IDENT { IDENT } ;

binop   = "+" | "-" | "*" | "/"
        | "=" | "<>" | "<" | ">" | "<=" | ">="
        | "::"
        | "&&" | "||" ;
```

`expr binop expr` 부분은 그대로 두면 모호합니다. 아래의 우선순위와 결합 방향으로 모호함을 해소합니다. 함수 호출은 우선순위 선언 대신 `app`/`atom` 계층으로 표현했습니다. `Parser.fsy`도 같은 방식으로 작성되어 있으며 shift/reduce 충돌이 없습니다.

### 우선순위와 결합 방향

위에 있을수록 우선순위가 **낮습니다**. `Parser.fsy`의 선언 순서와 같습니다.

| 우선순위 | 연산자 | 결합 방향 | 선언 |
|---|---|---|---|
| 1 (가장 낮음) | `let ... in`, `if ... then ... else`, `fun ... ->`, `match` 분기 `->` | — | `%nonassoc IN ELSE ARROW` |
| 2 | `\|\|` | 왼쪽 | `%left OR` |
| 3 | `&&` | 왼쪽 | `%left AND` |
| 4 | `=` `<>` `<` `>` `<=` `>=` | 왼쪽 | `%left EQUALS NOTEQUAL ...` |
| 5 | `::` | **오른쪽** | `%right CONS` |
| 6 | `+` `-` (이항) | 왼쪽 | `%left PLUS MINUS` |
| 7 | `*` `/` | 왼쪽 | `%left STAR SLASH` |
| 8 | `-` (단항), `not` | — | `%nonassoc UMINUS NOT` |
| 9 (가장 높음) | 함수 호출 `f x` | 왼쪽 | 문법 계층 (`app = app atom`) |

F#과 같은 순서입니다. 함수 호출은 어떤 연산자보다 먼저 묶입니다.

예:

| 입력 | 해석 |
|---|---|
| `2 + 3 * 4` | `2 + (3 * 4)` |
| `10 - 3 - 2` | `(10 - 3) - 2` |
| `-5 + 2` | `(-5) + 2` |
| `1 - -2` | `1 - (-2)` |
| `1 + 2 < 4` | `(1 + 2) < 4` |
| `a < b && c` | `(a < b) && c` |
| `a \|\| b && c` | `a \|\| (b && c)` |
| `not a = b` | `(not a) = b` |
| `1 < 2 < 3` | `(1 < 2) < 3` (파싱은 되지만 평가할 때 타입 오류) |
| `1 :: 2 :: []` | `1 :: (2 :: [])` |
| `1 + 2 :: xs` | `(1 + 2) :: xs` |
| `x :: xs = ys` | `(x :: xs) = ys` |
| `f x :: xs` | `(f x) :: xs` |
| `f x y` | `(f x) y` |
| `f x + g y` | `(f x) + (g y)` |
| `-f x` | `-(f x)` |
| `f (g x)` | `f`에 `g x`의 결과를 전달 |
| `f -1` | `f - 1` (뺄셈). 음수를 넘기려면 `f (-1)` |

> **F#과 다른 점:** F#은 `f -1`처럼 `-` 뒤에 공백이 없으면 함수 호출로 해석하는 특별 규칙이 있습니다. 이 언어는 공백을 구분하지 않으므로 `f -1`은 항상 뺄셈입니다.

### 함수 인자

함수 인자 자리에는 `atom`만 올 수 있습니다. 즉 숫자, `true`/`false`, 변수, 괄호식, 리스트 리터럴입니다. 나머지는 괄호로 감쌉니다.

| 입력 | 해석 |
|---|---|
| `f (x + 1)` | 인자는 `x + 1` |
| `f x + 1` | `(f x) + 1` |
| `f (fun x -> x)` | 람다를 인자로 전달. 괄호가 필요함 |
| `f (if c then 1 else 2)` | `if`를 인자로 전달. 괄호가 필요함 |
| `f [1; 2]` | 리스트 리터럴은 괄호 없이 인자가 됨 |
| `f (x :: xs)` | `::` 식은 괄호가 필요함. `f x :: xs`는 `(f x) :: xs` |

> 비교 연산자를 `%nonassoc`로 선언하지 않은 이유: fsyacc는 `%nonassoc` 연산자가 연달아 나와도 parse error를 만들지 않습니다. 대신 shift/reduce 충돌을 보고하고 shift를 고릅니다. 그래서 `%left`로 선언하고, `1 < 2 < 3` 같은 식은 평가 단계의 타입 검사에서 걸러냅니다.

### `let`, `if`, `fun`의 범위

`IN`, `ELSE`, `ARROW`의 우선순위가 가장 낮기 때문에 `let` 본문, `else` 분기, `fun` 본문은 **오른쪽으로 갈 수 있는 데까지** 이어집니다.

| 입력 | 해석 |
|---|---|
| `let x = 1 in x + 2` | `let x = 1 in (x + 2)` |
| `if c then 1 else 2 + 3` | `if c then 1 else (2 + 3)` |
| `fun x -> x + 1` | `fun x -> (x + 1)` |
| `(fun x -> x) 1` | 람다를 바로 호출하려면 괄호로 감쌈 |
| `1 + let x = 2 in x * 3` | `1 + (let x = 2 in (x * 3))` |
| `-let x = 1 in x + 2` | `-(let x = 1 in (x + 2))` |
| `(let x = 1 in x) + 10` | 괄호로 본문 범위를 제한 |
| `(if c then 1 else 2) + 10` | 괄호로 `else` 분기 범위를 제한 |

- `then` 분기는 `then`과 `else` 사이에 있으므로 범위가 분명합니다. 그 안에 다른 `if`를 바로 써도 됩니다.
  - `if a then if b then 1 else 2 else 3`은 `if a then (if b then 1 else 2) else 3`입니다.
- `if`는 식이므로 **`else`가 반드시 있어야 합니다**. `if c then 1`은 parse error입니다.
- `let`의 `=`와 비교 연산자 `=`는 같은 토큰이지만 위치로 구분됩니다.
  - `let x = 1 = 1 in x`에서 첫 번째 `=`는 바인딩이고, 두 번째 `=`는 비교입니다. 결과는 `true`입니다.

### 리스트와 `match`

- 리스트 리터럴 `[a; b; c]`는 `a :: b :: c :: []`의 문법 설탕입니다. 원소 구분자는 `;`이고, 끝에 `;`를 붙이면(`[1;]`) parse error입니다.
- `match`는 리스트만 다룹니다. 분기는 **`[]` 분기 하나와 `h :: t` 분기 하나**가 꼭 있어야 하고, 순서는 상관없습니다. 첫 분기 앞의 `|`는 생략할 수 있습니다.
  - 분기가 하나뿐이거나(`match xs with [] -> 0`), 같은 종류가 둘이면 parse error입니다.
  - `h`, `t` 자리에는 변수 이름만 올 수 있습니다. 중첩 패턴(`h :: []`, `1 :: t`)은 지원하지 않습니다. `_`는 보통 변수 이름으로 취급됩니다.
- 마지막 분기의 본문은 `->`의 우선순위가 가장 낮으므로 오른쪽 끝까지 이어집니다.
  - `match xs with [] -> 0 | h :: t -> h + 1`의 마지막 본문은 `h + 1`입니다.
- 분기 개수가 고정되어 있기 때문에 `match` 안에 `match`를 괄호 없이 넣어도 모호하지 않습니다. 안쪽 `match`가 분기 두 개를 가져가고, 그다음 `|`부터는 바깥 `match`의 분기입니다.
  - `match a with [] -> match b with [] -> 1 | x :: y -> 2 | h :: t -> 3`에서 `| h :: t -> 3`은 바깥 `match`의 분기입니다.
  - F#은 분기 수가 정해져 있지 않아서 안쪽 `match`가 뒤의 분기를 모두 가져갑니다. 읽기 쉽게 하려면 안쪽 `match`를 괄호로 감싸는 것이 좋습니다.

### 함수 정의의 문법 설탕

`let`으로 함수를 정의하는 문법과 여러 인자 `fun`은 모두 **인자 하나짜리 람다를 중첩한 것**으로 바뀝니다(커링).

| 입력 | 같은 뜻 |
|---|---|
| `fun x y -> e` | `fun x -> fun y -> e` |
| `let f x y = e1 in e2` | `let f = fun x -> fun y -> e1 in e2` |
| `let rec f x y = e1 in e2` | `LetRec ("f", "x", fun y -> e1, e2)` |

- `let rec`에는 인자가 하나 이상 있어야 합니다. `let rec f = 1 in f`는 parse error입니다.
- `fun`에도 인자가 하나 이상 있어야 합니다. `fun -> 1`은 parse error입니다.

## 3. AST 대응

| 구문 | AST (`Ast.Expr`) |
|---|---|
| `n` | `Number n` |
| `"s"` | `Str "s"` (이스케이프는 렉서에서 처리된 값) |
| `true` / `false` | `Bool true` / `Bool false` |
| `x` | `Var "x"` |
| `let x = e1 in e2` | `Let ("x", e1, e2)` |
| `let f x = e1 in e2` | `Let ("f", Lambda ("x", e1), e2)` |
| `let rec f x = e1 in e2` | `LetRec ("f", "x", e1, e2)` |
| `fun x -> e` | `Lambda ("x", e)` |
| `f x` | `App (f, x)` |
| `if c then e1 else e2` | `If (c, e1, e2)` |
| `e1 + e2` / `-` / `*` / `/` | `Add` / `Subtract` / `Multiply` / `Divide` |
| `-e` | `Negate e` |
| `e1 = e2` / `<>` | `Equal` / `NotEqual` |
| `e1 < e2` / `>` / `<=` / `>=` | `Less` / `Greater` / `LessEqual` / `GreaterEqual` |
| `e1 && e2` / `\|\|` | `And` / `Or` |
| `not e` | `Not e` |
| `[]` | `Nil` |
| `e1 :: e2` | `Cons (e1, e2)` |
| `[e1; e2]` | `Cons (e1, Cons (e2, Nil))` (리터럴은 AST에 남지 않음) |
| `match e with [] -> e1 \| h :: t -> e2` | `Match (e, e1, "h", "t", e2)` (분기 순서와 상관없이 같은 AST) |
| `(e)` | `e` (괄호는 AST에 남지 않음) |

## 4. 의미 (평가 규칙)

### 값

```fsharp
type Value =
    | VInt of int                                         // 32비트 정수
    | VBool of bool                                       // true / false
    | VString of string                                   // "..."
    | VClosure of param * body * env                      // fun param -> body + 정의 시점의 환경
    | VRecClosure of name * param * body * env            // let rec name param = body
    | VList of Value list                                 // [v1; v2; ...]
```

평가는 환경 `env : Map<string, Value>`에서 이루어집니다. 처음 환경은 비어 있습니다. 결과는 `formatValue`로 출력합니다.
- 정수는 `3`, `-3`처럼 출력됩니다.
- 불리언은 `true`, `false`로 출력됩니다.
- 문자열은 따옴표와 이스케이프를 붙인 리터럴 형태로 출력됩니다. 예: `"say \"hi\"\n"`. 출력을 그대로 다시 입력하면 같은 값이 됩니다.
- 함수는 `<fun>`으로 출력됩니다.
- 리스트는 `[1; 2; 3]`, `[]`, `[[true]; []]`처럼 출력됩니다.

타입 오류 메시지에서 문자열의 타입 이름은 `string`, 함수는 `function`, 리스트는 `list`입니다.

### 규칙

| 식 | 피연산자 타입 | 결과 |
|---|---|---|
| `Number n` | | `VInt n` |
| `Bool b` | | `VBool b` |
| `Str s` | | `VString s` |
| `Var x` | | `env`에서 `x`의 값. 없으면 `Unbound variable: x` 오류 |
| `Let (x, e1, e2)` | 아무 타입 | `e1`을 평가해 `v`를 얻고, `env`에 `x = v`를 추가한 환경에서 `e2`를 평가 |
| `LetRec (f, x, e1, e2)` | | `env`에 `f = VRecClosure (f, x, e1, env)`를 추가한 환경에서 `e2`를 평가 |
| `Lambda (x, e)` | | `VClosure (x, e, env)`. 현재 환경을 함께 저장 |
| `App (f, a)` | `f`: function | `f`, `a` 순서로 평가한 뒤 아래 "함수 호출" 규칙으로 본문을 평가 |
| `If (c, e1, e2)` | `c`: bool | `c`가 `true`면 `e1`, `false`면 `e2`를 평가. **선택된 분기만** 평가함 |
| `Add` | int, int 또는 string, string | int 덧셈 또는 문자열 연결. 왼쪽 값의 타입으로 정함 |
| `Subtract` `Multiply` `Divide` | int, int | int |
| `Negate` | int | int |
| `Less` `Greater` `LessEqual` `GreaterEqual` | int, int 또는 string, string | bool. 문자열은 문자 코드 순서(ordinal)로 비교 |
| `Equal` `NotEqual` | 같은 타입끼리 (int, bool, string, list). 리스트는 원소를 차례로 비교. 함수는 비교할 수 없음 | bool |
| `And` `Or` | bool, bool | bool. **단락 평가** |
| `Not` | bool | bool |
| `Nil` | | `VList []` |
| `Cons (e1, e2)` | `e1`: 아무 타입, `e2`: list | `e1`, `e2` 순서로 평가하고 `e2` 리스트 앞에 `e1` 값을 붙임 |
| `Match (e, e1, h, t, e2)` | `e`: list | 빈 리스트면 `e1`을 평가. 아니면 `env`에 `h = 첫 원소`, `t = 나머지 리스트`를 추가하고 `e2`를 평가. **선택된 분기만** 평가함 |

### 함수 호출

호출할 함수 값을 `fv`, 인자 값을 `av`라고 합니다.

| `fv` | 본문을 평가하는 환경 |
|---|---|
| `VClosure (x, body, cenv)` | `cenv` + `{x = av}` |
| `VRecClosure (f, x, body, cenv)` | `cenv` + `{f = fv}` + `{x = av}`. 본문에서 `f`가 자기 자신을 가리킴 |
| 그 밖의 값 | `Type error: expected function but got <타입>` |

- 본문은 **호출한 곳의 환경이 아니라 함수를 정의한 곳의 환경(`cenv`)** 에서 평가합니다.
- 인자 이름이 함수 이름과 같으면 인자가 우선합니다. `{x = av}`를 나중에 추가하기 때문입니다.

### 성질

- **동적 타입 검사**: 타입은 실행 중에 연산을 할 때 검사합니다. 피연산자 타입이 맞지 않으면 `Type error: expected <기대 타입> but got <실제 타입>` 오류가 납니다.
  - `1 + true`는 `Type error: expected int but got bool`입니다.
  - `if 1 then 2 else 3`은 `Type error: expected bool but got int`입니다.
  - `=`, `<>`, `+`, `<` 등은 왼쪽 값의 타입을 기준으로 오른쪽을 검사합니다. `1 = true`는 `expected int`, `true = 1`은 `expected bool` 오류입니다.
  - `"a" + 1`은 `expected string but got int`, `1 + "a"`는 `expected int but got string`입니다.
  - 왼쪽 값이 쓸 수 없는 타입이면 가능한 타입을 모두 적습니다. `true + 1`과 `true < false`는 `expected int or string but got bool`입니다.
- **if 분기 타입**: 두 분기의 타입이 같은지는 검사하지 않습니다. 선택된 분기만 평가하기 때문에 `if true then 1 else false`는 `1`입니다.
- **단락 평가**:
  - `false && e`는 `e`를 평가하지 않고 `false`입니다. `true || e`는 `e`를 평가하지 않고 `true`입니다.
  - 예를 들어 `false && 1 / 0 = 0`은 0으로 나누기 오류 없이 `false`입니다.
- **정수 나눗셈**: 0 쪽으로 버립니다. `7 / 2`는 `3`, `-7 / 2`는 `-3`입니다. 오른쪽이 0이면 `Attempted to divide by zero.` 오류가 납니다.
- **오버플로**: 검사하지 않고 값이 한 바퀴 돕니다. `2147483647 + 1`은 `-2147483648`입니다.
- **정적 범위**: 변수는 자신을 감싸는 `let`의 본문 안에서만 보입니다.
  - `(let x = 1 in x) + x`는 `Unbound variable: x` 오류입니다.
- **섀도잉**: 안쪽 `let`이 같은 이름을 다시 묶으면 본문 안에서는 안쪽 값이 쓰입니다. 본문을 벗어나면 바깥 값이 다시 보입니다.
  - `let x = 1 in (let x = 2 in x) + x`는 `2 + 1`이므로 `3`입니다.
- **재귀는 `let rec`으로만**: `let`의 바인딩 값 `e1`은 바깥 환경에서 평가합니다. 그래서 `e1` 안에서는 자기 자신을 참조할 수 없습니다.
  - `let f x = f x in f 1`은 `Unbound variable: f`입니다.
  - `let f n = n in let f n = f (n + 1) in f 1`에서 안쪽 `f` 본문의 `f`는 바깥 `f`를 가리킵니다. 결과는 `2`입니다.
- **클로저와 정적 범위**: 함수는 정의될 때의 환경을 기억합니다.
  - `let x = 1 in let f y = x + y in let x = 100 in f 1`은 `2`입니다. `101`이 아닙니다.
- **커링과 부분 적용**: 모든 함수는 인자를 하나씩 받습니다. 인자를 일부만 주면 나머지를 기다리는 함수가 됩니다.
  - `let add x y = x + y in let inc = add 1 in inc 41`은 `42`입니다.
- **값에 의한 호출(call-by-value)**: 인자는 함수 본문을 실행하기 전에 평가합니다. 쓰이지 않는 인자도 평가합니다.
  - `let k x = 1 in k (1 / 0)`은 0으로 나누기 오류입니다.
- **함수는 비교할 수 없음**: `(fun x -> x) = (fun x -> x)`는 `Type error: expected int, bool, string or list but got function`입니다.
- **문자열 비교는 문자 코드 순서**: 사전 순서나 현재 문화권의 정렬 규칙을 쓰지 않습니다. 그래서 대문자가 소문자보다 앞입니다.
  - `"B" < "a"`는 `true`, `"ab" < "abc"`는 `true`입니다.
- **문자열에는 아직 내장 함수가 없음**: 길이, 부분 문자열, 숫자와의 변환은 지원하지 않습니다. `+`로 연결하고 `=`, `<` 등으로 비교만 할 수 있습니다.
- **리스트 원소 타입은 검사하지 않음**: `[1; true]`도 만들 수 있습니다. 비교할 때 원소끼리 타입이 다르면 오류입니다.
  - `[1] = [true]`는 `Type error: expected int but got bool`입니다.
  - 길이가 다르면 원소를 비교하지 않고 `false`입니다. 그래서 `[fun x -> x] = []`는 오류 없이 `false`입니다.
- **리스트에는 내장 함수가 없음**: 길이, 합, `map` 같은 연산은 `let rec`과 `match`로 직접 정의합니다.

  ```
  let rec map f xs = match xs with [] -> [] | h :: t -> f h :: map f t in
  map (fun x -> x * x) [1; 2; 3]                  (* [1; 4; 9] *)
  ```
- **재귀 깊이 한계**: 평가기는 재귀 호출마다 .NET 스택을 사용합니다.
  - `calc` 명령은 512MB 스택의 스레드에서 평가하므로 재귀 깊이 약 100,000까지 동작합니다. 1,000,000에서는 넘칩니다.
  - .NET은 스택 오버플로를 잡을 수 없으므로 `Error:` 메시지 없이 `Stack overflow.`를 출력하고 프로세스가 끝납니다.
  - 꼬리 재귀 최적화는 하지 않습니다.

## 5. 오류

| 단계 | 원인 | 메시지 |
|---|---|---|
| 렉서 | 알 수 없는 문자 | `Unexpected character: <문자>` |
| 렉서 | 닫히지 않은 문자열 | `Unterminated string` |
| 렉서 | 지원하지 않는 이스케이프 | `Invalid escape sequence: \<문자>` |
| 렉서 | `int` 범위를 넘는 숫자 | `Value was either too large or too small for an Int32.` |
| 파서 | 문법에 맞지 않는 토큰 나열 (예: `1 +`, `let x = 1`, `if c then 1`, `let in = 1 in 2`, `let rec f = 1 in f`, `fun -> 1`, `[1;]`, `match xs with [] -> 0`) | `parse error` |
| 평가 | 정의되지 않은 변수 | `Unbound variable: <이름>` |
| 평가 | 피연산자 타입 불일치 | `Type error: expected <기대 타입> but got <int\|bool\|string\|function\|list>` |
| 평가 | 리스트가 아닌 값에 `::`나 `match` 사용 (예: `1 :: 2`) | `Type error: expected list but got int` |
| 평가 | 함수가 아닌 값을 호출 (예: `1 2`) | `Type error: expected function but got int` |
| 평가 | 0으로 나누기 | `Attempted to divide by zero.` |
| 평가 | 재귀가 너무 깊음 | `Stack overflow.` (프로세스 종료, 잡을 수 없음) |

파서 오류 메시지에는 아직 위치(줄, 열) 정보가 없습니다.
