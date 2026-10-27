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
| `IDENT` | `[A-Za-z_][A-Za-z0-9_]*` (키워드 제외) | `x`, `foo_1`, `_tmp` |
| 키워드 | `let` `in` `if` `then` `else` `true` `false` `not` | |
| `EQUALS` | `=` | |
| `NOTEQUAL` `LESS` `GREATER` `LESSEQUAL` `GREATEREQUAL` | `<>` `<` `>` `<=` `>=` | |
| `AND` `OR` | `&&` `\|\|` | |
| `PLUS` `MINUS` `STAR` `SLASH` | `+` `-` `*` `/` | |
| `LPAREN` `RPAREN` | `(` `)` | |
| `EOF` | 입력의 끝 | |

### 규칙

- **공백**: 스페이스, 탭, 줄바꿈(`\n`, `\r\n`)은 토큰을 구분하는 역할만 하고 무시됩니다.
- **가장 긴 일치**: 렉서는 가장 길게 일치하는 패턴을 고릅니다. 길이가 같으면 `Lexer.fsl`에서 앞에 있는 규칙이 이깁니다.
  - 키워드 규칙이 `IDENT` 규칙보다 앞에 있으므로 `let`, `if`, `true` 등은 키워드입니다.
  - 키워드보다 길게 일치하는 `letx`, `iffy`, `trueish`, `notx`는 `IDENT`입니다.
  - `<=`는 `<`와 `=`로 나뉘지 않고 `LESSEQUAL` 하나가 됩니다. `<>`, `>=`도 같습니다.
  - `2x`는 `NUMBER 2`와 `IDENT x`로 나뉩니다.
- **대소문자 구분**: 키워드는 소문자만 인정합니다. `Let`, `True`는 `IDENT`입니다.
- **숫자 리터럴**: 음이 아닌 정수만 리터럴입니다. `-5`는 `MINUS`와 `NUMBER 5`로 나뉘고, 파서가 단항 마이너스로 처리합니다.
  - 32비트 `int` 범위를 넘는 리터럴(예: `2147483648`)은 렉서 단계에서 오류가 납니다.
- **그 밖의 문자**: 위 패턴에 맞지 않는 문자는 `Unexpected character: <문자>` 오류를 냅니다. `&`나 `|` 하나만 쓴 경우도 여기에 해당합니다.
- **주석**: 아직 지원하지 않습니다.

## 2. 구문

### EBNF

```ebnf
program = expr EOF ;

expr    = NUMBER
        | "true" | "false"
        | IDENT
        | "let" IDENT "=" expr "in" expr
        | "if" expr "then" expr "else" expr
        | expr binop expr
        | "-" expr
        | "not" expr
        | "(" expr ")" ;

binop   = "+" | "-" | "*" | "/"
        | "=" | "<>" | "<" | ">" | "<=" | ">="
        | "&&" | "||" ;
```

이 문법은 그대로 두면 모호합니다. 아래의 우선순위와 결합 방향으로 모호함을 해소합니다. `Parser.fsy`도 같은 방식으로 작성되어 있으며 shift/reduce 충돌이 없습니다.

### 우선순위와 결합 방향

위에 있을수록 우선순위가 **낮습니다**. `Parser.fsy`의 선언 순서와 같습니다.

| 우선순위 | 연산자 | 결합 방향 | 선언 |
|---|---|---|---|
| 1 (가장 낮음) | `let ... in`, `if ... then ... else` | — | `%nonassoc IN ELSE` |
| 2 | `\|\|` | 왼쪽 | `%left OR` |
| 3 | `&&` | 왼쪽 | `%left AND` |
| 4 | `=` `<>` `<` `>` `<=` `>=` | 왼쪽 | `%left EQUALS NOTEQUAL ...` |
| 5 | `+` `-` (이항) | 왼쪽 | `%left PLUS MINUS` |
| 6 | `*` `/` | 왼쪽 | `%left STAR SLASH` |
| 7 (가장 높음) | `-` (단항), `not` | — | `%nonassoc UMINUS NOT` |

F#과 같은 순서입니다. F#에서 `not`은 함수이고 함수 적용은 어떤 연산자보다 먼저 묶이므로, 여기서도 `not`이 가장 높은 우선순위를 가집니다.

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

> 비교 연산자를 `%nonassoc`로 선언하지 않은 이유: fsyacc는 `%nonassoc` 연산자가 연달아 나와도 parse error를 만들지 않습니다. 대신 shift/reduce 충돌을 보고하고 shift를 고릅니다. 그래서 `%left`로 선언하고, `1 < 2 < 3` 같은 식은 평가 단계의 타입 검사에서 걸러냅니다.

### `let`과 `if`의 범위

`IN`과 `ELSE`의 우선순위가 가장 낮기 때문에 `let` 본문과 `else` 분기는 **오른쪽으로 갈 수 있는 데까지** 이어집니다.

| 입력 | 해석 |
|---|---|
| `let x = 1 in x + 2` | `let x = 1 in (x + 2)` |
| `if c then 1 else 2 + 3` | `if c then 1 else (2 + 3)` |
| `1 + let x = 2 in x * 3` | `1 + (let x = 2 in (x * 3))` |
| `-let x = 1 in x + 2` | `-(let x = 1 in (x + 2))` |
| `(let x = 1 in x) + 10` | 괄호로 본문 범위를 제한 |
| `(if c then 1 else 2) + 10` | 괄호로 `else` 분기 범위를 제한 |

- `then` 분기는 `then`과 `else` 사이에 있으므로 범위가 분명합니다. 그 안에 다른 `if`를 바로 써도 됩니다.
  - `if a then if b then 1 else 2 else 3`은 `if a then (if b then 1 else 2) else 3`입니다.
- `if`는 식이므로 **`else`가 반드시 있어야 합니다**. `if c then 1`은 parse error입니다.
- `let`의 `=`와 비교 연산자 `=`는 같은 토큰이지만 위치로 구분됩니다.
  - `let x = 1 = 1 in x`에서 첫 번째 `=`는 바인딩이고, 두 번째 `=`는 비교입니다. 결과는 `true`입니다.

## 3. AST 대응

| 구문 | AST (`Ast.Expr`) |
|---|---|
| `n` | `Number n` |
| `true` / `false` | `Bool true` / `Bool false` |
| `x` | `Var "x"` |
| `let x = e1 in e2` | `Let ("x", e1, e2)` |
| `if c then e1 else e2` | `If (c, e1, e2)` |
| `e1 + e2` / `-` / `*` / `/` | `Add` / `Subtract` / `Multiply` / `Divide` |
| `-e` | `Negate e` |
| `e1 = e2` / `<>` | `Equal` / `NotEqual` |
| `e1 < e2` / `>` / `<=` / `>=` | `Less` / `Greater` / `LessEqual` / `GreaterEqual` |
| `e1 && e2` / `\|\|` | `And` / `Or` |
| `not e` | `Not e` |
| `(e)` | `e` (괄호는 AST에 남지 않음) |

## 4. 의미 (평가 규칙)

### 값

```fsharp
type Value =
    | VInt of int      // 32비트 정수
    | VBool of bool    // true / false
```

평가는 환경 `env : Map<string, Value>`에서 이루어집니다. 처음 환경은 비어 있습니다. 결과는 `formatValue`로 출력합니다. 정수는 `3`, `-3`처럼, 불리언은 `true`, `false`로 출력됩니다.

### 규칙

| 식 | 피연산자 타입 | 결과 |
|---|---|---|
| `Number n` | | `VInt n` |
| `Bool b` | | `VBool b` |
| `Var x` | | `env`에서 `x`의 값. 없으면 `Unbound variable: x` 오류 |
| `Let (x, e1, e2)` | 아무 타입 | `e1`을 평가해 `v`를 얻고, `env`에 `x = v`를 추가한 환경에서 `e2`를 평가 |
| `If (c, e1, e2)` | `c`: bool | `c`가 `true`면 `e1`, `false`면 `e2`를 평가. **선택된 분기만** 평가함 |
| `Add` `Subtract` `Multiply` `Divide` | int, int | int |
| `Negate` | int | int |
| `Less` `Greater` `LessEqual` `GreaterEqual` | int, int | bool |
| `Equal` `NotEqual` | 같은 타입끼리 (int, int 또는 bool, bool) | bool |
| `And` `Or` | bool, bool | bool. **단락 평가** |
| `Not` | bool | bool |

### 성질

- **동적 타입 검사**: 타입은 실행 중에 연산을 할 때 검사합니다. 피연산자 타입이 맞지 않으면 `Type error: expected <기대 타입> but got <실제 타입>` 오류가 납니다.
  - `1 + true`는 `Type error: expected int but got bool`입니다.
  - `if 1 then 2 else 3`은 `Type error: expected bool but got int`입니다.
  - `=`, `<>`는 왼쪽 값의 타입을 기준으로 오른쪽을 검사합니다. `1 = true`는 `expected int`, `true = 1`은 `expected bool` 오류입니다.
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
- **재귀 없음**: 바인딩 값 `e1`은 바깥 환경에서 평가합니다. 그래서 `e1` 안에서는 자기 자신 `x`를 참조할 수 없습니다.

## 5. 오류

| 단계 | 원인 | 메시지 |
|---|---|---|
| 렉서 | 알 수 없는 문자 | `Unexpected character: <문자>` |
| 렉서 | `int` 범위를 넘는 숫자 | `Value was either too large or too small for an Int32.` |
| 파서 | 문법에 맞지 않는 토큰 나열 (예: `1 +`, `let x = 1`, `if c then 1`, `let in = 1 in 2`) | `parse error` |
| 평가 | 정의되지 않은 변수 | `Unbound variable: <이름>` |
| 평가 | 피연산자 타입 불일치 | `Type error: expected <int\|bool> but got <int\|bool>` |
| 평가 | 0으로 나누기 | `Attempted to divide by zero.` |

파서 오류 메시지에는 아직 위치(줄, 열) 정보가 없습니다.
