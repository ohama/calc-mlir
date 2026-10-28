---
created: 2026-10-28
description: fsyacc의 기본 오류는 위치 없는 Exception "parse error"다. parse_error_rich로 구분되는 예외를 던지고, 오류 토큰의 위치는 lexbuf의 StartPos/EndPos에서 읽는다
---

# fsyacc 파서 오류에 줄과 열 붙이기

fsyacc가 만든 파서는 문법 오류에서 **`System.Exception("parse error")`** 만 던진다. 위치도, 어떤 토큰이 문제인지도 없다. 필요한 정보는 이미 `lexbuf`에 있다. 파서가 오류를 낸 순간 `lexbuf`는 **문제의 토큰을 막 읽은 상태**이기 때문이다. 그래서 두 가지만 하면 된다.

1. `parse_error_rich`로 파서 오류를 다른 예외와 구분한다.
2. 파서를 부른 곳에서 그 예외를 잡고 `lexbuf.StartPos`/`EndPos`로 위치를 붙인다.

## The Insight

LR 파서는 다음 토큰(lookahead)을 보고 나서 "이 토큰으로는 진행할 수 없다"고 판단한다. 그래서 오류 시점의 `lexbuf`에는 그 토큰의 위치가 남아 있다. 이 프로젝트에서 실제로 찍어 본 값이다.

| 입력 | 예외 | `StartPos` (줄, 열) | `EndPos` | 마지막 lexeme |
|---|---|---|---|---|
| `1 + + 2` | `System.Exception: parse error` | (0, 4) | (0, 5) | `+` |
| `let in` | 같음 | (0, 4) | (0, 6) | `in` |
| `\n\n  1 2 )` | 같음 | (2, 6) | (2, 7) | `)` |
| `1 +` | 같음 | (0, 3) | (0, 3) | `` (빈 문자열 = EOF) |
| `1 $ 2` | `System.Exception: Unexpected character: $` | (0, 2) | (0, 3) | `$` |

여기서 세 가지를 알 수 있다.

- **fslex의 `Position`은 줄과 열이 0부터** 시작한다. 사람에게 보여 줄 때는 1을 더한다.
- **EOF에서는 `StartPos = EndPos`** 이다. 이 경우 토큰 대신 "입력 끝"이라고 말해야 한다.
- 렉서 오류와 파서 오류가 **같은 `System.Exception` 타입**이다. 메시지 문자열로 구분하는 수밖에 없는데, 그러면 깨지기 쉽다.

## Why This Matters

- `Error: parse error`만으로는 50줄짜리 입력에서 어디를 고쳐야 할지 알 수 없다.
- `try ... with ex when ex.Message = "parse error"`로 구분하면 FsLexYacc가 메시지를 바꾸는 순간 조용히 깨진다. 같은 `Exception` 타입이라 타입으로도 구분이 안 된다.
- 렉서 오류에도 위치가 없으면 사용자는 같은 문제를 다시 겪는다.

## Recognition Pattern

- fsyacc 파서의 오류 메시지가 `parse error` 한 마디뿐일 때
- 오류 위치를 넣으려고 문법에 `error` 토큰 규칙을 추가하려다 충돌이 늘어날 때
- 렉서 오류와 파서 오류를 구분해서 처리하고 싶을 때

## The Approach

**위치는 렉서가 이미 관리하고 있다. 파서는 "오류가 났다"는 신호만 주면 된다.**

### Step 1: 오류 타입을 렉서·파서보다 앞에 정의한다

```fsharp
module Errors

open FSharp.Text.Lexing

/// 렉서·파서 오류. line, column 은 1부터 셈 (column 은 UTF-16 문자 단위)
exception SyntaxError of message: string * line: int * column: int with
    override this.Message = sprintf "%s (line %d, column %d)" this.message this.line this.column

/// fsyacc 의 parse_error_rich 가 던짐. 오류 토큰의 위치와 내용은 lexbuf 에 있으므로
/// Program.parse 가 잡아서 SyntaxError 로 바꿈
exception ParseFailure

/// fslex 의 Position 은 줄과 열이 0부터 시작
let syntaxError message (pos: Position) =
    raise (SyntaxError (message, pos.Line + 1, pos.Column + 1))
```

`Errors.fs`는 `Parser.fs`와 `Lexer.fs`보다 **앞에** 컴파일되어야 한다(`.fsproj`의 `<Compile>` 순서).

`Message`를 override해 두면 기존의 `eprintfn "Error: %s" ex.Message` 코드가 그대로 위치를 출력한다.

### Step 2: `parse_error_rich`로 파서 오류를 구분되는 예외로 바꾼다

fsyacc가 만든 `Parser.fs`에는 이런 코드가 있다.

```fsharp
parseError = (fun (ctxt:FSharp.Text.Parsing.ParseErrorContext<_>) ->
                  match parse_error_rich with
                  | Some f -> f ctxt
                  | None -> parse_error ctxt.Message);
```

`parse_error_rich`의 기본값은 FsLexYacc 런타임에 있는 `None`이다. `.fsy`의 헤더에 같은 이름을 정의하면 그쪽이 쓰인다.

```
%{
open Ast

/// 기본 동작은 위치 없는 Exception "parse error". Program.parse 가 lexbuf 의 위치를 붙이도록 따로 구분되는 예외를 던짐
let parse_error_rich = Some (fun (_: FSharp.Text.Parsing.ParseErrorContext<_>) -> raise Errors.ParseFailure)
%}
```

- 헤더는 생성 파일에서 `type token = ...`보다 **앞에** 놓인다. 그래서 `ParseErrorContext<token>`이라고 쓸 수 없다. `<_>`로 두면 아래의 `parseError`에서 쓰일 때 타입이 정해진다.
- `ParseErrorContext`에는 `CurrentToken`, `ShiftTokens`(올 수 있었던 토큰들) 같은 정보도 있다. "expected `in`" 같은 메시지를 만들 때 쓸 수 있다.

### Step 3: 파서를 부르는 곳에서 위치를 붙인다

```fsharp
let parse (input: string) : Expr =
    let lexbuf = LexBuffer<char>.FromString input
    try
        Parser.start Lexer.tokenize lexbuf
    with Errors.ParseFailure ->
        // 오류를 낸 토큰은 마지막으로 읽은 토큰 (StartPos ~ EndPos)
        let startPos, endPos = lexbuf.StartPos, lexbuf.EndPos
        if startPos.AbsoluteOffset = endPos.AbsoluteOffset then
            // 입력 끝: 끝의 공백·줄바꿈을 건너뛴 마지막 글자 바로 뒤를 가리킴
            let offset = input.TrimEnd().Length
            let before = input.Substring(0, offset)
            let line = before |> Seq.filter ((=) '\n') |> Seq.length
            let column = offset - (before.LastIndexOf '\n' + 1)
            raise (Errors.SyntaxError ("parse error: unexpected end of input", line + 1, column + 1))
        else
            let token = input.Substring(startPos.AbsoluteOffset, endPos.AbsoluteOffset - startPos.AbsoluteOffset)
            Errors.syntaxError (sprintf "parse error: unexpected '%s'" token) startPos
```

- **토큰 내용은 `LexemeString`이 아니라 원본 입력에서 잘라 온다.** 여러 규칙에 걸쳐 만든 토큰(문자열)은 마지막 lexeme이 토큰 전체가 아니기 때문이다(Step 4).
- **EOF 위치를 그대로 쓰지 않는다.** `echo "1 +" | calc`처럼 끝에 줄바꿈이 붙으면 EOF는 `line 2, column 1`이다. 사람이 기대하는 위치는 `+` 바로 뒤(`line 1, column 4`)다.

### Step 4: 여러 규칙에 걸친 토큰은 `StartPos`를 되돌린다

문자열은 여는 `"`에서 `readString` 규칙으로 넘어가 여러 lexeme을 읽는다. 토큰을 반환하는 시점의 `StartPos`는 **마지막 lexeme인 닫는 `"`** 를 가리킨다. 최소 렉서로 확인한 결과다.

```
입력: '  "abc"'   (토큰은 열 2에서 시작)
→ StartPos.Column = 6, EndPos.Column = 7, lexeme = '"'
```

이대로 두면 `let "ab" = 1`의 오류 위치가 닫는 `"`가 되고, 토큰 내용도 `"`만 보인다. 시작 위치를 넘겨 두었다가 토큰을 반환할 때 되돌린다.

```
    // 닫히지 않은 문자열 오류는 여는 " 위치로 알려야 하므로 시작 위치를 넘김
    | '"'           { readString (StringBuilder()) lexbuf.StartPos lexbuf }

and readString (sb: StringBuilder) (start: Position) = parse
    // 토큰 위치가 닫는 " 가 아니라 여는 " 부터가 되도록 StartPos 를 되돌림
    | '"'           { lexbuf.StartPos <- start; STRING (sb.ToString()) }
    ...
    | eof           { syntaxError "Unterminated string" start }
```

같은 `start`로 `Unterminated string` 오류도 여는 `"` 위치를 가리키게 된다.

### Step 5: 렉서 오류도 같은 예외로 바꾼다

```
    | _             { syntaxError (sprintf "Unexpected character: %s" (lexeme lexbuf)) lexbuf.StartPos }
```

줄 번호가 맞으려면 줄바꿈 규칙마다 `lexbuf.EndPos <- lexbuf.EndPos.NextLine`을 호출해야 한다. 문자열 안의 줄바꿈 규칙도 마찬가지다. 이걸 빠뜨리면 열은 맞는데 줄이 틀린다.

## Example

CLI에서는 위치로 원본 줄을 찾아 `^`를 찍는다.

```fsharp
/// 오류가 난 줄과 그 아래 ^ 표시. 탭은 그대로 두어야 ^ 가 같은 열에 맞음
let errorLocation (input: string) line column =
    let lines = input.Split('\n')
    if line < 1 || line > lines.Length then []
    else
        let text = lines.[line - 1].TrimEnd('\r')
        let prefix = text.Substring(0, min (column - 1) text.Length)
        let padding = prefix |> String.map (fun c -> if c = '\t' then '\t' else ' ')
        let gutter = string line
        [ sprintf "  %s | %s" gutter text
          sprintf "  %s | %s^" (String.replicate gutter.Length " ") padding ]
```

```
$ calc -e "1 + + 2"
Error: parse error: unexpected '+' (line 1, column 5)
  1 | 1 + + 2
    |     ^
```

출력 대신 문자열 리스트를 반환하게 만들면 테스트하기 쉽다.

```fsharp
Expect.equal (Program.errorLocation "\tx )" 1 4) [ "  1 | \tx )"; "    | \t  ^" ] ""
```

**한계:** 위치는 "파서가 더 진행할 수 없게 된 곳"이다. 실수한 곳은 더 앞일 수 있다. `let y = 2` 뒤에 `in`을 빠뜨리면 다음 줄이 함수 호출 `2 x + y`로 이어져서 입력 끝에서야 오류가 난다.

## 체크리스트

- [ ] 오류 예외 모듈이 `Parser.fs`, `Lexer.fs`보다 앞에 컴파일된다
- [ ] `.fsy` 헤더에 `parse_error_rich`를 정의해서 파서 오류를 구분되는 예외로 던진다
- [ ] 파서 호출부에서 `lexbuf.StartPos`/`EndPos`로 위치와 토큰 내용을 만든다 (0부터 → 1부터)
- [ ] EOF(`StartPos = EndPos`)는 "end of input"으로, 끝의 공백을 건너뛴 위치로 알린다
- [ ] 여러 규칙에 걸친 토큰(문자열)은 반환 전에 `StartPos`를 시작 위치로 되돌린다
- [ ] 모든 줄바꿈 규칙(문자열 안 포함)이 `EndPos.NextLine`을 호출한다
- [ ] 여러 줄, `\r\n`, 탭, 빈 입력, 끝 줄바꿈이 붙은 입력의 위치를 테스트로 고정했다

## 관련 문서

- `lex-string-literals-with-fslex.md` - 문자열 리터럴을 두 번째 렉서 규칙으로 읽는 방법
- `handle-nested-match-ambiguity-in-fsyacc.md` - fsyacc가 만드는 충돌과 해결
