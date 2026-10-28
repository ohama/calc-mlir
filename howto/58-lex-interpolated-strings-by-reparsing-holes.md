---
order: 58
created: 2026-10-29
commit: 1663f01
version: 0.33.0
description: 보간 문자열 $"a{e}b"의 구멍은 렉서가 소스로 모은 뒤 그 자리에서 파서를 다시 불러 파싱한다. 새 LexBuffer의 EndPos를 구멍의 시작 위치로 두면 오류 위치가 바깥 입력에서 이어진다. 결과는 토큰 하나(포맷 문자열과 식 목록)이고, 파서는 이것을 sprintf 노드로 바꾼다. fslex 규칙 인자의 타입 표기는 이름 하나만 받는다
---

# 보간 문자열의 구멍을 렉서에서 다시 파싱하기

> 관련 커밋: [1663f01](https://github.com/ohama/calc-mlir/commit/1663f01) (v0.33.0)

`$"Hello {name}, {age + 1} next year"`는 문자열 안에 **식**이 들어 있다. 문자열 리터럴은 렉서가 토큰 하나로 읽는다. 그런데 식은 파서가 읽어야 하고, 식 안에 다시 문자열이나 중괄호가 있을 수 있다(`{f "}"}`).

흔한 방법은 두 가지다.

| 방법 | 하는 일 | 비용 |
|---|---|---|
| 렉서가 토큰을 나눔 | `INTERP_START "Hello "`, 구멍의 토큰들, `INTERP_MID ", "`, ..., `INTERP_END` | 렉서가 "지금 구멍 안인가"를 중첩 단계별로 기억해야 하고(닫는 `}`가 구멍의 끝인지 레코드의 끝인지), 문법에 규칙이 늘어 충돌이 생기기 쉬움 |
| 렉서가 구멍을 다시 파싱 | 구멍의 소스를 모아 그 자리에서 파서를 부르고, 결과 식을 토큰에 담음 | 파서를 여러 번 부름. 오류 위치를 맞춰야 함 |

Calc는 두 번째를 쓴다. 문법에는 토큰 하나와 규칙 하나만 늘었고, 충돌은 0개 그대로다.

## The Insight

**구멍은 그 자체로 완전한 식이다. 파서 입장에서는 작은 입력 하나를 따로 파싱하는 것과 같다.** 렉서는 구멍의 끝(짝이 맞는 `}`)만 찾으면 된다. 그러면 이미 있는 파서를 그대로 다시 쓸 수 있다.

```
$"a{x + 1}b%d{n}"
  렉서:  글자 → 포맷 문자열  "a%Ab%d"
         구멍 → 소스 "x + 1", "n" → Parser.start 로 각각 파싱
         토큰: INTERP ("a%Ab%d", [Add (Var "x", Number 1); Var "n"])
  파서:  App (App (Printf ("sprintf", "a%Ab%d"), Add ...), Var "n")
```

보간 문자열은 `sprintf`의 문법 설탕이 된다. 타입 추론, 인터프리터, 컴파일러는 새로 할 일이 없다(`57-type-printf-from-format-string-literals.md`).

남는 문제는 **위치**다. 구멍만 따로 파싱하면 위치가 구멍의 소스 기준(1행 1열)이 된다. 새 `LexBuffer`의 위치를 구멍의 시작 위치에서 출발시키면 해결된다.

## Why This Matters

- **위치를 맞추지 않으면** `$"%d{"x"}"`의 타입 오류가 1행 1열을 가리킨다. 여러 줄 프로그램에서는 다른 줄을 가리킨다.
- **구멍의 끝을 중괄호 수로만 찾으면** `{f "}"}`의 문자열 속 `}`에서 구멍이 끝난다.
- **포맷 문자열로 모을 때 `%`를 그대로 두면** `$"100%"`가 `Missing format specifier after %` 오류가 된다. 글자 `%`는 `%%`로 모아야 한다.

## Recognition Pattern

- 문자열 안에 식이 들어가는 문법을 추가할 때 (보간 문자열, 템플릿, 셸의 `$(...)`)
- 렉서 상태를 여러 개 두는 대신 기존 파서를 재사용하고 싶을 때
- 다른 입력 조각을 파싱한 결과의 오류 위치를 원래 입력에 맞춰야 할 때

## The Approach

### Step 1: `$"`에서 보간 문자열을 읽는다

```
| '$' '"'       { readInterpolated (StringBuilder()) [] lexbuf.StartPos lexbuf }
```

`readInterpolated`는 닫는 `"`까지 읽으며 두 가지를 모은다. 포맷 문자열(`sb`)과, 구멍의 식을 거꾸로 모은 목록(`holes`)이다.

| 입력 | 포맷 문자열에 더할 것 | 구멍 |
|---|---|---|
| 보통 글자, `\n` 같은 이스케이프 | 그 글자 | |
| `{{`, `}}` | `{`, `}` | |
| `%` | `%%` | |
| `{e}` | `%A` | `e` |
| `%d{e}` (`%s`, `%b`, `%A`도) | `%d` | `e` |
| 짝 없는 `}` | 오류: `Unmatched } in interpolated string (use }} for a brace)` | |

닫는 `"`에서 토큰의 시작 위치를 `$`로 되돌린다. 보간 문자열 전체에 대한 오류(`1 + $"a"`)가 `$`를 가리킨다.

```
| '"'           { lexbuf.StartPos <- start; INTERP (sb.ToString(), List.rev holes) }
```

### Step 2: 구멍의 끝을 찾는다

```
and interpolationHole (source: StringBuilder) (depth: int) (start: Position) = parse
    | '}'           { if depth = 0 then source.ToString()
                      else (source.Append(lexeme lexbuf) |> ignore; interpolationHole source (depth - 1) start lexbuf) }
    | '{'           { source.Append(lexeme lexbuf) |> ignore; interpolationHole source (depth + 1) start lexbuf }
    | '"' ([^ '"' '\\' '\n' '\r'] | '\\' _)* '"'
                    { source.Append(lexeme lexbuf) |> ignore; interpolationHole source depth start lexbuf }
    | newline       { lexbuf.EndPos <- lexbuf.EndPos.NextLine; ... }
    | eof           { syntaxError "Unterminated string" start }
    | _             { source.Append(lexeme lexbuf) |> ignore; interpolationHole source depth start lexbuf }
```

- **중괄호 깊이를 센다.** 구멍 안의 중괄호도 짝을 맞춘다.
- **문자열 리터럴은 통째로 건너뛴다.** 그래서 `$"[{"}"}]"`의 `"}"`에서 구멍이 끝나지 않는다. 안쪽 보간 문자열 `$"{ $"in{1}" }"`도 `$` 한 글자와 문자열 리터럴로 읽혀서 통째로 넘어간다.
- **줄바꿈에서 `EndPos`를 다음 줄로** 옮긴다. 구멍 뒤의 위치가 맞는다.

### Step 3: 구멍을 파서로 파싱하고 위치를 이어 붙인다

```fsharp
let parseHole (tokenize: LexBuffer<char> -> token) (source: string) (start: Position) : Ast.Expr =
    if source.Trim() = "" then syntaxError "Empty expression in interpolated string" start
    let lexbuf = LexBuffer<char>.FromString source
    lexbuf.EndPos <- start                  // 위치를 구멍의 시작에서 출발
    try
        Parser.start tokenize lexbuf
    with ParseFailure ->
        // AbsoluteOffset 도 start 에서 출발하므로, 소스 안의 위치는 start 만큼 빼서 구함
        let tokenStart = lexbuf.StartPos.AbsoluteOffset - start.AbsoluteOffset
        let tokenEnd = lexbuf.EndPos.AbsoluteOffset - start.AbsoluteOffset
        if tokenStart >= tokenEnd then syntaxError "parse error: unexpected end of interpolated expression" lexbuf.StartPos
        else syntaxError (sprintf "parse error: unexpected '%s'" (source.Substring(tokenStart, tokenEnd - tokenStart))) lexbuf.StartPos
```

- **`StartPos`가 아니라 `EndPos`를 바꾼다.** fslex가 만든 코드는 토큰을 읽기 시작할 때 `EndPos`를 `StartPos`로 복사하고, 읽은 만큼 `EndPos`를 옮긴다. 그래서 `EndPos`가 "지금 위치"다.
- **`start`는 `{` 바로 뒤의 위치**(`lexbuf.EndPos`)다. 파서가 노드에 기록하는 위치(`41-track-source-positions-without-changing-ast.md`)가 모두 바깥 입력 기준이 된다. 타입 오류도 그대로 원래 줄과 열을 가리킨다.
- **같은 시작 기호(`start`)를 쓴다.** 시작 기호는 입력 끝까지 읽어야 끝나므로, `{1 )}`처럼 식 뒤에 남는 토큰도 오류다.
- **파서를 렉서 안에서 다시 부르는 것은 안전하다.** fsyacc가 만든 파서는 상태 스택을 호출마다 따로 만든다. 바깥 파서는 토큰 하나를 기다리는 중이므로, 안쪽 파싱이 끝난 뒤 그 토큰을 받는다.

렉서(`Lexer.fsl`)가 파서(`Parser.start`)를 부르지만 순환 참조는 아니다. fsyacc가 만든 `Parser.fs`는 토큰 타입과 파서를 담고, 렉서 함수는 인자(`tokenize`)로 받는다. 컴파일 순서는 `Parser.fs` 다음 `Lexer.fs`다.

### Step 4: 파서는 토큰을 sprintf 적용으로 바꾼다

```
%token <string * Ast.Expr list> INTERP

Atom:
    | INTERP    { let format, holes = $1 in
                  at parseState (List.fold (fun f e -> App (f, e)) (at parseState (Printf ("sprintf", format))) holes) }
```

`Printf ("sprintf", format)`은 리터럴이 붙은 `sprintf`와 같은 노드다. 사용자가 `sprintf`라는 이름을 가려도 보간 문자열은 내장 함수의 뜻을 그대로 쓴다.

### Step 5: fslex의 함정 두 가지

**규칙 인자의 타입 표기는 이름 하나만 받는다.** FsLexYacc 11.3.0에서 확인한 결과다.

| 규칙 머리 | fslex |
|---|---|
| `and r (holes: int)` | ✅ |
| `and r (start: Position)` | ✅ |
| `and r holes (start: Position)` | ✅ (표기 없음) |
| `and r (holes: int list)` | ❌ `parse error` |
| `and r (holes: Ast.Expr)` | ❌ `parse error` |
| `and r (holes: list<int>)` | ❌ `Unexpected character '<'` |

오류 위치가 그 규칙 머리의 표기 자리(열 번호)를 가리킨다. 표기를 빼면 F#이 타입을 추론한다.

**액션 코드의 `'{'`, `'}'` 문자 리터럴은 쓰지 않는다.** fslex는 액션의 끝을 중괄호 짝으로 찾는데, 문자 리터럴을 모른다. `"{"` 같은 문자열로 쓴다(`12-escape-quotes-and-braces-in-fslex-actions.md`).

```
| "{{"    { sb.Append("{") |> ignore; ... }     // ✅  '{' 이면 액션이 파일 끝까지 이어져 parse error
```

규칙 부분의 `//` 주석에 들어간 중괄호는 문제가 없다. 이 프로젝트에서 처음에 주석을 의심했지만, 실제 원인은 위의 타입 표기였다.

## Example

| 입력 | 결과 |
|---|---|
| `let name = "Kim" in $"Hello {name}, {1 + 2} next {{x}} 100%"` | `"Hello Kim, 3 next {x} 100%"` |
| `$"[{"}"}]"` | `"[}]"` (구멍 안 문자열의 `}`) |
| `$"{ $"in{1}" }"` | `"in1"` (중첩) |
| `$"%d{"x"}"` | `Type mismatch: expected int but got string (line 1, column 6)` |
| `$"a{1 )}"` | `parse error: unexpected ')' (line 1, column 7)` |
| `$"a{1 +}"` | `parse error: unexpected end of interpolated expression (line 1, column 8)` |
| `$"a{}"` | `Empty expression in interpolated string (line 1, column 5)` |
| `$"a}"` | `Unmatched } in interpolated string (use }} for a brace) (line 1, column 4)` |
| `$"a{1` | `Unterminated string (line 1, column 1)` |

여러 줄에 걸친 구멍도 원래 줄을 가리킨다.

```
let x = 1 in
$"a{x}b{x +
  true}"
Error: Type mismatch: expected int but got bool (line 3, column 3)
```

## 체크리스트

- [ ] 구멍의 끝을 찾을 때 중괄호 깊이를 세고 문자열 리터럴을 건너뛰는가?
- [ ] 글자 `%`를 `%%`로 모으는가?
- [ ] 구멍을 파싱하는 `LexBuffer`의 `EndPos`를 구멍의 시작 위치로 두는가?
- [ ] 구멍의 파서 오류 토큰을 `AbsoluteOffset - start.AbsoluteOffset`으로 찾는가?
- [ ] 빈 구멍, 짝 없는 `}`, 닫히지 않은 문자열을 오류로 내는가?
- [ ] 보간 문자열 토큰의 시작 위치를 `$`로 되돌리는가?
- [ ] fslex 규칙 인자에 `Ast.Expr list` 같은 타입 표기를 쓰지 않았는가?

## 관련 문서

- `57-type-printf-from-format-string-literals.md` - 보간 문자열이 바뀌는 `Printf` 노드의 타입과 실행
- `11-lex-string-literals-with-fslex.md` - 문자열 리터럴을 읽는 렉서 규칙
- `12-escape-quotes-and-braces-in-fslex-actions.md` - 액션 코드의 `'"'`, `'}'`
- `41-track-source-positions-without-changing-ast.md` - 노드에 위치를 기록하는 방법
