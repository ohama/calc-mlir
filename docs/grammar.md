# 문법 (Grammar)

Calc 언어의 어휘 구조, 구문, 의미를 정리한 문서입니다. 구현은 다음 파일에 있습니다.

| 단계 | 파일 |
|---|---|
| 어휘 (토큰) | `src/Calc/Lexer.fsl` |
| 구문 (파서) | `src/Calc/Parser.fsy` |
| AST | `src/Calc/Ast.fs` |
| 타입 추론 | `src/Calc/Infer.fs` |
| 의미 (평가) | `src/Calc/Eval.fs` |

## 1. 어휘 구조

### 토큰

| 토큰 | 패턴 | 예 |
|---|---|---|
| `NUMBER` | `[0-9]+` | `0`, `42` |
| `STRING` | `"`로 시작해서 `"`로 끝남 (아래 "문자열 리터럴" 참고) | `"hello"`, `""`, `"a\"b"` |
| `IDENT` | `[a-z_][A-Za-z0-9_]*` (키워드와 `_` 하나는 제외). 변수와 타입 이름 | `x`, `foo_1`, `_tmp`, `fooBar` |
| `CTOR` | `[A-Z][A-Za-z0-9_]*`. 생성자 (타입 이름으로도 쓸 수 있음) | `Some`, `None`, `Node`, `X2` |
| `TYVAR` | `'` 다음에 `IDENT` 모양. type variable | `'a`, `'key` |
| `UNDERSCORE` | `_` (와일드카드 패턴, 쓰지 않는 인자) | |
| 키워드 | `let` `rec` `in` `fun` `if` `then` `else` `true` `false` `not` `match` `with` `when` `type` `of` | |
| `EQUALS` | `=` | |
| `ARROW` | `->` | |
| `NOTEQUAL` `LESS` `GREATER` `LESSEQUAL` `GREATEREQUAL` | `<>` `<` `>` `<=` `>=` | |
| `AND` `OR` | `&&` `\|\|` | |
| `PLUS` `MINUS` `STAR` `SLASH` | `+` `-` `*` `/` | |
| `LPAREN` `RPAREN` | `(` `)` | |
| `LBRACKET` `RBRACKET` `SEMI` | `[` `]` `;` | |
| `COMMA` | `,` | |
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
- **대소문자 구분**: 키워드는 소문자만 인정합니다. 첫 글자가 대문자면 생성자(`CTOR`)입니다. 그래서 `Let`, `True`, `X`는 생성자이고 변수 이름으로 쓸 수 없습니다(OCaml과 같은 규칙).
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
        | "let" tuplePat "=" expr "in" expr              (* 튜플 분해 *)
        | "let" IDENT params "=" expr "in" expr          (* 함수 정의 *)
        | "let" "rec" IDENT params "=" expr "in" expr    (* 재귀 함수 정의 *)
        | "fun" params "->" expr                         (* 람다 *)
        | "if" expr "then" expr "else" expr
        | "match" expr "with" [ "|" ] arm { "|" arm }    (* 패턴 매칭 *)
        | "type" [ typeParams ] typeName "=" [ "|" ] ctorDecl { "|" ctorDecl } "in" expr
        | expr binop expr
        | "-" expr
        | "not" expr ;

app     = app atom                                       (* 함수 호출 *)
        | atom ;

atom    = NUMBER
        | STRING
        | "true" | "false"
        | IDENT
        | CTOR                                           (* 생성자 *)
        | "(" expr ")"
        | "(" expr "," expr { "," expr } ")"             (* 튜플 *)
        | "[" [ expr { ";" expr } ] "]" ;                (* 리스트 리터럴 *)

arm     = pattern [ "when" expr ] "->" expr ;

pattern = ctorPat [ "::" pattern ] ;                     (* :: 는 오른쪽 결합 *)
ctorPat = CTOR atomPat                                   (* Some x, Node (l, x, r) *)
        | atomPat ;
atomPat = IDENT | "_" | CTOR
        | NUMBER | "-" NUMBER | STRING | "true" | "false"
        | "[" "]"
        | "[" pattern { ";" pattern } "]"                (* 리스트 패턴 *)
        | "(" pattern ")"
        | tuplePat ;
tuplePat = "(" pattern "," pattern { "," pattern } ")" ;

params  = param { param } ;
param   = IDENT | "_" ;

typeParams = TYVAR | "(" TYVAR { "," TYVAR } ")" ;     (* 'a tree, ('k, 'v) map *)
typeName   = IDENT | CTOR ;
ctorDecl   = CTOR [ "of" type ] ;
type       = prodType [ "->" type ] ;                    (* -> 는 오른쪽 결합 *)
prodType   = appType { "*" appType } ;
appType    = atomType { IDENT } ;                        (* int list option *)
atomType   = IDENT | TYVAR | "(" type ")" ;

binop   = "+" | "-" | "*" | "/"
        | "=" | "<>" | "<" | ">" | "<=" | ">="
        | "::"
        | "&&" | "||" ;
```

`expr binop expr` 부분은 그대로 두면 모호합니다. 아래의 우선순위와 associativity로 모호함을 해소합니다. 함수 호출은 우선순위 선언 대신 `app`/`atom` 계층으로 표현했습니다. `Parser.fsy`도 같은 방식으로 작성되어 있으며 shift/reduce 충돌이 없습니다.

### 우선순위와 associativity

위에 있을수록 우선순위가 **낮습니다**. `Parser.fsy`의 선언 순서와 같습니다.

| 우선순위 | 연산자 | associativity | 선언 |
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

### 리스트와 튜플

- 리스트 리터럴 `[a; b; c]`는 `a :: b :: c :: []`의 syntactic sugar입니다. 원소 구분자는 `;`이고, 끝에 `;`를 붙이면(`[1;]`) parse error입니다.
- 튜플은 **괄호 안에** 원소를 `,`로 구분해서 씁니다. 원소는 2개 이상입니다. `(1, "a", true)`, `((1, 2), [3])`
  - `(e)`는 튜플이 아니라 괄호식입니다.
  - F#과 달리 괄호 없는 튜플(`1, 2`)은 지원하지 않습니다. parse error입니다.
  - 리스트 원소 구분자 `;`와 튜플 원소 구분자 `,`는 다릅니다. `[1, 2]`는 parse error이고, `[(1, 2)]`가 튜플 하나짜리 리스트입니다.

### 패턴 매칭

```
match e with
| pattern1 -> e1
| pattern2 when guard -> e2
| ...
```

- 분기는 하나 이상이고, 첫 분기 앞의 `|`는 생략할 수 있습니다.
- **위에서부터** 패턴이 일치하고 가드(`when`)가 `true`인 첫 분기를 고릅니다. 일치하는 분기가 없으면 `Match failure: <값>` 오류입니다.

| 패턴 | 일치하는 값 | 묶는 이름 |
|---|---|---|
| `x` | 모든 값 | `x` |
| `_` | 모든 값 | 없음 |
| `42`, `-1`, `"a"`, `true` | 같은 리터럴 | 없음 |
| `[]` | 빈 리스트 | 없음 |
| `p1 :: p2` | 첫 원소가 `p1`, 나머지 리스트가 `p2`와 일치하는 리스트 | `p1`, `p2`가 묶는 이름 |
| `[p1; p2]` | 원소가 정확히 2개인 리스트 (`p1 :: p2 :: []`와 같음) | |
| `(p1, p2, ...)` | 원소 수가 같고 각 원소가 일치하는 튜플 | |
| `(p)` | `p`와 같음 (괄호로 묶기) | |
| `None`, `Red` | 같은 생성자로 만든 값 | 없음 |
| `Some p`, `Node (l, x, r)` | 같은 생성자로 만든 값이고, 인자가 `p`와 일치 | `p`가 묶는 이름 |

- 패턴은 중첩할 수 있습니다. `(n, s) :: _`, `[[x]]`, `(x :: xt, y :: yt)`
- `::` 패턴은 right-associative입니다. `a :: b :: t`는 `a :: (b :: t)`입니다.
- 생성자 패턴은 `::`보다 강합니다. `Some x :: t`는 `(Some x) :: t`입니다. 생성자의 인자가 단순한 이름이나 리터럴이 아니면 괄호로 감쌉니다(`Some (a, b)`, `Some (Some x)`, `Some (h :: t)`).
- **생성자 패턴은 지금 보이는 생성자여야 합니다.** 오타를 조용히 "불일치"로 넘기지 않으려는 것입니다. 이 검사는 그 분기에 도달했을 때 합니다.
  - 모르는 생성자: `Unknown constructor: Nome`
  - 인자 있는 생성자를 인자 없이: `Constructor Some requires an argument`
  - 인자 없는 생성자에 인자: `Constructor None takes no argument`
- **한 패턴 안에서 같은 이름을 두 번 쓸 수 없습니다.** `(a, a)`는 `Duplicate variable in pattern: a (line L, column C)` 오류입니다. 위치는 패턴의 시작입니다.
- **타입이 달라도 오류가 아니라 불일치입니다.** `match "1" with 1 -> ... | _ -> ...`는 두 번째 분기를 고릅니다. `match 1 with [] -> 0 | h :: t -> h`는 `Match failure: 1`입니다.
- 패턴이 묶은 이름은 그 분기의 가드와 본문에서만 보이고, 바깥의 같은 이름을 가립니다.
- 가드는 bool이어야 합니다. 아니면 `Type error: expected bool but got <타입>`입니다.
- 마지막 분기의 본문은 `->`의 우선순위가 가장 낮으므로 오른쪽 끝까지 이어집니다.
  - `match xs with [] -> 0 | h :: t -> h + 1`의 마지막 본문은 `h + 1`입니다.
- **중첩 `match`**: 분기 본문에 괄호 없이 `match`를 쓰면, 그 뒤의 `|` 분기는 **모두 안쪽 `match`의 것**입니다. F#과 같습니다.
  - `match a with [] -> match b with [] -> 1 | x :: y -> 2 | h :: t -> 3`에서 `x :: y`와 `h :: t` 분기는 모두 안쪽 `match b`의 분기입니다.
  - 바깥 분기로 쓰려면 안쪽 `match`를 괄호로 감쌉니다. `match a with [] -> (match b with ...) | h :: t -> 3`
  - 문법에서는 분기 목록을 끝내는 규칙에 `%prec NOBAR`를 주어 이 동작을 정했습니다(충돌 없음).

### 튜플 분해 `let`

`let (a, b) = e1 in e2`처럼 `let` 왼쪽에 튜플 패턴을 쓸 수 있습니다. 튜플 안에는 어떤 패턴이든 올 수 있습니다(`let (h :: t, n) = ... in ...`).
- 일치하지 않으면 `Match failure: <값>`입니다. `let (a, b) = (1, 2, 3) in a`도 원소 수가 달라서 실패입니다.
- `let` 왼쪽에는 이름 하나 또는 튜플 패턴만 올 수 있습니다. `let [x] = ...`, `let _ = ...`는 지원하지 않습니다.
- `fun`과 함수 정의의 인자는 이름이나 `_`만 됩니다. `fun (a, b) -> ...`는 아직 지원하지 않습니다. 대신 `fun p -> let (a, b) = p in ...`으로 씁니다.
- `_` 인자(`fun _ -> 1`, `let f _ x = x`)는 값을 받아서 버립니다. `_`는 식에 쓸 수 없으므로 참조할 방법이 없습니다.

### algebraic data type (`type`)

```
type shape = Circle of int | Rect of int * int in
let area s = match s with Circle r -> 3 * r * r | Rect (w, h) -> w * h in
map area [Circle 2; Rect (2, 3)]                        (* [12; 6] *)

type 'a tree = Leaf | Node of 'a tree * 'a * 'a tree in
let rec sum t = match t with Leaf -> 0 | Node (l, x, r) -> sum l + x + sum r in
sum (Node (Node (Leaf, 1, Leaf), 2, Node (Leaf, 3, Leaf)))   (* 6 *)
```

- 이 언어에는 최상위 선언이 없으므로 `type ... in 식`처럼 **식**입니다. 생성자는 `in` 뒤의 식 안에서만 보입니다. 본문은 `let`처럼 오른쪽 끝까지 이어집니다.
- 생성자 이름은 대문자로 시작합니다. 타입 이름은 소문자(OCaml 방식)나 대문자로 시작할 수 있습니다.
- type variable은 OCaml처럼 이름 **앞에** 씁니다. `type 'a tree`, `type ('k, 'v) entry` (F#의 `Tree<'a>` 문법은 지원하지 않음)
- 첫 생성자 앞의 `|`는 생략할 수 있습니다. 한 선언 안에서 같은 생성자 이름을 두 번 쓰면 `Duplicate constructor: <이름> (line L, column C)`입니다.
- **`of` 뒤의 타입은 타입 추론이 사용합니다**("6. 타입 추론"). `--no-typecheck`로 평가만 할 때는 튜플 원소 수만 검사합니다.
  - `of int * int`(원소 2개) 생성자는 원소 2개짜리 튜플을 받아야 합니다. `Rect 1`은 `Constructor Rect expects 2 fields but got int`, `Rect (1, 2, 3)`은 `... but got tuple of 3`입니다.
  - `of int`(원소 1개) 생성자는 어떤 값이든 하나 받습니다. `Circle (1, 2)`도 됩니다.
  - 타입 식 문법: `int`, `'a`, `int list`, `'a tree option`(인자가 앞), `int * string`, `int -> bool`, `(int * int)`
- **생성자는 값입니다.**
  - 인자 없는 생성자(`None`, `Leaf`)는 그 자체로 값입니다.
  - 인자 있는 생성자(`Some`, `Node`)는 인자 하나를 받는 함수처럼 쓸 수 있습니다. `map Some [1; 2]`는 `[Some 1; Some 2]`입니다. 출력은 `<fun>`, 타입 이름은 `function`입니다.
- 같은 이름의 생성자를 안쪽 `type`에서 다시 선언하면 안쪽 것이 가립니다. 바깥 타입의 값은 안쪽 생성자 패턴과 일치하지 않습니다.
- 대문자 이름은 `let`이나 `fun`으로 묶을 수 없습니다. `let X = 1 in X`는 parse error입니다.

### `option`

`'a option = None | Some of 'a`는 선언 없이 쓸 수 있습니다(처음 환경에 들어 있음). 값이 없을 수 있는 결과에 씁니다.

```
match tryHead xs with Some x -> x | None -> 0
defaultValue 0 (tryFind (fun x -> x > 10) xs)
```

### 함수 정의의 syntactic sugar

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
| `(e1, e2)` | `Tuple [e1; e2]` |
| `match e with p1 -> e1 \| p2 when g -> e2` | `Match (e, [ (p1, None, e1); (p2, Some g, e2) ])` (분기 순서 유지) |
| `let (a, b) = e1 in e2` | `LetPattern (PTuple [PVar "a"; PVar "b"], e1, e2)` |
| 패턴 `x` / `_` / `1` / `true` / `"s"` | `PVar "x"` / `PWildcard` / `PInt 1` / `PBool true` / `PString "s"` |
| 패턴 `[]` / `p1 :: p2` / `[p1; p2]` | `PNil` / `PCons (p1, p2)` / `PCons (p1, PCons (p2, PNil))` |
| 패턴 `(p1, p2)` | `PTuple [p1; p2]` |
| `None` / `Some e` | `Ctor "None"` / `App (Ctor "Some", e)` |
| 패턴 `None` / `Some p` | `PCtor ("None", None)` / `PCtor ("Some", Some p)` |
| `type 'a t = A \| B of 'a * int in e` | `TypeDecl ("t", ["a"], [ ("A", None); ("B", Some (TTuple [TVar "a"; TName ("int", [])])) ], e)` |
| 타입 `int list` / `'a` / `a * b` / `a -> b` | `TName ("list", [TName ("int", [])])` / `TVar "a"` / `TTuple [a; b]` / `TFun (a, b)` |
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
    | VTuple of Value list                                // (v1, v2, ...)
    | VBuiltin of name * args                             // 내장 함수 (args: 지금까지 받은 인자)
    | VData of typeName * ctor * payload option           // None, Some 1, Rect (2, 3)
    | VConstructor of typeName * ctor * fields            // 인자 있는 생성자 자체 (Some, Rect)
```

평가는 환경 `env : Map<string, Value>`에서 이루어집니다. 처음 환경에는 내장 함수만 들어 있습니다(아래 "내장 함수" 참고). 결과는 `formatValue`로 출력합니다.
- 정수는 `3`, `-3`처럼 출력됩니다.
- 불리언은 `true`, `false`로 출력됩니다.
- 문자열은 따옴표와 이스케이프를 붙인 리터럴 형태로 출력됩니다. 예: `"say \"hi\"\n"`. 출력을 그대로 다시 입력하면 같은 값이 됩니다.
- 함수는 `<fun>`으로 출력됩니다. 내장 함수도 같습니다.
- 리스트는 `[1; 2; 3]`, `[]`, `[[true]; []]`처럼 출력됩니다.
- 튜플은 `(1, "a", [true])`처럼 출력됩니다.
- 생성자 값은 `None`, `Some 1`, `Node (Leaf, 1, Leaf)`처럼 출력됩니다. 다시 입력해도 같은 값이 되도록 인자가 있는 생성자 값과 음수는 괄호로 감쌉니다(`Some (Some 1)`, `Some (-1)`).

타입 오류 메시지에서 문자열의 타입 이름은 `string`, 함수는 `function`, 리스트는 `list`, 튜플은 `tuple`입니다. 생성자 값의 타입 이름은 선언한 타입 이름입니다(`option`, `shape`).

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
| `Equal` `NotEqual` | 같은 타입끼리 (int, bool, string, list, tuple, 같은 이름의 데이터 타입). 리스트와 튜플은 원소를 차례로 비교하고, 원소 수가 다르면 `false`. 데이터 값은 생성자가 다르면 `false`, 같으면 인자를 비교. 함수는 비교할 수 없음 | bool |
| `And` `Or` | bool, bool | bool. **short-circuit evaluation** |
| `Not` | bool | bool |
| `Nil` | | `VList []` |
| `Cons (e1, e2)` | `e1`: 아무 타입, `e2`: list | `e1`, `e2` 순서로 평가하고 `e2` 리스트 앞에 `e1` 값을 붙임 |
| `Tuple [e1; ...]` | 아무 타입 | 왼쪽부터 평가한 `VTuple` |
| `Match (e, cases)` | 아무 타입 | `e`를 평가하고, 위에서부터 패턴이 일치하고 가드가 `true`인 첫 분기의 본문을 패턴이 묶은 이름을 추가한 환경에서 평가. 없으면 `Match failure`. **선택된 분기만** 평가함 |
| `LetPattern (p, e1, e2)` | 아무 타입 | `e1`의 값이 `p`와 일치하면 묶은 이름을 추가한 환경에서 `e2`를 평가. 아니면 `Match failure` |
| `Ctor c` | | `env`에서 `c`의 값(`VData` 또는 `VConstructor`). 없으면 `Unknown constructor: c` |
| `TypeDecl (t, _, ctors, e)` | | 인자 없는 생성자는 `VData (t, c, None)`, 있는 생성자는 `VConstructor (t, c, 원소 수)`로 `env`에 추가하고 `e`를 평가 |

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

- **동적 타입 검사**: 평가기도 연산을 할 때 타입을 검사합니다. 보통은 평가 전에 타입 추론("6. 타입 추론")이 먼저 걸러내므로, 아래 오류들은 `--no-typecheck`로 실행하거나 `Eval.eval`을 직접 부를 때 보입니다. 피연산자 타입이 맞지 않으면 `Type error: expected <기대 타입> but got <실제 타입>` 오류가 납니다.
  - `1 + true`는 `Type error: expected int but got bool`입니다.
  - `if 1 then 2 else 3`은 `Type error: expected bool but got int`입니다.
  - `=`, `<>`, `+`, `<` 등은 왼쪽 값의 타입을 기준으로 오른쪽을 검사합니다. `1 = true`는 `expected int`, `true = 1`은 `expected bool` 오류입니다.
  - `"a" + 1`은 `expected string but got int`, `1 + "a"`는 `expected int but got string`입니다.
  - 왼쪽 값이 쓸 수 없는 타입이면 가능한 타입을 모두 적습니다. `true + 1`과 `true < false`는 `expected int or string but got bool`입니다.
- **if 분기 타입**: 평가기는 두 분기의 타입이 같은지 검사하지 않습니다. 선택된 분기만 평가하기 때문에 `--no-typecheck`로는 `if true then 1 else false`가 `1`입니다. 타입 추론은 이 식을 거부합니다.
- **short-circuit evaluation**:
  - `false && e`는 `e`를 평가하지 않고 `false`입니다. `true || e`는 `e`를 평가하지 않고 `true`입니다.
  - 예를 들어 `false && 1 / 0 = 0`은 0으로 나누기 오류 없이 `false`입니다.
- **정수 나눗셈**: 0 쪽으로 버립니다. `7 / 2`는 `3`, `-7 / 2`는 `-3`입니다. 오른쪽이 0이면 `Attempted to divide by zero.` 오류가 납니다.
- **오버플로**: 검사하지 않고 값이 한 바퀴 돕니다. `2147483647 + 1`은 `-2147483648`입니다.
- **lexical scope**: 변수는 자신을 감싸는 `let`의 본문 안에서만 보입니다.
  - `(let x = 1 in x) + x`는 `Unbound variable: x` 오류입니다.
- **섀도잉**: 안쪽 `let`이 같은 이름을 다시 묶으면 본문 안에서는 안쪽 값이 쓰입니다. 본문을 벗어나면 바깥 값이 다시 보입니다.
  - `let x = 1 in (let x = 2 in x) + x`는 `2 + 1`이므로 `3`입니다.
- **재귀는 `let rec`으로만**: `let`의 바인딩 값 `e1`은 바깥 환경에서 평가합니다. 그래서 `e1` 안에서는 자기 자신을 참조할 수 없습니다.
  - `let f x = f x in f 1`은 `Unbound variable: f`입니다.
  - `let f n = n in let f n = f (n + 1) in f 1`에서 안쪽 `f` 본문의 `f`는 바깥 `f`를 가리킵니다. 결과는 `2`입니다.
- **클로저와 lexical scope**: 함수는 정의될 때의 환경을 기억합니다.
  - `let x = 1 in let f y = x + y in let x = 100 in f 1`은 `2`입니다. `101`이 아닙니다.
- **커링과 partial application**: 모든 함수는 인자를 하나씩 받습니다. 인자를 일부만 주면 나머지를 기다리는 함수가 됩니다.
  - `let add x y = x + y in let inc = add 1 in inc 41`은 `42`입니다.
- **call-by-value**: 인자는 함수 본문을 실행하기 전에 평가합니다. 쓰이지 않는 인자도 평가합니다.
  - `let k x = 1 in k (1 / 0)`은 0으로 나누기 오류입니다.
- **함수는 비교할 수 없음**: `(fun x -> x) = (fun x -> x)`는 `Type error: expected int, bool, string, list, tuple or data but got function`입니다.
- **문자열 비교는 문자 코드 순서**: 사전 순서나 현재 문화권의 정렬 규칙을 쓰지 않습니다. 그래서 대문자가 소문자보다 앞입니다.
  - `"B" < "a"`는 `true`, `"ab" < "abc"`는 `true`입니다.
- **문자열 처리는 내장 함수로**: 길이, 부분 문자열, 검색, 나누기, 숫자 변환은 아래 "내장 함수"를 씁니다.
- **리스트 원소 타입은 검사하지 않음**: `[1; true]`도 만들 수 있습니다. 비교할 때 원소끼리 타입이 다르면 오류입니다.
  - `[1] = [true]`는 `Type error: expected int but got bool`입니다.
  - 길이가 다르면 원소를 비교하지 않고 `false`입니다. 그래서 `[fun x -> x] = []`는 오류 없이 `false`입니다.
- **리스트 처리는 내장 함수 또는 `match`로**: `map`, `filter`, `fold` 등은 아래 "내장 함수"에 있습니다. 직접 정의해도 됩니다. 같은 이름으로 정의하면 내장 함수가 가려집니다.

  ```
  let rec map f xs = match xs with [] -> [] | h :: t -> f h :: map f t in
  map (fun x -> x * x) [1; 2; 3]                  (* [1; 4; 9] *)
  ```
- **재귀 깊이 한계**: 평가기는 재귀 호출마다 .NET 스택을 사용합니다.
  - `calc` 명령은 512MB 스택의 스레드에서 평가하므로 재귀 깊이 약 100,000까지 동작합니다. 1,000,000에서는 넘칩니다.
  - .NET은 스택 오버플로를 잡을 수 없으므로 `Error:` 메시지 없이 `Stack overflow.`를 출력하고 프로세스가 끝납니다.
  - 꼬리 재귀 최적화는 하지 않습니다.

### 내장 함수

처음 환경에 들어 있는 함수입니다. 문법상으로는 보통 변수이므로 따로 키워드가 없습니다.

#### 문자열

| 함수 | 타입 | 예 | 결과 |
|---|---|---|---|
| `length` | string → int, list → int | `length "hello"`, `length [1; 2]` | `5`, `2` |
| `substr s start count` | string → int → int → string | `substr "hello" 1 3` | `"ell"` |
| `upper`, `lower` | string → string | `upper "abc"` | `"ABC"` |
| `trim` | string → string | `trim "  a b  "` | `"a b"` (앞뒤 공백 제거) |
| `contains s sub` | string → string → bool | `contains "hello" "ell"` | `true` |
| `startsWith s prefix`, `endsWith s suffix` | string → string → bool | `startsWith "hello" "he"` | `true` |
| `indexOf s sub` | string → string → int | `indexOf "hello" "l"` | `2` (없으면 `-1`) |
| `replace s old new` | string → string → string → string | `replace "a-b" "-" "+"` | `"a+b"` (모두 바꿈) |
| `split s sep` | string → string → list | `split "a,b,,c" ","` | `["a"; "b"; ""; "c"]` |
| `join sep xs` | string → list → string | `join ", " ["a"; "b"]` | `"a, b"` |
| `toString v` | 아무 값 → string | `toString 42`, `toString [1]` | `"42"`, `"[1]"` |
| `toInt s` | string → int | `toInt "-42"` | `-42` |

- **인자 순서**: 다루는 문자열이 첫 인자입니다(`contains s sub`). 예외로 `join`은 .NET `String.Join`처럼 구분자가 먼저입니다.
- **커링**: 보통 함수처럼 인자를 하나씩 받습니다. 인자가 다 모일 때까지 실행하지 않고, 인자 타입도 그때 검사합니다.
  - `let first = substr "hello" 0 in first 2`는 `"he"`입니다.
  - `map upper (split "a b" " ")`처럼 다른 함수에 넘길 수 있습니다.
- **가리기**: `let length = 5 in length`처럼 같은 이름을 `let`으로 다시 묶으면 내장 함수가 가려집니다.
- **음수 인자는 괄호로**: `toString -5`는 `toString - 5`(뺄셈)입니다. `toString (-5)`로 씁니다.
- **위치와 길이는 UTF-16 code unit**: .NET `string`과 같습니다. 한글 한 글자는 1이지만 이모지(`"😀"`)는 2입니다.
- **문화권에 영향받지 않음**: `upper`/`lower`는 `ToUpperInvariant`/`ToLowerInvariant`이고, `contains`/`startsWith`/`endsWith`/`indexOf`/`replace`는 문자 코드 그대로(ordinal) 비교합니다.
- **세부 동작**:
  - `contains s ""`와 `startsWith s ""`는 `true`, `indexOf s ""`는 `0`입니다.
  - `split s ""`는 한 글자(UTF-16 code unit)씩 나눕니다. `split "" ","`는 `[""]`입니다.
  - `toString`은 문자열은 그대로 돌려주고, 나머지 값은 출력 형태로 바꿉니다. 리스트 안의 문자열은 따옴표가 붙습니다(`toString ["a"]`는 `"[\"a\"]"`).
  - `toInt`는 선택적인 `-`/`+` 부호와 숫자만 받습니다. 공백이 있거나(`" 4"`) `int` 범위를 넘으면 오류입니다.

#### 리스트

| 함수 | 타입 | 예 | 결과 |
|---|---|---|---|
| `head xs`, `tail xs` | list → 원소, list → list | `head [1; 2]`, `tail [1; 2]` | `1`, `[2]` |
| `isEmpty xs` | list → bool | `isEmpty []` | `true` |
| `nth xs i` | list → int → 원소 | `nth [10; 20] 1` | `20` (0부터 셈) |
| `reverse xs` | list → list | `reverse [1; 2; 3]` | `[3; 2; 1]` |
| `append xs ys` | list → list → list | `append [1] [2; 3]` | `[1; 2; 3]` |
| `range a b` | int → int → list | `range 1 5` | `[1; 2; 3; 4; 5]` (`b` 포함, `a > b`면 `[]`) |
| `sum xs` | list → int | `sum [1; 2; 3]` | `6` |
| `sort xs` | list → list | `sort [3; 1; 2]` | `[1; 2; 3]` |
| `map f xs` | 함수 → list → list | `map (fun x -> x * 2) [1; 2]` | `[2; 4]` |
| `filter f xs` | 함수 → list → list | `filter (fun x -> x > 1) [1; 2; 3]` | `[2; 3]` |
| `fold f acc xs` | 함수 → 초기값 → list → 값 | `fold (fun acc x -> acc + x) 0 [1; 2; 3]` | `6` |
| `exists f xs`, `forall f xs` | 함수 → list → bool | `exists (fun x -> x = 2) [1; 2]` | `true` |

- **인자 순서**: higher-order function(`map`, `filter`, `fold`, `exists`, `forall`)은 F#의 `List` 모듈처럼 **함수가 먼저, 리스트가 마지막**입니다. 그래서 `let inc = map (fun x -> x + 1) in inc [1; 2]`처럼 partial application을 하기 좋습니다. 나머지는 문자열 함수처럼 다루는 리스트가 먼저입니다(`nth xs i`, `append xs ys`).
- **`fold`**: F#의 `List.fold`와 같이 왼쪽부터 접고, 함수는 `fun acc x -> ...` 순서로 인자를 받습니다. `fold (fun acc x -> x :: acc) [] [1; 2; 3]`은 `[3; 2; 1]`입니다.
- **조건 함수**: `filter`, `exists`, `forall`에 넘긴 함수는 bool을 반환해야 합니다. 아니면 `Type error: expected bool but got <타입>`입니다.
- **`sum`**: 원소는 모두 int여야 합니다. `+`와 같이 오버플로를 검사하지 않고 값이 한 바퀴 돕니다(`sum [2147483647; 1]`은 `-2147483648`).
- **`sort`**: `<`와 같은 순서를 씁니다. 원소가 모두 int이거나 모두 string이어야 합니다. 섞여 있으면 정렬 전에 `Type error`를 냅니다.
- **`exists`/`forall`**: 결과가 정해지면 나머지 원소에는 함수를 호출하지 않습니다. 빈 리스트에서 `exists`는 `false`, `forall`은 `true`입니다.
- **`range`**: 결과 리스트를 한 번에 만듭니다. 아주 큰 범위는 메모리를 그만큼 씁니다.

#### 튜플

| 함수 | 타입 | 예 | 결과 |
|---|---|---|---|
| `fst p`, `snd p` | 원소 2개짜리 튜플 → 원소 | `fst (1, "a")`, `snd (1, "a")` | `1`, `"a"` |

원소가 3개 이상인 튜플은 패턴으로 꺼냅니다. `let (a, b, c) = t in ...`

#### option

| 함수 | 타입 | 예 | 결과 |
|---|---|---|---|
| `tryHead xs` | list → option | `tryHead [1; 2]`, `tryHead []` | `Some 1`, `None` |
| `tryFind f xs` | 함수 → list → option | `tryFind (fun x -> x > 1) [1; 2; 3]` | `Some 2` |
| `defaultValue d opt` | 기본값 → option → 값 | `defaultValue 0 None` | `0` |

## 5. 오류

| 단계 | 원인 | 메시지 |
|---|---|---|
| 렉서 | 알 수 없는 문자 | `Unexpected character: <문자> (line L, column C)` |
| 렉서 | 닫히지 않은 문자열 | `Unterminated string (line L, column C)`. 위치는 여는 `"` |
| 렉서 | 지원하지 않는 이스케이프 | `Invalid escape sequence: \<문자> (line L, column C)`. 위치는 `\` |
| 렉서 | `int` 범위를 넘는 숫자 | `Integer literal out of range: 2147483648 (line L, column C)` |
| 파서 | 문법에 맞지 않는 토큰 (예: `1 + + 2`, `let in = 1 in 2`, `[1;]`) | `parse error: unexpected '<토큰>' (line L, column C)` |
| 파서 | 식이 끝나지 않았는데 입력이 끝남 (예: `1 +`, `let x = 1`, `if c then 1`, `match xs with [] -> 0`) | `parse error: unexpected end of input (line L, column C)` |
| 파서 | 한 `type` 선언 안에 같은 생성자 이름이 두 번 | `Duplicate constructor: <이름> (line L, column C)` |
| 파서 | 한 패턴 안에 같은 이름이 두 번 (예: `(a, a)`) | `Duplicate variable in pattern: a (line L, column C)`. 위치는 패턴의 시작 |
| 평가 | 정의되지 않은 변수 | `Unbound variable: <이름>` |
| 평가 | 피연산자 타입 불일치 | `Type error: expected <기대 타입> but got <int\|bool\|string\|function\|list\|tuple\|타입 이름>` |
| 평가 | `::` 오른쪽이 리스트가 아님 (예: `1 :: 2`) | `Type error: expected list but got int` |
| 평가 | `match`나 `let` 튜플 분해에서 일치하는 패턴이 없음 | `Match failure: <값>` |
| 평가 | 함수가 아닌 값을 호출 (예: `1 2`) | `Type error: expected function but got int` |
| 평가 | 0으로 나누기 | `Attempted to divide by zero.` |
| 평가 | `substr`의 범위가 문자열 밖 | `substr: index out of range (length 5, start 3, count 5)` |
| 평가 | `replace`의 찾을 문자열이 빈 문자열 | `replace: empty search string` |
| 평가 | `toInt`가 정수로 읽을 수 없는 문자열 | `toInt: invalid integer: "abc"` |
| 평가 | 빈 리스트에 `head`/`tail` | `head: empty list`, `tail: empty list` |
| 평가 | `nth`의 위치가 리스트 밖 | `nth: index out of range (length 1, index 1)` |
| 평가 | `fst`/`snd`에 원소 2개짜리 튜플이 아닌 값 | `Type error: expected pair but got <타입>` |
| 평가 | 선언되지 않은(보이지 않는) 생성자 | `Unknown constructor: <이름>` |
| 평가 | 생성자 인자의 튜플 원소 수가 선언과 다름 | `Constructor Rect expects 2 fields but got int` |
| 평가 | 패턴에서 생성자 인자 유무가 선언과 다름 | `Constructor Some requires an argument`, `Constructor None takes no argument` |
| 평가 | 재귀가 너무 깊음 | `Stack overflow.` (프로세스 종료, 잡을 수 없음) |

### 오류 위치

렉서와 파서 오류(`Errors.SyntaxError`)에는 위치가 붙습니다. 평가 오류에는 아직 위치가 없습니다.

- 줄과 열은 **1부터** 셉니다. 열은 UTF-16 문자 단위이고 탭도 한 칸입니다.
- 줄바꿈은 `\n`과 `\r\n` 모두 한 줄로 셉니다. 문자열 리터럴 안의 줄바꿈도 셉니다.
- 파서 오류의 위치는 **문법에 맞지 않는 첫 토큰의 시작**입니다. 문자열 토큰은 여는 `"`가 시작입니다.
- 입력이 중간에 끝난 경우의 위치는 **마지막 글자 바로 뒤**입니다. 끝에 있는 공백과 줄바꿈은 건너뜁니다. 그래서 `echo "1 +" | calc`처럼 줄바꿈이 붙은 입력도 `line 1, column 4`입니다.
- 빈 입력은 `line 1, column 1`입니다.

`calc` 명령은 메시지 아래에 오류가 난 줄과 `^` 표시를 표준 에러로 출력합니다.

```
$ calc -e "1 + + 2"
Error: parse error: unexpected '+' (line 1, column 5)
  1 | 1 + + 2
    |     ^
```

- 오류 위치는 "파서가 더 진행할 수 없게 된 곳"입니다. 실제 실수는 더 앞에 있을 수 있습니다.
  - 예를 들어 `let x = 1 in\n  let y = 2\n  x + y`에서 빠진 것은 둘째 줄의 `in`입니다. 하지만 파서는 `2\n  x + y`를 함수 호출 `2 x + y`로 읽으므로 입력 끝(`line 3, column 8`)에서야 오류를 냅니다.

## 6. 타입 추론

`calc`는 파싱한 다음, 평가하기 **전에** Hindley–Milner 타입 추론(`Infer.typecheck`)을 실행합니다. 타입이 맞지 않으면 `Error: Type mismatch: ...`를 출력하고 평가하지 않습니다. 추론한 타입은 `Type:` 줄에 나옵니다. `--no-typecheck`를 주면 추론을 건너뜁니다.

### 타입

| 타입 | 예 |
|---|---|
| `int`, `bool`, `string` | `1`, `true`, `"a"` |
| `t list` | `[1; 2]`: `int list` |
| `t1 * t2 * ...` | `(1, "a")`: `int * string` |
| `t1 -> t2` | `fun x -> x + 1`: `int -> int` |
| `t option` | `Some 1`: `int option` |
| 사용자 타입 | `Node (Leaf, 1, Leaf)`: `int tree` |
| type variable | `'a`, `'b`, ... |

출력할 때 `->`는 right-associative고, `*`가 `->`보다, type argument(`int list`)가 `*`보다 강합니다. 필요한 곳에만 괄호를 씁니다. 예: `('a -> 'b) -> 'a list -> 'b list`, `(int * int) list`

### 규칙

- **let-polymorphism**: `let`으로 묶은 값의 타입은 generalization됩니다. `let id x = x in (id 1, id true)`는 `int * bool`입니다.
  - generalization은 `let`, `let rec`, `let (a, b) = ...`에서만 합니다. 함수 인자는 monomorphic입니다. `fun f -> (f 1, f true)`는 오류입니다.
- **`if`**: 조건은 `bool`, 두 분기는 같은 타입입니다.
- **리스트**: 원소는 모두 같은 타입입니다. `[1; true]`는 오류입니다.
- **`=`, `<>`**: 같은 타입이면 어떤 타입이든 됩니다(`'a -> 'a -> bool`). OCaml과 같습니다. 함수끼리 비교하면 타입 검사는 통과하지만 실행 중에 오류가 납니다.
- **`+`, `<`, `>`, `<=`, `>=`**: 두 피연산자가 같은 타입이고, 그 타입은 `int` 또는 `string`이어야 합니다.
  - 이런 "allowed list가 있는 type variable"은 **generalize하지 않습니다**. 처음 쓰인 곳이 타입을 정합니다.
    - `let add x y = x + y in add "a" "b"`는 `string`입니다.
    - `let add x y = x + y in (add 1 2, add "a" "b")`는 오류입니다(F#에서도 `add`는 한 타입).
  - 끝까지 정해지지 않으면 첫 번째 허용 타입으로 정합니다. `fun x y -> x + y`는 `int -> int -> int`입니다(F#과 같음).
- **내장 함수 `length`**: `string` 또는 리스트, **`sort`**: `int list` 또는 `string list`. 같은 allowed list 규칙을 따릅니다. 정해지지 않으면 `length`는 `string -> int`입니다.
- **패턴**: 패턴의 타입은 대상 식의 타입과 같아야 합니다. `match "1" with 1 -> ...`, 원소 수가 다른 튜플 패턴은 오류입니다.
- **생성자**: `type` 선언의 `of` 타입으로 생성자 타입을 만듭니다.
  - `type 'a tree = Leaf | Node of 'a tree * 'a * 'a tree`에서 `Leaf : 'a tree`, `Node : 'a tree * 'a * 'a tree -> 'a tree`
  - 선언에 쓴 타입 이름과 type variable은 알려진 것이어야 합니다: `Unknown type: foo`, `Unbound type variable: 'b`, `Type list expects 1 type argument(s) but got 0`
  - 같은 이름의 `type`을 다시 선언하면 다른 타입입니다. 오류 메시지에서는 `t/1`, `t/2`처럼 구분합니다.

### 내장 함수의 타입

| 함수 | 타입 |
|---|---|
| `length` | `'a -> int` (`'a`는 `string` 또는 리스트) |
| `substr` | `string -> int -> int -> string` |
| `upper`, `lower`, `trim` | `string -> string` |
| `contains`, `startsWith`, `endsWith` | `string -> string -> bool` |
| `indexOf` | `string -> string -> int` |
| `replace` | `string -> string -> string -> string` |
| `split` | `string -> string -> string list` |
| `join` | `string -> string list -> string` |
| `toString` | `'a -> string` |
| `toInt` | `string -> int` |
| `head`, `tail`, `isEmpty`, `nth` | `'a list -> 'a`, `'a list -> 'a list`, `'a list -> bool`, `'a list -> int -> 'a` |
| `reverse`, `append` | `'a list -> 'a list`, `'a list -> 'a list -> 'a list` |
| `range`, `sum` | `int -> int -> int list`, `int list -> int` |
| `sort` | `'a list -> 'a list` (`'a`는 `int` 또는 `string`) |
| `map`, `filter` | `('a -> 'b) -> 'a list -> 'b list`, `('a -> bool) -> 'a list -> 'a list` |
| `fold` | `('b -> 'a -> 'b) -> 'b -> 'a list -> 'b` |
| `exists`, `forall` | `('a -> bool) -> 'a list -> bool` |
| `fst`, `snd` | `'a * 'b -> 'a`, `'a * 'b -> 'b` |
| `tryHead`, `tryFind` | `'a list -> 'a option`, `('a -> bool) -> 'a list -> 'a option` |
| `defaultValue` | `'a -> 'a option -> 'a` |
| `Some`, `None` | `'a -> 'a option`, `'a option` |

### 타입 오류

| 원인 | 메시지 |
|---|---|
| 타입이 다름 | `Type mismatch: expected <기대 타입> but got <실제 타입>` (예: `[1; true]`는 `expected int list but got bool list`) |
| allowed list에 없는 타입 | `Type mismatch: expected int or string but got bool` |
| 함수가 아닌 값을 호출 | `Type mismatch: expected function but got int` |
| 자기 자신을 포함하는 타입 (`fun x -> x x`) | `Type mismatch: cannot construct infinite type 'a = 'a -> 'b` |
| 정의되지 않은 변수, 생성자 | `Unbound variable: x`, `Unknown constructor: X` |
| 생성자 패턴의 인자 유무 | `Constructor Some requires an argument`, `Constructor None takes no argument` |
| `type` 선언의 타입 | `Unknown type: foo`, `Unbound type variable: 'b`, `Duplicate type variable: 'a`, `Type list expects 1 type argument(s) but got 0` |

- **위치:** 오류에는 문제가 된 식이나 패턴의 위치가 붙습니다(OCaml처럼). `calc`는 파서 오류와 같은 형식으로 위치와 `^`를 출력합니다.

  ```
  Error: Type mismatch: expected int but got bool (line 2, column 5)
    2 |   f true
      |     ^
  ```

  - 함수 인자가 맞지 않으면 인자, `if` 조건이면 조건, 분기 타입이 다르면 `else` 분기를 가리킵니다.
  - 연산자 피연산자, `::`의 오른쪽, 리스트 리터럴의 원소(`[1; true]`의 `true`), 패턴, 가드, 분기 본문도 그 자체를 가리킵니다.
  - 정의되지 않은 이름은 그 이름을 가리킵니다.
- **위치 기록 방식:** 위치는 AST에 넣지 않습니다. 파서가 노드마다 시작 위치를 따로 기록합니다(`Errors.positionOf`). 그래서 AST 모양과 `%A` 출력은 그대로입니다. `TypeError`의 `Message`에는 위치가 없고, 위치는 `position` 필드에 있습니다.
- 메시지의 두 타입은 unification이 실패한 시점의 **전체 타입**입니다. 일부가 이미 unify된 상태로 보일 수 있습니다.

### 빠진 match 분기 경고

타입 추론이 끝나면 `match`와 `let` 패턴이 모든 값을 덮는지 검사합니다. 덮지 못하면, 덮지 못하는 값의 예와 위치를 담은 경고를 표준 에러로 출력합니다. 경고만 하고 실행은 계속합니다. OCaml의 Warning 8, F#의 FS0025와 같습니다.

```
$ calc -e "match Some 1 with | Some x -> x"
Warning: this match is not exhaustive. Example of a value not matched: None (line 1, column 1)
  1 | match Some 1 with | Some x -> x
    | ^
```

| 패턴 | 값의 예 |
|---|---|
| `match xs with \| x :: _ -> x` | `[]` |
| `match (b, n) with \| (true, _) -> 1 \| (false, 0) -> 2` | `(false, 1)` |
| `match xs with \| [a; b] -> a \| [] -> 0` | `[_]` |
| `match o with \| Some (Some _) -> 1 \| None -> 0` | `Some None` |
| `match n with \| 0 -> 0 \| 1 -> 1` | `2` |
| `let (a, 1) = p in a` | `(_, 0)` (`this let pattern is not exhaustive`) |

- **생성자 목록:** `bool`, 리스트, `option`, 튜플, 사용자 타입은 생성자를 모두 압니다. `int`와 `string`은 값이 무한히 많으므로 `_`나 변수가 있어야 덮습니다.
- **가드:** 가드가 있는 분기는 실패할 수 있으므로 덮는 데 세지 않습니다(OCaml, F#과 같음).
- **알고리즘:** Maranget, "Warnings for pattern matching"(2007)의 usefulness 검사입니다. 값의 예도 같은 계산에서 나옵니다(`Infer.typecheckWithWarnings`).
- **같은 결과:** 경고가 없는 프로그램은 실행 중에 `Match failure`가 나지 않습니다. 평가 예제로 이것을 테스트합니다(`DiagnosticTests`).
- **아직 하지 않는 것:** 쓰이지 않는 분기 경고(OCaml Warning 11)는 아직 없습니다.

### soundness와 남은 실행 중 오류

타입 검사를 통과한 프로그램은 실행 중에 `Type error`가 나지 않습니다. 함수끼리 `=` 비교만 예외입니다(테스트: `InferTests`의 soundness 테스트). 다음 오류는 여전히 실행 중에 납니다.

- `Match failure` (빠진 분기가 있으면 실행 전에 경고함, 아래), `head []`, `nth`/`substr` 범위, `toInt` 변환 실패
- 0으로 나누기, 재귀가 너무 깊음
- 함수끼리 `=` 비교: 타입은 `'a -> 'a -> bool`로 통과하고, 실행 중에 `Type error: expected int, bool, string, list, tuple or data but got function`

