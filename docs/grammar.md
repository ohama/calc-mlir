# 문법 (Grammar)

Calc 언어의 어휘 구조, 구문, 의미를 정리한 문서입니다. 구현은 다음 파일에 있습니다.

| 단계 | 파일 |
|---|---|
| 어휘 (토큰) | `src/Calc/Lexer.fsl` |
| indentation 문법 | `src/Calc/LayoutFilter.fs` (렉서와 파서 사이) |
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
| `INTERP` | `$"`로 시작하는 보간 문자열. 렉서가 구멍의 식까지 파싱해서 (포맷 문자열, 식 목록)으로 넘김 (아래 "문자열 포맷") | `$"hi {name}"` |
| `IDENT` | `[a-z_][A-Za-z0-9_]*` (키워드와 `_` 하나는 제외). 변수와 타입 이름 | `x`, `foo_1`, `_tmp`, `fooBar` |
| `CTOR` | `[A-Z][A-Za-z0-9_]*`. 생성자 (타입 이름으로도 쓸 수 있음) | `Some`, `None`, `Node`, `X2` |
| `TYVAR` | `'` 다음에 `IDENT` 모양. type variable | `'a`, `'key` |
| `UNDERSCORE` | `_` (와일드카드 패턴, 쓰지 않는 인자) | |
| 키워드 | `let` `rec` `and` `in` `fun` `function` `if` `then` `else` `true` `false` `not` `match` `with` `when` `type` `of` `try` `exception` `while` `for` `to` `downto` `do` `done` `module` `struct` `end` `open` | |
| `EQUALS` | `=` | |
| `ARROW` | `->` | |
| `NOTEQUAL` `LESS` `GREATER` `LESSEQUAL` `GREATEREQUAL` | `<>` `<` `>` `<=` `>=` | |
| `AND` `OR` | `&&` `\|\|` | |
| `PLUS` `MINUS` `STAR` `SLASH` `PERCENT` | `+` `-` `*` `/` `%` | |
| `PIPE` `COMPOSE` | `\|>` `>>` | |
| `LPAREN` `RPAREN` | `(` `)` | |
| `LBRACKET` `RBRACKET` `SEMI` | `[` `]` `;` | |
| `SEMISEMI` | `;;` (최상위 항목 구분) | |
| `COMMA` | `,` | |
| `CONS` | `::` | |
| `COLON` | `:` (타입 표기) | |
| `COLONEQUALS` `BANG` | `:=` `!` (mutable reference) | |
| `LBRACE` `RBRACE` `DOT` | `{` `}` `.` (레코드) | |
| `LBRACKBAR` `BARRBRACK` `LARROW` | `[\|` `\|]` `<-` (배열). 가장 긴 일치라서 `x<-1`은 `x <- 1`이고, `x < -1`로 띄어 씁니다 | |
| `QIDENT` `QCTOR` | `(CTOR ".")+ IDENT`, `(CTOR ".")+ CTOR`. 모듈 이름으로 한정한 이름. 점 앞뒤에 공백이 없어야 함 | `M.f`, `M.N.g`, `M.t`, `M.Some` |
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
- **주석**: `//`부터 줄 끝까지(F#), `(*`부터 짝이 맞는 `*)`까지(OCaml, F#)는 공백처럼 건너뜁니다. 블록 주석은 중첩할 수 있습니다(`(* a (* b *) c *)`). 닫히지 않은 블록 주석은 여는 위치에서 `Unterminated comment` 오류를 냅니다.
- `()`는 토큰 하나가 아니라 `LPAREN`과 `RPAREN`입니다. `;;`는 `;` 두 개가 아니라 `SEMISEMI` 하나입니다(가장 긴 일치).

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

### 보간 문자열 `$"..."`

F#과 같은 보간 문자열입니다. 중괄호 안의 식을 계산해서 그 자리에 넣습니다.

```
let name = "Kim" in let age = 30 in $"Hello {name}, {age + 1} next year"   (* "Hello Kim, 31 next year" *)
$"{{literal}} %d{1 + 2} {[1; 2]} 100%"                                      (* "{literal} 3 [1; 2] 100%" *)
```

- **`{e}`:** 아무 타입의 식. `toString`과 같은 모양으로 넣습니다(문자열은 따옴표 없이, 나머지는 결과 출력과 같음).
- **`%d{e}`, `%s{e}`, `%b{e}`, `%A{e}`:** 지정자를 붙이면 식의 타입을 검사합니다. `$"%d{"x"}"`는 `Type mismatch: expected int but got string`입니다.
- **중괄호 글자:** `{{`와 `}}`. 짝이 없는 `}`는 `Unmatched } in interpolated string (use }} for a brace)` 오류입니다.
- **`%` 글자:** 지정자와 `{`가 뒤따르지 않으면 그냥 `%`입니다.
- **구멍 안:** 아무 식이나 쓸 수 있고 문자열 리터럴도 됩니다(`$"{"}"}"`). 빈 구멍 `{}`는 `Empty expression in interpolated string`입니다. 구멍 안의 오류는 원래 입력의 줄과 열을 가리킵니다.
- **뜻:** `$"a{x}b{y}"`는 `sprintf "a%Ab%A" x y`와 같습니다(아래 "문자열 포맷").

## 2. 구문

### EBNF

```ebnf
(* 프로그램: 최상위 항목들. 선언 뒤의 식은 ";;" 다음에만 (아래 "최상위 선언") *)
program = items EOF ;
items   = ε
        | ";;" items
        | seq [ ";;" items ]
        | decl afterDecl ;
afterDecl = ε | ";;" items | decl afterDecl ;
decl    = "let" IDENT [ params ] [ ":" type ] "=" seq
        | "let" letPat "=" seq
        | "let" "rec" recBinding { "and" recBinding }
        | "type" [ typeParams ] typeName "=" [ "|" ] ctorDecl { "|" ctorDecl }
        | "type" [ typeParams ] typeName "=" "{" fieldDecls "}"   (* 레코드 타입 *)
        | "exception" ctorDecl                           (* 예외 선언 *)
        | "module" CTOR "=" "struct" { decl | ";;" } "end"   (* 모듈 *)
        | "open" ( CTOR | QCTOR ) ;

seq     = expr [ ";" seq ] ;                             (* e1; e2: 순서 실행 *)

expr    = app
        | "let" IDENT [ ":" type ] "=" seq "in" seq
        | "let" letPat "=" seq "in" seq                  (* 튜플 분해, (), _ *)
        | "let" IDENT params [ ":" type ] "=" seq "in" seq   (* 함수 정의, 반환 타입 표기 *)
        | "let" "rec" recBinding { "and" recBinding } "in" seq   (* 재귀 함수 정의, and 로 mutual recursion *)
        | "function" [ "|" ] arm { "|" arm }             (* fun x -> match x with ... *)
        | "fun" params "->" seq                          (* 람다 *)
        | "if" expr "then" expr "else" expr
        | "match" expr "with" [ "|" ] arm { "|" arm }    (* 패턴 매칭 *)
        | "try" seq "with" [ "|" ] arm { "|" arm }       (* 예외 받기 *)
        | "exception" ctorDecl "in" seq                  (* 예외 선언 *)
        | "type" [ typeParams ] typeName "=" [ "|" ] ctorDecl { "|" ctorDecl } "in" seq
        | "type" [ typeParams ] typeName "=" "{" fieldDecls "}" "in" seq
        | "while" seq "do" seq "done"                    (* 반복문 *)
        | "for" IDENT "=" seq ( "to" | "downto" ) seq "do" seq "done"
        | expr binop expr
        | "-" expr
        | "not" expr ;

app     = app atom                                       (* 함수 호출 *)
        | atom ;

atom    = primary
        | "!" atom ;                                     (* mutable reference 읽기 *)

primary = NUMBER
        | STRING
        | INTERP                                         (* $"a{e}b" *)
        | "true" | "false"
        | IDENT
        | QIDENT | QCTOR                                 (* M.f, M.C *)
        | primary "." IDENT                              (* 필드 읽기 *)
        | primary "." "[" seq "]"                        (* 배열 원소 읽기 *)
        | "[|" [ expr { ";" expr } ] "|]"                (* 배열 *)
        | "{" fields "}"                                 (* 레코드 *)
        | "{" primary "with" fields "}"                  (* 필드 몇 개를 바꾼 새 레코드 *)
        | CTOR                                           (* 생성자 *)
        | "(" ")"                                        (* unit *)
        | "(" expr ")"
        | "(" expr ":" type ")"                          (* 타입 표기 *)
        | "(" expr ";" seq ")"                           (* 괄호 안의 순서 실행 *)
        | "(" expr "," expr { "," expr } ")"             (* 튜플 *)
        | "[" [ expr { ";" expr } ] "]" ;                (* 리스트 리터럴 *)

arm     = pattern [ "when" expr ] "->" seq ;

fields     = IDENT "=" expr { ";" IDENT "=" expr } [ ";" ] ;
fieldDecls = IDENT ":" type { ";" IDENT ":" type } [ ";" ] ;

recBinding = IDENT params [ ":" type ] "=" seq ;

pattern = consPat [ "|" pattern ] ;                      (* or 패턴: 가장 약함 *)
consPat = ctorPat [ "::" consPat ] ;                     (* :: 는 오른쪽 결합 *)
ctorPat = CTOR atomPat                                   (* Some x, Node (l, x, r) *)
        | atomPat ;
atomPat = IDENT | "_" | CTOR
        | NUMBER | "-" NUMBER | STRING | "true" | "false"
        | "[" "]" | "(" ")"
        | "[" pattern { ";" pattern } "]"                (* 리스트 패턴 *)
        | recordPat
        | "(" pattern ")"
        | tuplePat ;
tuplePat = "(" pattern "," pattern { "," pattern } ")" ;
recordPat = "{" IDENT "=" pattern { ";" IDENT "=" pattern } [ ";" ] "}" ;   (* 쓴 필드만 검사 *)
letPat  = tuplePat | recordPat | "(" ")" | "_" ;

params  = param { param } ;
param   = IDENT | "_" | "(" ")"                        (* () 는 unit 인자 *)
        | "(" ( IDENT | "_" ) ":" type ")" ;            (* 타입을 적은 인자 *)

typeParams = TYVAR | "(" TYVAR { "," TYVAR } ")" ;     (* 'a tree, ('k, 'v) map *)
typeName   = IDENT | CTOR ;
ctorDecl   = CTOR [ "of" type ] ;
type       = prodType [ "->" type ] ;                    (* -> 는 오른쪽 결합 *)
prodType   = appType { "*" appType } ;
appType    = atomType { IDENT }                          (* int list option *)
           | "(" type "," type { "," type } ")" IDENT { IDENT } ;   (* (int, string) pair *)
atomType   = IDENT | TYVAR | "(" type ")" ;

binop   = "+" | "-" | "*" | "/" | "%"
        | "|>" | ">>"
        | "=" | "<>" | "<" | ">" | "<=" | ">="
        | "::"
        | "&&" | "||"
        | ":="                                           (* mutable reference 쓰기 *)
        | "<-" ;                                         (* 배열 원소 쓰기. 왼쪽은 a.[i] *)
```

`expr binop expr` 부분은 그대로 두면 모호합니다. 아래의 우선순위와 associativity로 모호함을 해소합니다. 함수 호출은 우선순위 선언 대신 `app`/`atom` 계층으로 표현했습니다. `Parser.fsy`도 같은 방식으로 작성되어 있으며 shift/reduce 충돌이 없습니다.

### 우선순위와 associativity

위에 있을수록 우선순위가 **낮습니다**. `Parser.fsy`의 선언 순서와 같습니다.

| 우선순위 | 연산자 | associativity | 선언 |
|---|---|---|---|
| 0 (가장 낮음) | `;` (순서 실행) | 오른쪽 | `%nonassoc BELOWSEMI`, `%nonassoc SEMI` (`seq = expr [";" seq]` 규칙) |
| 1 | `let ... in`, `if ... then ... else`, `fun ... ->`, `match` 분기 `->` | — | `%nonassoc IN ELSE ARROW` |
| 2 | `:=`, `<-` | **오른쪽** | `%right COLONEQUALS LARROW` |
| 3 | `\|\|` | 왼쪽 | `%left OR` |
| 4 | `&&` | 왼쪽 | `%left AND` |
| 5 | `=` `<>` `<` `>` `<=` `>=`, `\|>`, `>>` | 왼쪽 | `%left EQUALS NOTEQUAL ... PIPE COMPOSE` |
| 6 | `::` | **오른쪽** | `%right CONS` |
| 7 | `+` `-` (이항) | 왼쪽 | `%left PLUS MINUS` |
| 8 | `*` `/` `%` | 왼쪽 | `%left STAR SLASH PERCENT` |
| 9 | `-` (단항), `not` | — | `%nonassoc UMINUS NOT` |
| 10 | 함수 호출 `f x` | 왼쪽 | 문법 계층 (`app = app atom`) |
| 11 | `!` (mutable reference 읽기) | — | 문법 계층 (`atom = "!" atom`) |
| 12 (가장 높음) | `.` (필드 읽기) | 왼쪽 | 문법 계층 (`primary = primary "." IDENT`) |

F#과 같은 순서입니다. 함수 호출은 `!`와 `.`를 뺀 어떤 연산자보다 먼저 묶입니다. `:=`, `!`, `.`의 자리는 OCaml과 같습니다.

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
| `r := !r + 1` | `r := ((!r) + 1)` |
| `f !r` | `f (!r)` |
| `!f x` | `(!f) x` |
| `a := b := c` | `a := (b := c)` |
| `a.[i] <- a.[i] + 1` | `a.[i] <- (a.[i] + 1)` |
| `f a.[i]` | `f (a.[i])` |
| `f p.x` | `f (p.x)` |
| `!p.r` | `!(p.r)` |
| `a.b.c` | `(a.b).c` |

> **F#과 다른 점:** F#은 `f -1`처럼 `-` 뒤에 공백이 없으면 함수 호출로 해석하는 특별 규칙이 있습니다. 이 언어는 공백을 구분하지 않으므로 `f -1`은 항상 뺄셈입니다.

### `%`, `|>`, `>>`, `function`, or 패턴, `let rec … and`

| 문법 | 뜻 | 예 |
|---|---|---|
| `a % b` | 나머지. 부호는 `a`를 따름(.NET, C와 같음). `b`가 0이면 `Attempted to divide by zero.`, `Int32.MinValue % -1`은 `Arithmetic operation resulted in an overflow.` | `-7 % 3` → `-1` |
| `x \|> f` | `f x`. `x`를 먼저 계산합니다(F#과 같음) | `[1; 2; 3] \|> map (fun x -> x * x) \|> sum` → `14` |
| `f >> g` | `fun x -> g (f x)` | `(inc >> dbl) 5` → `12` |
| `function` | 인자 하나를 받아 바로 `match`하는 함수 | `map (function Some x -> x \| None -> 0) xs` |
| `p1 \| p2` | or 패턴. 왼쪽부터 처음 맞는 쪽. 두 쪽은 같은 이름을 묶어야 합니다 | `\| 1 \| 2 \| 3 -> "small"`, `\| (x, "a") \| (x, "b") -> x` |
| `let rec f … and g …` | mutual recursion | `let rec even n = … odd (n - 1) and odd n = … even (n - 1)` |

- **우선순위:** `|>`와 `>>`는 비교 연산자와 같은 자리에서 왼쪽부터 묶입니다(F#, OCaml과 같음). `%`는 `*`, `/`와 같은 자리입니다.
- **`not`은 함수가 아닙니다.** 전위 연산자라서 `x |> not`은 쓸 수 없습니다. `x |> (fun b -> not b)`로 씁니다.
- **or 패턴의 범위:** 패턴에서 가장 약해서 `h :: t | []`는 `(h :: t) | []`입니다. 괄호 안에서도 쓸 수 있습니다(`Some (1 | 2)`).
- **or 패턴의 이름:** 두 쪽이 묶는 이름이 다르면 `Variable x must occur on both sides of this | pattern` 오류입니다(OCaml과 같음). 같은 이름은 두 쪽에서 타입이 같아야 합니다.
- **가드:** 왼쪽 패턴이 맞아 묶은 값으로 가드가 실패하면, 오른쪽 패턴을 다시 시도하지 않고 다음 분기로 갑니다(OCaml과 같음).
- **`let rec … and …`:** 그룹의 함수는 서로를 부를 수 있습니다. 타입 추론은 그룹 안에서 서로를 단형으로 보고, 그룹이 끝나면 함께 generalize합니다. 같은 이름을 두 번 정의하면 `Variable f is bound several times in this let rec` 오류입니다. 최상위 선언으로도 씁니다.
- **`and`:** 이제 키워드라서 변수 이름으로 쓸 수 없습니다.

### 최상위 선언, `;;`, `;`

프로그램은 최상위 항목을 차례로 씁니다. 0열에서 시작하는 새 줄이 새 항목입니다(아래 "indentation 문법").

```
type shape = Circle of int | Rect of int * int
let area s =
    match s with
    | Circle r -> 3 * r * r
    | Rect (w, h) -> w * h
let rec fact n = if n = 0 then 1 else n * fact (n - 1)

println (fact 5)
println (area (Rect (2, 3)))
area (Circle 1)
```

한 줄에 이어 쓰거나 OCaml처럼 쓸 때는 `;;`로 항목을 나눕니다(`let x = 1;; x + 1`).

- **선언:** `in` 없는 `let`, `let rec`, `let (a, b) =`, `let () =`, `let _ =`, `type`. 앞의 선언은 뒤의 모든 항목에서 보입니다. 파서가 `let … in (나머지 항목)` 사슬로 풀어 쓰므로, 의미는 `let … in`과 같습니다.
- **선언 뒤의 식:** 0열에서 시작하는 새 줄은 새 항목입니다(아래 "indentation 문법"). 한 줄에 이어 쓸 때는 `;;` 다음에 씁니다(`let x = 1;; x + 1`). 선언의 오른쪽 식은 다음 선언, `;;`, 0열의 새 줄까지 이어집니다. 그래서 `let x = f` 다음 줄의 `g`는 0열이면 새 식, 들여쓰면 `f g`(함수 호출)입니다.
- **결과:** 마지막 항목이 식이면 그 값, 선언이면 `()`입니다. 식 항목이 여럿이면 앞의 것은 순서대로 실행하고 버립니다(`e1;; e2`는 `e1; e2`와 같음). 빈 프로그램(주석만 있는 파일 포함)은 `()`입니다.
- **`;` (순서 실행):** `e1; e2`는 `e1`을 평가하고 버린 뒤 `e2`의 값이 됩니다.
  - **어디까지 이어지는지:** `let`의 본문과 오른쪽, `fun`의 본문, `match` 분기, 괄호 안, 최상위에서 오른쪽 끝까지 이어집니다. `let x = 1 in a; b`는 `let x = 1 in (a; b)`입니다.
  - **`if`:** `if`의 `else` 분기는 `;` 앞에서 끝납니다. `if c then a else b; d`는 `(if c then a else b); d`입니다(OCaml과 같음).
  - **리스트 안:** 리스트 리터럴 안의 `;`는 원소 구분자입니다. 순서 실행을 넣으려면 괄호로 감쌉니다(`[(a; b)]`).
  - **우선순위:** `;`는 모든 연산자보다 약합니다.

### indentation 문법

F#의 light syntax처럼 들여쓰기로 블록을 구분합니다. `in`, `;`, `;;`, `done`, `struct … end`를 쓴 코드(verbose syntax)도 그대로 받습니다.

```
type shape = Circle of int | Rect of int * int

let rec fact n =
    if n = 0 then 1
    else n * fact (n - 1)

let area shape =
    let pi = 3                       (* let pi = 3 in … *)
    match shape with
    | Circle r -> pi * r * r
    | Rect (w, h) -> w * h

println (fact 5)                     (* ;; 없이 새 항목 *)
println (area (Rect (2, 3)))
```

- **블록:** `let … =`, `->`(`fun`과 `match`/`function`/`try`의 분기), `then`, `else`, `try`, `do` 다음의 식이 블록입니다. 블록의 첫 토큰의 열이 블록의 열입니다.
- **같은 열의 새 줄은 구분자:**
  - 블록 안의 `let`(아직 `in`이 없는) 다음이면 `in`
  - 블록 안이면 `;`
  - 최상위(0열)와 모듈 안이면 `;;`
  - 괄호 안이면 `;`(리스트와 레코드에서는 원소 구분자, `(a⏎ b)`는 `(a; b)`)
  - 앞 줄의 마지막 토큰이 식을 끝낼 수 있고(이름, 리터럴, 닫는 괄호, `done`, `end`), 새 줄의 첫 토큰이 식을 시작할 수 있을 때만 넣습니다. 그래서 `… in`, `a +`, `let x =`로 끝나는 줄 다음은 이어집니다.
- **더 들여쓴 새 줄은 앞 줄을 잇습니다:** `f a` 다음 줄의 들여쓴 `b`는 인자입니다.
- **블록보다 왼쪽인 새 줄은 블록을 닫습니다(offside rule).** `while`/`for`의 `do` 블록이 닫히면 `done`을, `module M =` 다음의 들여쓴 선언이 끝나면 `end`를 넣습니다.
- **이어지는 줄:** infix 연산자(`+ - * / % = <> < > <= >= && || |> >> :: :=`), `|`, `then`, `else`, `with`, `in`, `and`, `->`, `do`, `,`, 닫는 괄호로 시작하는 줄은 앞 줄을 잇습니다. 줄 머리의 연산자는 블록의 열보다 "연산자 길이 + 1"만큼 왼쪽에 있어도 됩니다(F#과 같음).

  ```
  let total =
      xs
      |> map f
      |> sum
  ```

- **`match`의 분기:** `|`의 열이 분기의 주인을 정합니다. 바깥 `match`의 열에 쓴 `|`는 안쪽 `match`를 닫습니다(괄호가 필요 없음). `|`는 `match`가 있는 줄의 머리까지 왼쪽에 둘 수 있습니다(`let f x = match x with` 다음 줄의 `    | A -> …`).

  ```
  match a with
  | A ->
      match b with
      | X -> 1
      | Y -> 2
  | B -> 3                           (* 바깥 match 의 분기 *)
  ```

- **한 줄 안에서는 지금과 같습니다.** 구분자를 넣지 않으므로 한 줄로 쓴 `let … in`, `;`, 중첩 `match`의 뜻은 바뀌지 않습니다. 테스트가 기존 예제 전부를 indentation 문법을 켜고 끄고 파싱해서 AST가 같은지 확인합니다(`LayoutTests`).
- **오류:**
  - `Tabs are not allowed in indentation`: 들여쓰기에 탭을 쓰면 오류입니다(F#과 같음).
  - `The block following this let is unfinished`: 블록이 몸 없는 `let`으로 끝납니다. 위치는 그 `let`입니다.
- **구현:** `LayoutFilter`가 렉서와 파서 사이에서 F#의 `LexFilter`처럼 context 스택으로 가상 토큰을 넣습니다. 블록은 가상 괄호(`OBLOCKBEGIN … OBLOCKEND`)로 감싸고, 구분자는 문법의 `in`, `;`, `;;`, `done`, `struct`, `end`를 그대로 씁니다. 설계는 [indentation-design.md](indentation-design.md)에 있습니다.
- **F#과 다른 점:** F#의 모든 permitted undentation, `begin … end`, `elif`, `#indent "off"`는 없습니다. 괄호 안에서 블록보다 왼쪽에 쓴 줄은 오류가 아니라 이어지는 줄입니다.

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

### 타입 표기

식, 함수 인자, 반환 타입, `let` 값에 타입을 적을 수 있습니다(OCaml과 같은 문법). 타입 추론이 적은 타입과 추론한 타입을 unify합니다. 실행에는 영향이 없습니다.

```
(xs : int list)                                          (* 식: 괄호가 필요함 *)
fun (x: int) (y: string) -> y + toString x               (* 인자 *)
let f (x: int) : string = toString x in f 1              (* 인자와 반환 타입 *)
let rec fact (n: int) : int = if n = 0 then 1 else n * fact (n - 1) in fact 5
let names : string list = [] in names                    (* let 값 *)
type ('a, 'b) pair = P of 'a * 'b in (P (1, "a") : (int, string) pair)
```

- **타입 문법:** `type` 선언의 `of` 뒤와 같습니다. 타입 인자가 여럿이면 `(int, string) pair`처럼 괄호로 묶어 앞에 씁니다.
- **식 표기는 괄호 안에서만:** `(e : t)`. 괄호 없는 `e : t`는 parse error입니다(OCaml과 같음, F#과 다름).
- **맞지 않으면 타입 오류:** `("a" : int)`는 `Type mismatch: expected int but got string`이고, 위치는 표기한 식(`"a"`)을 가리킵니다. 반환 타입이 맞지 않으면 함수 본문을 가리킵니다.
- **타입 이름:** 알려진 타입이어야 합니다. `(1 : foo)`는 `Unknown type: foo`, `([] : list)`는 `Type list expects 1 type argument(s) but got 0`입니다. 위치는 타입을 가리킵니다.
- **type variable `'a`:** 표기에서는 "아직 모르는 타입"이라는 뜻입니다. polymorphic하라고 강제하지 않습니다(OCaml과 같음).
  - **범위:** 한 `let` 정의(값, 인자, 반환 타입, 안쪽 `let`) 안에서 같은 이름은 같은 타입입니다. `fun (x: 'a) (y: 'a) -> (x, y)`는 `'a -> 'a -> 'a * 'a`입니다. 안쪽 `let`은 바깥에서 쓴 `'a`를 같은 것으로 보고, 바깥에 없는 이름은 안쪽 정의의 것입니다.
  - **generalization:** 정의가 끝나면 보통 type variable처럼 generalize합니다. `let id (x: 'a) : 'a = x in (id 1, id "a")`는 `int * string`입니다.
  - **다른 타입으로 정해지면 경고:** `let f (x: 'a) = x + 1`은 `int -> int`가 되고 `Warning: the type variable 'a has been constrained to be type int`를 냅니다(F# FS0064와 같음. OCaml은 경고하지 않음). 아래 "6. 타입 추론"을 참고하세요.
- **풀어 쓰기:** 인자 표기 `fun (x: t) -> e`는 `fun x -> let x = (x : t) in e`, 반환 타입 표기 `let f x : t = e`는 `let f x = (e : t)`입니다. 컴파일러는 표기를 지우고 같은 코드를 만듭니다.

### 예외

OCaml과 같은 예외입니다. `raise`로 던지고, `try … with`로 받습니다.

```
exception Neg of int in
let check n = if n < 0 then raise (Neg n) else n in
(try check 5 with Neg n -> 0, try check (-3) with Neg n -> n)      (* (5, -3) *)

try head [] with Failure msg -> msg                                  (* "head: empty list" *)
let safeDiv a b = try Some (a / (if b = 0 then failwith "div" else b)) with Failure _ -> None
```

- **타입 `exn`:** 예외 값의 타입입니다. 예외는 `exn`의 생성자이고, 보통 값처럼 리스트에 넣거나 `=`로 비교하거나 `match`할 수 있습니다.
- **선언:** `exception E`, `exception E of t`. 최상위 선언이나 `exception … in 식`으로 씁니다. 인자 타입에 type variable은 쓸 수 없습니다(OCaml과 같음, `Unbound type variable: 'a`).
- **처음부터 있는 예외:** `Failure of string`, `Not_found`
- **`raise : exn -> 'a`:** 돌아오지 않으므로 결과는 아무 타입입니다. 내장 함수입니다.
- **`failwith : string -> 'a`:** `raise (Failure msg)`. prelude에 있습니다. `head`, `tail`, `nth`도 prelude에서 `failwith`로 실패합니다.
- **`try e with | p -> e | …`:** `e`를 계산하다가 예외가 raise되면, 위에서부터 패턴과 가드가 맞는 첫 분기의 값이 됩니다. 맞는 분기가 없으면 같은 예외를 다시 raise합니다. 그래서 빠진 분기 경고는 하지 않고, 쓰이지 않는 분기 경고는 합니다. `e`와 분기 본문은 같은 타입이어야 합니다. 본문은 `;`까지 이어집니다(`try a; b with …`).
- **잡히지 않은 예외:** `Failure msg`는 `Error: msg`, 나머지는 `Error: exception <값>`을 출력하고 종료 코드 1로 끝납니다(`Error: exception Not_found`).
- **실행 중 오류는 예외가 아닙니다:** 0으로 나누기, `Match failure`, `substr` 범위, 깊은 재귀는 `try`로 받을 수 없습니다.
- **OCaml과 다른 점:**
  - 같은 이름의 예외는 프로그램에 하나만 선언할 수 있습니다(`Exception E is already defined`). 실행 중에는 예외를 이름으로 구분하기 때문입니다. OCaml은 선언마다 다른 예외입니다.
  - `try`의 분기 본문은 tail position이 아닙니다.

### 레코드

이름이 붙은 필드의 묶음입니다. F#, OCaml과 같은 문법입니다.

```
type point = { x: int; y: int }
type 'a box = { value: 'a; label: string }

let p = { y = 2; x = 1 }               (* { x = 1; y = 2 } *)
let q = { p with x = 10 }              (* { x = 10; y = 2 }, p 는 그대로 *)
p.x + q.x                              (* 11 *)
```

- **선언:** `type t = { f1: t1; f2: t2 }`. 최상위 선언이나 `type … in 식`으로 씁니다. type parameter를 쓸 수 있고(`'a box`), 자기 자신을 필드 타입에 쓸 수 있습니다(`type n = { value: int; next: n option }`). 같은 필드 이름을 두 번 쓰면 `Duplicate field: x`입니다.
- **만들기:** `{ f1 = e1; f2 = e2 }`. 필드는 아무 순서로 쓸 수 있고, 값은 **쓴 순서대로** 계산합니다. 모든 필드가 있어야 합니다(`Missing record field: y`).
- **필드 읽기:** `e.f`. 함수 호출보다 강하게 묶입니다(`f p.x == f (p.x)`).
- **`{ e with f = v }`:** `e`를 먼저 계산하고, 적은 필드만 바꾼 **새** 레코드를 만듭니다. `e`는 바뀌지 않습니다. `e`는 이름, 괄호, 필드 읽기 같은 작은 식입니다(`{ (f x) with … }`).
- **타입은 필드 이름이 정함:** `fun r -> r.x`의 `r`은 필드 `x`를 선언한 레코드 타입입니다. 같은 필드 이름을 가진 레코드 타입이 여럿이면 **마지막에 선언한** 타입입니다(OCaml과 같음). 앞의 타입으로 쓰려면 타입 표기도 도와주지 않습니다(`expected q but got p`).
- **비교와 출력:** `=`는 필드를 차례로 비교합니다. `<`와 `sort`에는 쓸 수 없습니다. 결과는 `{ x = 1; y = 2 }`처럼 선언한 순서로 출력합니다.
- **필드는 바꿀 수 없음:** 바꿔야 하면 필드에 `ref`를 둡니다(`type t = { r: int ref }`, `a.r := 5`).
- **패턴:** `{ x = p1; y = p2 }`. 쓴 필드만 검사하고, 쓰지 않은 필드는 무엇이든 맞습니다(F#과 같음). `match`, `function`, `try`, `let`의 왼쪽(`let { x = a; y = b } = p`), 생성자의 인자(`Some { v = x }`)에 씁니다. 타입은 레코드 식처럼 필드 이름이 정합니다.

  ```
  let describe p =
      match p with
      | { x = 0; y = 0 } -> "origin"
      | { x = 0 } -> "on y axis"
      | { x = a; y = b } when a = b -> "diagonal"
      | _ -> "somewhere"
  ```

  - 빠진 분기 검사는 레코드 타입을 생성자가 하나인 타입으로 봅니다. `match r with { y = true } -> 1`(`y : bool`)는 `{ x = _; y = false }`가 빠졌다고 경고합니다.
- **아직 없는 것:** 필드 이름만 쓰는 줄임(`{ x; y }`, OCaml의 패턴과 식), `mutable` 필드, F#처럼 타입 표기로 같은 이름의 필드를 고르는 것.

### 모듈

OCaml과 같은 문법의 모듈입니다. 최상위에서 선언합니다.

```
module Stack =
    type 'a t = Empty | Push of 'a * 'a t
    exception EmptyStack
    let empty = Empty
    let push x s = Push (x, s)
    let pop s =
        match s with
        | Empty -> raise EmptyStack
        | Push (x, rest) -> (x, rest)

let s = Stack.push 1 Stack.empty
(fst (Stack.pop s), (fun (t: int Stack.t) -> t) s)      (* (1, Stack.Push (1, Stack.Empty)) *)
```

`module Stack = struct … end`로 써도 같습니다.

- **선언:** `module M = struct … end`, 또는 `module M =` 다음 줄부터 들여쓴 선언(indentation 문법). 안에는 최상위와 같은 선언(`let`, `let rec … and`, `type`, 레코드 타입, `exception`, `module`, `open`)만 쓸 수 있습니다. 식을 실행하려면 `let _ = e`로 씁니다. 모듈 이름은 대문자로 시작합니다.
- **밖에서 쓰기:** `M.f`(값), `M.C`(생성자, 패턴에서도), `M.t`(타입 표기). 모듈 안의 모듈은 `M.N.g`입니다. 점 앞뒤에 공백을 둘 수 없습니다(`M . f`는 생성자 `M`의 필드 읽기).
- **안에서 쓰기:** 모듈 안에서는 앞에서 선언한 이름을 `M.` 없이 씁니다. 모듈 안의 모듈 `N`도 `N.g`로 씁니다.
- **`open M`:** 그 뒤에서 `M`의 이름을 `M.` 없이 씁니다. 뒤에서 같은 이름을 다시 선언하면 그쪽이 가립니다. 모듈 안의 `open`은 그 모듈이 끝날 때까지만입니다. 선언하지 않은 모듈은 `Unknown module: M`입니다.
- **가리기:** 모듈 밖의 이름은 모듈의 이름과 섞이지 않습니다. `module M = struct let x = 10 end let x = 1`의 두 `x`는 다른 값입니다. 같은 이름의 모듈을 다시 선언하면 뒤의 것이 가립니다.
- **출력:** 생성자 값과 타입은 전체 이름으로 출력합니다(`Stack.Empty`, `int Stack.t`). REPL은 선언마다 `val M.f : int -> int = <fun>`을 보여 줍니다.
- **레코드 필드:** 필드 이름은 모듈 이름을 붙이지 않습니다. 모듈 안에서 선언한 레코드도 밖에서 `p.x`, `{ x = 1 }`로 씁니다(OCaml은 `p.M.x`).
- **구현:** 파서 직후 `Modules.resolve`가 모듈을 펼칩니다.
  - 모듈 안의 선언은 `M.f`, `M.t`, `M.C` 같은 이름의 최상위 선언이 되고, 모듈 안의 짧은 이름과 `open`한 이름은 전체 이름으로 바뀝니다.
  - 그래서 타입 추론, 평가기, 컴파일러는 모듈을 모릅니다. 점이 들어간 이름은 렉서가 `QIDENT`, `QCTOR`로만 만들므로 사용자가 쓴 다른 이름과 겹치지 않습니다.
  - 바꾼 노드에는 원래 노드의 위치를 옮겨서 오류와 경고가 원래 자리를 가리킵니다.
- **아직 없는 것:** 시그니처(`sig … end`, `module M : S`), functor, `let module M = … in`, `include`, 파일마다 모듈.

### 배열

고정 길이이고 원소를 바꿀 수 있는 배열입니다. F#과 같은 문법입니다.

```
let a = [| 3; 1; 2 |]
a.[0] <- 10
println (a, Array.length a, a.[1] + a.[2])      (* ([|10; 1; 2|], 3, 3) *)

let squares = Array.init 5 (fun i -> i * i)     (* [|0; 1; 4; 9; 16|] *)
let grid = Array.create 3 0
for i = 0 to 2 do
    grid.[i] <- i * 10
```

- **리터럴:** `[| e1; e2 |]`, 빈 배열 `[||]`. 여러 줄로 쓰면 줄바꿈이 원소 구분자입니다(indentation 문법).
- **원소:** `a.[i]`는 i번째 원소(0부터), `a.[i] <- v`는 원소를 바꾸고 `()`입니다. `.[`는 함수 호출보다 강하게 묶이고(`f a.[i]`), `<-`는 `:=`와 같은 자리입니다. `<-`의 왼쪽은 `a.[i]`만 됩니다(`The left side of <- must be an array element a.[i]`).
- **범위 밖:** `Index was outside the bounds of the array.`로 끝납니다(.NET과 같은 문장). 0으로 나누기처럼 `try`로 받을 수 없는 실행 중 오류입니다.
- **같은 배열:** `let b = a in b.[0] <- 5`는 `a`도 바꿉니다. 복사는 `Array.copy`입니다.
- **`=`:** 길이가 같고 원소가 차례로 같으면 `true`입니다(F#, OCaml과 같음). `<`와 `sort`에는 쓸 수 없습니다.
- **출력:** `[|1; 2|]`(F#과 같음)
- **타입:** `'a array`. value restriction이 있으므로 `let a = [||]`의 원소 타입은 처음 쓴 곳이 정합니다.
- **함수:** F#의 `Array` 모듈과 같은 이름입니다.
  - 내장 함수: `Array.length`, `Array.create n v`, `Array.ofList`, `Array.toList`
  - prelude의 `module Array`: `Array.init n f`, `Array.map`, `Array.iter`, `Array.fold`, `Array.copy`
- **아직 없는 것:** 배열 패턴(`[| a; b |]`), `open Array`(prelude의 모듈은 프로그램의 `open`이 모름), 슬라이스(`a.[1..2]`), F# 6의 `a[i]`
- **오류 메시지:** 리터럴 원소의 타입이 다르면 리스트처럼 보입니다(`[| 1; "a" |]`는 `expected int list but got string list`). 리터럴은 내장 함수 `[||]`에 리스트를 준 것이기 때문입니다.

### mutable reference와 반복문

OCaml, F#과 같은 mutable reference와 반복문입니다.

```
let total = ref 0
for i = 1 to 10 do
    total := !total + i
println !total                                           (* 55 *)

let counter () =
    let n = ref 0
    fun () -> incr n; !n
let next = counter ()
println (next (), next ())                               (* (1, 2) *)

let n = ref 10
while !n > 0 do
    n := !n - 3
!n                                                       (* -2 *)
```

한 줄로 쓰면 `for i = 1 to 10 do total := !total + i done`처럼 `done`으로 닫습니다.

- **`ref v : 'a ref`:** 내용이 `v`인 새 칸입니다. 내장 함수이므로 보통 이름처럼 가릴 수 있습니다(`let ref x = x in …`).
- **`!r : 'a`:** 지금 내용입니다. 함수 호출보다 강하게 묶입니다(`f !r == f (!r)`).
- **`r := v : unit`:** 내용을 `v`로 바꿉니다. 이항 연산자 가운데 가장 약하고 오른쪽 결합입니다(`r := !r + 1`). `r`을 먼저 계산합니다.
- **`incr r`, `decr r`:** `r := !r + 1`, `r := !r - 1`. prelude에 있습니다(`int ref -> unit`).
- **같은 칸:** `let b = a in b := 2`는 `a`의 내용도 바꿉니다. 칸은 복사하지 않습니다.
- **`=`:** 내용을 비교합니다(OCaml, F#과 같음). `ref 1 = ref 1`은 `true`입니다. 같은 칸인지 비교하는 연산자는 없습니다. `<`와 `sort`에는 쓸 수 없습니다.
- **출력:** `ref 1`, `ref [1; 2]`, `ref (Some 1)`처럼 다시 입력할 수 있는 모양입니다.
- **패턴:** ref를 분해하는 패턴은 없습니다. `!r`로 꺼냅니다.
- **`while c do e done : unit`:** `c`가 `true`인 동안 `e`를 되풀이합니다.
- **`for i = a to b do e done : unit`:** `i`를 `a`부터 `b`까지 1씩 늘리며 `e`를 계산합니다. `downto`는 1씩 줄입니다. `a > b`(`downto`면 `a < b`)면 한 번도 계산하지 않습니다.
  - `a`와 `b`는 `int`이고, 반복 전에 `a`, `b` 순서로 한 번씩 계산합니다(OCaml과 같음). 본문에서 `b`에 쓴 ref를 바꿔도 반복 횟수는 그대로입니다.
  - `i`는 본문 안에서만 보이는 보통 이름입니다(바꿀 수 없음).
  - `b`가 `int`의 최댓값이어도 끝납니다. 마지막 반복 뒤에는 `i + 1`을 계산하지 않습니다.
- **본문의 타입:** `unit`이 아니면 `e1; e2`와 같은 버리는 값 경고를 합니다(F#과 같음).
- **구현:** 파서가 반복문을 tail recursion으로 바꿉니다. 그래서 인터프리터와 컴파일러 모두 반복 횟수와 상관없이 스택이 자라지 않습니다.

  ```
  while c do e done  ==  let rec $while $u = if c then (e; $while ()) else () in $while ()
  for i = a to b do e done
    ==  let $start = (a : int) in let $stop = (b : int) in
        let rec $for i = e; if i < $stop then $for (i + 1) else () in
        if $start <= $stop then $for $start else ()
  ```

- **OCaml, F#과 다른 점:** `break`, `continue`는 없습니다. F#의 `let mutable`과 `<-`, OCaml의 `{ contents = … }` 패턴과 `r.contents`도 없습니다.

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
| `()` | `Unit` |
| `e1; e2` | `Seq (e1, e2)` |
| 최상위 `let x = e` (뒤 항목들 `rest`) | `Let ("x", e, rest)`. 끝이 선언이면 `rest`는 `Unit` |
| `fun () -> e` | `Lambda ("()", Match (Var "()", [ (PUnit, None, e) ]))` (인자가 `()`인지 확인) |
| `a % b` | `Modulo (a, b)` |
| `x \|> f` | `Let ("$pipe", x, App (f, Var "$pipe"))` (F#처럼 `x`를 먼저 계산) |
| `f >> g` | `Let ("$f", f, Let ("$g", g, Lambda ("$x", App (Var "$g", App (Var "$f", Var "$x")))))` |
| `function p -> e \| ...` | `Lambda ("$function", Match (Var "$function", [ ... ]))` |
| 패턴 `p1 \| p2` | `POr (p1, p2)` |
| `let rec f x = a and g y = b in e` | `LetRecs ([ ("f", "x", a); ("g", "y", b) ], e)` (함수가 하나면 `LetRec`) |
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
| `try e with p -> e1` | `Try (e, [ (p, None, e1) ])` |
| `exception E of t in e` | `ExceptionDecl ("E", Some t, e)` |
| `$"a{x}"` | `App (Printf ("sprintf", "a%A"), Var "x")` (`sprintf "…"`는 파서에서 보통 `App`) |
| `!r` / `r := v` | `App (Var "!", r)` / `App (App (Var ":=", r), v)` (사용자가 쓸 수 없는 이름의 내장 함수) |
| `[\| a; b \|]` / `a.[i]` / `a.[i] <- v` | `App (Var "[\|\|]", [a; b])` / `App (App (Var ".[]", a), i)` / `App (App (App (Var ".[]<-", a), i), v)` (내장 함수) |
| `type 'a p = { x: int; y: 'a } in e` | `RecordDecl ("p", ["a"], [ ("x", TName ("int", [])); ("y", TVar "a") ], e)` |
| `{ y = 1; x = 2 }` | `Record [ ("y", Number 1); ("x", Number 2) ]` (쓴 순서) |
| `e.x` | `Field (e, "x")` |
| `module M = struct let f x = x end;; M.f 1` | `Let ("M.f", Lambda ("x", Var "x"), App (Var "M.f", Number 1))` (`Modules.resolve`가 펼침, `open`은 AST에 남지 않음) |
| `{ e with x = 1 }` | `RecordUpdate (e, [ ("x", Number 1) ])` |
| `while c do e done` | `LetRec ("$while", "$u", If (c, Seq (e, App (Var "$while", Unit)), Unit), App (Var "$while", Unit))` |
| `for i = a to b do e done` | `Let ("$start", Annot (a, int), Let ("$stop", Annot (b, int), LetRec ("$for", "i", …)))` (위 "mutable reference와 반복문") |
| `let (a, b) = e1 in e2` | `LetPattern (PTuple [PVar "a"; PVar "b"], e1, e2)` |
| 패턴 `x` / `_` / `1` / `true` / `"s"` | `PVar "x"` / `PWildcard` / `PInt 1` / `PBool true` / `PString "s"` |
| 패턴 `[]` / `p1 :: p2` / `[p1; p2]` | `PNil` / `PCons (p1, p2)` / `PCons (p1, PCons (p2, PNil))` |
| 패턴 `(p1, p2)` | `PTuple [p1; p2]` |
| 패턴 `{ y = p; x = q }` | `PRecord [ ("y", p); ("x", q) ]` (쓴 순서, 쓴 필드만) |
| `None` / `Some e` | `Ctor "None"` / `App (Ctor "Some", e)` |
| 패턴 `None` / `Some p` | `PCtor ("None", None)` / `PCtor ("Some", Some p)` |
| `type 'a t = A \| B of 'a * int in e` | `TypeDecl ("t", ["a"], [ ("A", None); ("B", Some (TTuple [TVar "a"; TName ("int", [])])) ], e)` |
| 타입 `int list` / `'a` / `a * b` / `a -> b` | `TName ("list", [TName ("int", [])])` / `TVar "a"` / `TTuple [a; b]` / `TFun (a, b)` |
| `(e : t)` | `Annot (e, t)` |
| `fun (x: t) -> e` | `Lambda ("x", Let ("x", Annot (Var "x", t), e))` |
| `let f x : t = e1 in e2` | `Let ("f", Lambda ("x", Annot (e1, t)), e2)` |
| `let x : t = e1 in e2` | `Let ("x", Annot (e1, t), e2)` |
| 타입 `(int, string) pair` | `TName ("pair", [TName ("int", []); TName ("string", [])])` |
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
    | VUnit                                               // ()
    | VRef of Value ref                                   // ref v (:= 가 내용을 바꿈)
    | VArray of Value[]                                   // [| 1; 2 |] (a.[i] <- v 가 원소를 바꿈)
    | VRecord of typeName * fields                        // { x = 1; y = 2 } (선언한 순서)
```

평가는 환경 `env : Map<string, Value>`에서 이루어집니다. 처음 환경에는 내장 함수만 들어 있습니다(아래 "내장 함수" 참고). 결과는 `formatValue`로 출력합니다.
- 정수는 `3`, `-3`처럼 출력됩니다.
- 불리언은 `true`, `false`로 출력됩니다.
- 문자열은 따옴표와 이스케이프를 붙인 리터럴 형태로 출력됩니다. 예: `"say \"hi\"\n"`. 출력을 그대로 다시 입력하면 같은 값이 됩니다.
- 함수는 `<fun>`으로 출력됩니다. 내장 함수도 같습니다.
- `()`는 `()`로 출력됩니다. `calc prog.calc`와 컴파일한 실행 파일은 결과가 `()`면 아무것도 출력하지 않습니다.
- 리스트는 `[1; 2; 3]`, `[]`, `[[true]; []]`처럼 출력됩니다.
- 튜플은 `(1, "a", [true])`처럼 출력됩니다.
- 생성자 값은 `None`, `Some 1`, `Node (Leaf, 1, Leaf)`처럼 출력됩니다. 다시 입력해도 같은 값이 되도록 인자가 있는 생성자 값과 음수는 괄호로 감쌉니다(`Some (Some 1)`, `Some (-1)`).
- mutable reference는 `ref 1`, `ref (Some 1)`, `ref (ref 1)`처럼 생성자와 같은 규칙으로 출력됩니다.
- 배열은 `[|1; 2|]`처럼 출력됩니다.
- 레코드는 `{ x = 1; y = "a" }`처럼 선언한 순서로 출력됩니다. 생성자의 인자여도 괄호를 붙이지 않습니다(`Some { x = 1 }`).

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
- **tail call은 스택을 쓰지 않습니다.** 함수 본문의 마지막 계산이 호출이면(`if`, `match`, `let`, `;`의 마지막 식 포함), 그 호출은 호출한 함수의 스택 프레임을 다시 씁니다. `let rec go n acc = if n = 0 then acc else go (n - 1) (acc + 1)`은 깊이에 상관없이 돌고, `let rec f … and g …`처럼 서로 부르는 함수와 함수 인자로 받은 함수의 호출도 같습니다(Scheme, OCaml, F#과 같음).
  - 인터프리터는 .NET의 tail call(`<Tailcalls>true</Tailcalls>`), 컴파일러는 LLVM의 `tailcc` calling convention과 `tail` 호출을 씁니다.
  - `1 + f x`, `f x :: rest`처럼 호출 뒤에 할 일이 남은 호출은 tail call이 아닙니다. 이런 재귀의 깊이는 스택 크기(512MB)가 정합니다.

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

함수는 두 종류로 구현되어 있지만, 쓰는 쪽에서는 구분되지 않습니다.

- **포맷 함수(3개):** `sprintf`, `printf`, `printfn`. 포맷 문자열 리터럴이 함수의 타입을 정합니다. 아래 "문자열 포맷"을 참고하세요.
- **prelude(Calc로 쓴 함수, 27개):** `failwith`, `fst`, `snd`, `ignore`, `isEmpty`, `head`, `tail`, `nth`, `fold`, `reverse`, `append`, `range`, `sum`, `map`, `filter`, `exists`, `forall`, `incr`, `decr`, `tryHead`, `tryFind`, `defaultValue`, `Array.init`, `Array.copy`, `Array.iter`, `Array.fold`, `Array.map`. 아래 "prelude"를 참고하세요.
- **내장 함수(F#과 C로 구현, 18개):** 나머지. 문자열 함수, `toString`, `toInt`, 여러 타입을 받는 `length`, `sort`, 출력 함수, `raise`입니다.
- **mutable reference(3개):** `ref`, `!`, `:=`. 인터프리터는 F#으로, 컴파일러는 C 런타임 함수 없이 블록 하나를 읽고 쓰는 코드로 만듭니다. `!`와 `:=`는 연산자로만 씁니다.
- **배열(7개):** `[||]`(리터럴), `.[]`, `.[]<-`, `Array.length`, `Array.create`, `Array.ofList`, `Array.toList`. 앞의 세 개는 문법(`[| … |]`, `a.[i]`, `a.[i] <- v`)으로만 씁니다.

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

#### 출력

| 함수 | 타입 | 예 | 출력 / 결과 |
|---|---|---|---|
| `print v` | 값 → unit | `print "a"; print 1` | `a1` (줄바꿈 없음) |
| `println v` | 값 → unit | `println [Some "b"]` | `[Some "b"]` 다음 줄바꿈 |
| `ignore v` | 값 → unit | `ignore (f x)` | `()` (값을 버림) |

#### 문자열 포맷

| 함수 | 타입 | 예 | 결과 |
|---|---|---|---|
| `sprintf fmt …` | 포맷이 정함 | `sprintf "%d is %s" 42 "answer"` | `"42 is answer"` |
| `printf fmt …` | 포맷이 정함, 결과는 unit | `printf "%d-" 1` | `1-` 출력 (줄바꿈 없음) |
| `printfn fmt …` | 포맷이 정함, 결과는 unit | `printfn "x=%A" [1]` | `x=[1]` 출력 후 줄바꿈 |

| 지정자 | 인자 타입 | 넣는 모양 |
|---|---|---|
| `%d` | `int` | `42`, `-1` |
| `%s` | `string` | 따옴표 없이 그대로 |
| `%b` | `bool` | `true`, `false` |
| `%A` | 아무 타입 | `toString`과 같음: 문자열은 따옴표 없이, 나머지는 결과 출력과 같음 (`[Some "a"]`) |
| `%%` | (인자 없음) | `%` 글자 |

- **포맷 문자열이 타입을 정함:** `sprintf "%d is %s"`의 타입은 `int -> string -> string`, `printfn "hi"`는 `unit`입니다. 인자가 다 모이면 문자열을 만들거나 출력합니다. `map (sprintf "<%d>") [1; 2]`처럼 partial application을 할 수 있습니다.
- **리터럴이어야 함:** 포맷 문자열은 함수 바로 뒤의 문자열 리터럴이어야 합니다(F#과 같음). `sprintf`만 쓰거나 변수에 담으면 `The format string of sprintf must be a string literal`입니다.
- **오류:** 모르는 지정자는 `Unknown format specifier: %q`, 끝의 `%` 하나는 `Missing format specifier after %`입니다. 위치는 포맷 문자열을 가리킵니다. 인자 타입이 맞지 않으면 그 인자를 가리킵니다.
- **가리기:** `let sprintf x = …`처럼 같은 이름을 정의하면 보통 함수입니다.
- **F#과 다른 점:** `%A`는 문자열에 따옴표를 붙이지 않습니다(F#은 `"a"`). 폭, 정렬, `%f` 같은 지정자는 아직 없습니다.

#### 배열

| 함수 | 타입 | 예 | 결과 |
|---|---|---|---|
| `Array.length a` | array → int | `Array.length [\| 1; 2 \|]` | `2` |
| `Array.create n v` | int → 값 → array | `Array.create 2 0` | `[\|0; 0\|]` (`n`이 음수면 오류) |
| `Array.init n f` | int → 함수 → array | `Array.init 3 (fun i -> i * i)` | `[\|0; 1; 4\|]` |
| `Array.ofList xs`, `Array.toList a` | list → array, array → list | `Array.toList [\| 1 \|]` | `[1]` |
| `Array.map f a` | 함수 → array → array | `Array.map (fun x -> x + 1) [\| 1 \|]` | `[\|2\|]` |
| `Array.iter f a` | 함수 → array → unit | `Array.iter println a` | 원소마다 `f` |
| `Array.fold f acc a` | 함수 → 초기값 → array → 값 | `Array.fold (fun s x -> s + x) 0 a` | 합 |
| `Array.copy a` | array → array | `Array.copy a` | 새 배열 |

#### mutable reference

| 함수 | 타입 | 예 | 결과 |
|---|---|---|---|
| `ref v` | 값 → ref | `ref 0` | 내용이 `0`인 새 칸 |
| `!r` | ref → 값 | `!(ref 3)` | `3` |
| `r := v` | ref → 값 → unit | `r := 5` | `()` (내용을 바꿈) |
| `incr r`, `decr r` | int ref → unit | `incr c` | `c := !c + 1` |

#### 예외

| 함수 | 타입 | 예 | 결과 |
|---|---|---|---|
| `raise e` | exn → 아무 타입 | `raise Not_found` | 예외를 던짐 |
| `failwith msg` | string → 아무 타입 | `failwith "bad"` | `raise (Failure "bad")` |

- 문자열은 따옴표 없이 그대로, 나머지 값은 결과와 같은 모양(`formatValue`)으로 출력합니다. `toString`과 같은 규칙입니다.
- 출력은 표준 출력으로, 호출한 순서대로 나갑니다. 실행 중 오류가 나면 그때까지의 출력은 남습니다.

#### prelude (Calc로 쓴 표준 라이브러리)

위 표의 함수 중 27개는 [`src/Calc/prelude.calc`](../src/Calc/prelude.calc)에 Calc 코드로 있습니다. 인터프리터, 타입 추론, 컴파일러가 모두 이 정의 하나를 씁니다.

```
let rec fold f acc xs =
    match xs with
    | [] -> acc
    | x :: rest -> fold f (f acc x) rest

let reverse xs = fold (fun acc x -> x :: acc) [] xs
let map f xs = reverse (fold (fun acc x -> f x :: acc) [] xs)
let sum (xs: int list) : int = fold (fun acc x -> acc + x) 0 xs
```

- **붙이는 방법:** 프로그램을 파싱한 뒤, 프로그램이 쓰는 prelude 정의만 `let … in` 사슬로 앞에 붙입니다(`Prelude.wrap`). `map`을 쓰면 `map`이 쓰는 `fold`와 `reverse`도 붙습니다. 쓰지 않는 정의는 타입 추론, 평가, 컴파일 비용만 늘리므로 붙이지 않습니다. REPL은 처음부터 모두 붙입니다.
- **가리기:** 프로그램에서 같은 이름을 정의하면 그쪽이 가립니다. prelude 안의 정의는 바뀌지 않습니다. `let reverse xs = xs in map f ys`의 `map`은 prelude의 `reverse`를 씁니다.
- **긴 리스트:** 모두 tail recursion이라 `sum (range 1 1000000)`도 스택이 자라지 않습니다. `map`과 `filter`는 거꾸로 모은 뒤 `reverse`합니다. 함수는 앞 원소부터 호출합니다.
- **타입:** prelude의 타입은 추론한 것입니다. `sum`처럼 `+`의 타입을 정해야 하는 곳에는 타입 표기를 씁니다. 타입은 예전의 내장 함수와 같습니다.
- **`--no-typecheck`의 오류:** 타입이 맞지 않게 쓰면 prelude 안의 `match`나 연산이 실패합니다. `fst 1`은 `Match failure: 1`, `range 1 true`는 `Type error: expected int or string but got bool`입니다.
- **실패하는 함수:** `head`, `tail`, `nth`는 `failwith`로 `Failure`를 raise합니다. 메시지는 내장 함수였을 때와 같고(`head: empty list`), 이제 `try`로 받을 수 있습니다.
- **내장 함수로 남은 것:** `length`와 `sort`는 여러 타입을 받는데, Calc 함수로 쓰면 allowed list가 있는 type variable이 generalize되지 않아(첫 사용이 타입을 정함) 같은 타입이 될 수 없습니다.
- **성능:** 인터프리터는 F#의 `List` 함수 대신 Calc 코드를 해석하므로 긴 리스트에서 느립니다. 100만 개 리스트에 `range`, `map`, `filter`, `sum`, `exists`를 쓰는 프로그램이 0.77초에서 5.0초가 되었습니다(Release). 컴파일한 실행 파일은 0.04초에서 0.23초가 되었습니다.

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
| 평가 | 빈 리스트에 `head`/`tail` (`Failure`, `try`로 받을 수 있음) | `head: empty list`, `tail: empty list` |
| 평가 | `nth`의 위치가 리스트 밖 (`Failure`) | `nth: index out of range (length 1, index 1)` |
| 평가 | 잡히지 않은 예외 | `Failure msg`는 `msg`, 나머지는 `exception <값>` (`exception Not_found`) |
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
| `int`, `bool`, `string`, `unit` | `1`, `true`, `"a"`, `()` |
| `t list` | `[1; 2]`: `int list` |
| `t1 * t2 * ...` | `(1, "a")`: `int * string` |
| `t1 -> t2` | `fun x -> x + 1`: `int -> int` |
| `t option` | `Some 1`: `int option` |
| `t ref` | `ref 1`: `int ref` |
| `t array` | `[\| 1 \|]`: `int array` |
| 레코드 타입 | `{ x = 1; y = 2 }`: `point` |
| `exn` | `Not_found`, `Failure "x"`: `exn` |
| 사용자 타입 | `Node (Leaf, 1, Leaf)`: `int tree` |
| type variable | `'a`, `'b`, ... |

출력할 때 `->`는 right-associative고, `*`가 `->`보다, type argument(`int list`)가 `*`보다 강합니다. 필요한 곳에만 괄호를 씁니다. 예: `('a -> 'b) -> 'a list -> 'b list`, `(int * int) list`

### 규칙

- **let-polymorphism**: `let`으로 묶은 값의 타입은 generalization됩니다. `let id x = x in (id 1, id true)`는 `int * bool`입니다.
  - generalization은 `let`, `let rec`, `let (a, b) = ...`에서만 합니다. 함수 인자는 monomorphic입니다. `fun f -> (f 1, f true)`는 오류입니다.
- **value restriction:** `let`의 값이 계산 없이 정해지는 식(non-expansive)일 때만 generalize합니다. 함수(`fun`, `let f x = …`), 리터럴, 변수, 생성자와 그 인자(`Some []`), 튜플과 리스트, 그런 값만 쓴 `let … in`이 여기에 들어갑니다. 함수 호출이 들어간 식은 generalize하지 않습니다(SML과 같은 규칙).
  - 이유: `let r = ref [] in r := [1]; r := ["a"]`에서 `r : 'a list ref`를 generalize하면 서로 다른 타입의 리스트를 넣고 꺼낼 수 있습니다(soundness가 깨짐). 이 프로그램은 `expected int list but got string list`입니다.
  - generalize하지 않은 type variable은 처음 쓴 곳이 타입을 정합니다(OCaml의 `'_weak1`). REPL은 이것을 `'_a`로 보여 줍니다(`val r : '_a list ref = ref []`).
  - partial application의 결과도 generalize하지 않습니다. `let f = id id in (f 1, f "a")`는 오류입니다(OCaml, F#과 같음). `let f x = id id x`처럼 인자를 적으면 됩니다.
  - 함수 본문 안의 `ref`는 부를 때마다 새 칸이므로 `let mk () = ref [] in …`의 `mk : unit -> 'a list ref`는 generalize합니다.
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
| `print`, `println`, `ignore` | `'a -> unit` |
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
| `ref`, `!`, `:=` | `'a -> 'a ref`, `'a ref -> 'a`, `'a ref -> 'a -> unit` |
| `a.[i]`, `a.[i] <- v` | `'a array -> int -> 'a`, `'a array -> int -> 'a -> unit` |
| `Array.length`, `Array.create`, `Array.init` | `'a array -> int`, `int -> 'a -> 'a array`, `int -> (int -> 'a) -> 'a array` |
| `Array.ofList`, `Array.toList`, `Array.copy` | `'a list -> 'a array`, `'a array -> 'a list`, `'a array -> 'a array` |
| `Array.map`, `Array.iter`, `Array.fold` | `('a -> 'b) -> 'a array -> 'b array`, `('a -> unit) -> 'a array -> unit`, `('b -> 'a -> 'b) -> 'b -> 'a array -> 'b` |
| `incr`, `decr` | `int ref -> unit` |
| `raise`, `failwith` | `exn -> 'a`, `string -> 'a` |
| `sprintf`, `printf`, `printfn` | 포맷 문자열이 정함 (`sprintf "%d %s"`: `int -> string -> string`) |
| `Failure`, `Not_found` | `string -> exn`, `exn` |

### 타입 오류

| 원인 | 메시지 |
|---|---|
| 타입이 다름 | `Type mismatch: expected <기대 타입> but got <실제 타입>` (예: `[1; true]`는 `expected int list but got bool list`) |
| allowed list에 없는 타입 | `Type mismatch: expected int or string but got bool` |
| 함수가 아닌 값을 호출 | `Type mismatch: expected function but got int` |
| 자기 자신을 포함하는 타입 (`fun x -> x x`) | `Type mismatch: cannot construct infinite type 'a = 'a -> 'b` |
| 정의되지 않은 변수, 생성자 | `Unbound variable: x`, `Unknown constructor: X` |
| 생성자 패턴의 인자 유무 | `Constructor Some requires an argument`, `Constructor None takes no argument` |
| `type` 선언과 타입 표기의 타입 | `Unknown type: foo`, `Unbound type variable: 'b`(`type`과 `exception` 선언만), `Duplicate type variable: 'a`, `Type list expects 1 type argument(s) but got 0` |
| 레코드 | `Unknown record field: x`, `Missing record field: y`, `The record field y does not belong to type p` |
| 같은 이름의 예외를 다시 선언 | `Exception E is already defined` (범위가 겹치지 않아도, `Failure`와 `Not_found` 포함) |

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

- **생성자 목록:** `bool`, 리스트, `option`, 튜플, 사용자 타입, 레코드(생성자 하나)는 생성자를 모두 압니다. `int`와 `string`은 값이 무한히 많으므로 `_`나 변수가 있어야 덮습니다.
- **가드:** 가드가 있는 분기는 실패할 수 있으므로 덮는 데 세지 않습니다(OCaml, F#과 같음).
- **알고리즘:** Maranget, "Warnings for pattern matching"(2007)의 usefulness 검사입니다. 값의 예도 같은 계산에서 나옵니다(`Infer.typecheckWithWarnings`).
- **같은 결과:** 경고가 없는 프로그램은 실행 중에 `Match failure`가 나지 않습니다. 평가 예제로 이것을 테스트합니다(`DiagnosticTests`).

### 쓰이지 않는 분기 경고

앞의 분기들이 이미 모든 경우를 덮어서 절대 선택되지 않는 분기를 경고합니다(OCaml Warning 11, F# FS0026과 같음). 위치는 그 분기의 패턴을 가리킵니다.

```
$ calc -e "match true with true -> 1 | false -> 2 | _ -> 3"
Warning: this match case is unused (line 1, column 42)
  1 | match true with true -> 1 | false -> 2 | _ -> 3
    |                                          ^
```

| 예 | 쓰이지 않는 분기 |
|---|---|
| `\| _ -> 0 \| 1 -> 1` | `1` (`_`가 이미 모두 덮음) |
| `\| [] -> 0 \| x :: _ -> x \| [y] -> y` | `[y]` |
| `\| Some _ -> 1 \| Some 2 -> 2 \| None -> 0` | `Some 2` |
| `\| 1 \| 2 -> 0 \| 2 -> 1 \| _ -> 9` | `2` (or 패턴이 이미 덮음) |

- **가드:** 가드가 있는 앞 분기는 실패할 수 있으므로 덮는 데 세지 않습니다. `| x when x > 0 -> 1 | 1 -> 2`의 두 번째 분기는 경고하지 않습니다. 가드가 있는 분기 자체도 앞 분기가 이미 덮으면 경고합니다.
- **판정:** 빠진 분기 검사와 같은 Maranget usefulness 검사입니다. 분기마다 "앞의 가드 없는 분기들이 덮지 못하는 값 중에 이 패턴이 맞는 값이 있는가"를 묻습니다.
- **아직 하지 않는 것:** or 패턴의 한쪽만 쓰이지 않는 경우(OCaml Warning 12)는 경고하지 않습니다.

### 버리는 값 경고

`e1; e2`에서 버리는 `e1`의 타입이 `unit`이 아니면 경고합니다(F# FS0020, OCaml Warning 10과 같음). 값을 일부러 버리려면 `ignore e1`이나 `let _ = e1`을 씁니다.

```
$ calc -e "1; 2"
Warning: this expression has type int and its value is ignored (use ignore or let _ = ...) (line 1, column 1)
```

### 타입 변수가 다른 타입으로 정해짐 경고

타입 표기에 쓴 `'a`가 추론이 끝난 뒤 구체적인 타입으로 정해지면 경고합니다(F# FS0064와 같음). `'a`라고 적었지만 polymorphic하지 않다는 뜻입니다. 위치는 그 `'a`를 처음 쓴 타입 표기입니다.

```
$ calc -e "let f (x: 'a) = x + 1 in f 2"
Warning: the type variable 'a has been constrained to be type int (line 1, column 11)
  1 | let f (x: 'a) = x + 1 in f 2
    |           ^
```

- `+`, `<`처럼 allowed list가 있는 연산자로 정해졌는데 끝까지 하나로 정해지지 않았으면 `int or string`으로 보여 줍니다.
- 다른 `'b`와 같은 타입이 된 경우(`fun (x: 'a) (y: 'b) -> [x; y]`)는 경고하지 않습니다.

### soundness와 남은 실행 중 오류

타입 검사를 통과한 프로그램은 실행 중에 `Type error`가 나지 않습니다. 함수끼리 `=` 비교만 예외입니다(테스트: `InferTests`의 soundness 테스트). 다음 오류는 여전히 실행 중에 납니다.

- `Match failure` (빠진 분기가 있으면 실행 전에 경고함, 아래), `substr` 범위, `toInt` 변환 실패
- 잡히지 않은 예외(`head []`, `nth` 범위, `failwith`, `raise`)
- 0으로 나누기, 재귀가 너무 깊음
- 함수끼리 `=` 비교: 타입은 `'a -> 'a -> bool`로 통과하고, 실행 중에 `Type error: expected int, bool, string, list, tuple or data but got function`

