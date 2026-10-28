---
order: 11
created: 2026-10-28
commit: fa0dbad
version: 0.8.0
description: 문자열 리터럴은 정규식 하나로 잡지 말고 두 번째 렉서 규칙(and readString sb = parse)으로 한 글자씩 모은다. 이스케이프 규칙 순서와 eof 처리가 핵심이다
---

# fslex로 문자열 리터럴 렉싱하기

> 관련 커밋: [fa0dbad](https://github.com/ohama/calc-mlir/commit/fa0dbad) (v0.8.0)

`"..."`를 정규식 하나(`'"' [^ '"']* '"'`)로 잡으면 이스케이프(`\"`)를 처리할 수 없고, 오류 메시지도 만들 수 없다. 여는 `"`를 만나면 **두 번째 렉서 규칙으로 넘어가서** 닫는 `"`까지 한 조각씩 읽으며 `StringBuilder`에 모은다. fslex의 `and rule args = parse`가 이 용도다.

## The Insight

렉서를 "상태 기계"로 본다. 보통 상태(`tokenize`)와 문자열 안 상태(`readString`)는 **같은 문자에 다른 의미**를 준다.

| 문자 | `tokenize`에서 | `readString`에서 |
|---|---|---|
| `let` | 키워드 | 그냥 글자 |
| 공백 | 버림 | 값에 포함 |
| `\` | 알 수 없는 문자 | 이스케이프 시작 |
| `"` | 문자열 시작 | 문자열 끝 |
| 파일 끝 | `EOF` 토큰 | 오류 (닫히지 않음) |

이 차이를 정규식 하나에 다 넣으려 하지 말고, 상태마다 규칙 집합을 따로 둔다. 규칙에 인자(`sb`)를 주면 상태 사이에 누적 값을 넘길 수 있다.

## Why This Matters

- 정규식 한 줄로는 `"a\"b"`가 `"a\"`에서 끝난다. 이스케이프를 넣으려면 정규식이 복잡해지고, 그래도 이스케이프된 값으로 바꾸는 일은 액션에서 다시 해야 한다.
- 닫는 `"`가 없는 입력에서 `Unexpected character: "` 같은 엉뚱한 오류가 난다. 두 번째 규칙에서는 `eof`를 직접 잡아서 `Unterminated string`이라고 말할 수 있다.
- 이스케이프 규칙의 **순서**를 잘못 두면 fslex는 경고 없이 빌드되고, 정상적인 `\n`이 오류가 된다(아래 Step 2).

## Recognition Pattern

- 문자열, 문자, 주석(`(* ... *)`), heredoc처럼 **시작과 끝 구분자가 있고 안쪽 규칙이 다른** 토큰을 렉싱할 때
- 정규식 하나로 토큰을 잡으려다 이스케이프나 중첩 때문에 막혔을 때

## The Approach

### Step 1: 여는 `"`에서 두 번째 규칙을 호출한다

```
{
open FSharp.Text.Lexing
open Parser
open System.Text

let lexeme (lexbuf: LexBuffer<_>) = LexBuffer<_>.LexemeString lexbuf
}

rule tokenize = parse
    | '"'           { readString (StringBuilder()) lexbuf }
    ...

// 여는 " 다음부터 닫는 " 까지 읽어서 STRING 토큰 하나를 만듦
and readString (sb: StringBuilder) = parse
    | '"'           { STRING (sb.ToString()) }
    ...
```

- `readString`은 토큰 하나를 다 만들면 그 토큰을 반환한다. 파서는 `tokenize`가 `STRING`을 반환한 것으로 본다.

### Step 2: 이스케이프는 "구체적인 것 → 나머지" 순서로 둔다

```
    | '\\' '"'      { sb.Append('\"') |> ignore; readString sb lexbuf }
    | '\\' '\\'     { sb.Append('\\') |> ignore; readString sb lexbuf }
    | '\\' 'n'      { sb.Append('\n') |> ignore; readString sb lexbuf }
    | '\\' 't'      { sb.Append('\t') |> ignore; readString sb lexbuf }
    | '\\' 'r'      { sb.Append('\r') |> ignore; readString sb lexbuf }
    | '\\' _        { failwithf "Invalid escape sequence: %s" (lexeme lexbuf) }
```

fslex는 **가장 긴 일치**를 먼저 보고, 길이가 같으면 **앞의 규칙**을 고른다. `'\\' 'n'`과 `'\\' _`는 둘 다 길이 2이므로 순서가 결과를 정한다.

`'\\' _`를 위로 올리면 fslex는 경고 없이 빌드되지만 실행 결과가 이렇게 된다(실험으로 확인).

```
"a\nb"  →  Invalid escape sequence: \n
```

반면 `'\\' _`(길이 2)와 맨 끝의 `_`(길이 1) 사이에는 순서 문제가 없다. 길이가 다르면 긴 쪽이 이긴다.

> 액션 코드에서 `'"'` 대신 `'\"'`를 쓴 이유는 `12-escape-quotes-and-braces-in-fslex-actions.md`에 있다. `'"'`로 쓰면 fslex가 `unterminated string in code`로 실패한다.

### Step 3: 줄바꿈, 파일 끝, 나머지 문자를 처리한다

```
    | newline       { lexbuf.EndPos <- lexbuf.EndPos.NextLine; sb.Append(lexeme lexbuf) |> ignore; readString sb lexbuf }
    | eof           { failwith "Unterminated string" }
    | _             { sb.Append(lexeme lexbuf) |> ignore; readString sb lexbuf }
```

- **줄바꿈**: 값에 넣는 것과 별개로 `EndPos.NextLine`을 호출해야 이후 토큰의 줄 번호가 맞는다. `tokenize`의 줄바꿈 규칙과 같은 일을 한다.
- **`eof`**: 이 규칙이 없으면 문자열이 닫히지 않은 입력에서 fslex 런타임이 `unrecognized input` 예외를 낸다(실험으로 확인). 무엇이 잘못됐는지 알 수 없는 메시지이므로 `eof`를 직접 잡는다.
- **`"abc\`처럼 `\`로 끝나는 입력**: `'\\' _`는 `eof`와 일치하지 않는다. 그래서 `\`는 `_`로 값에 들어가고, 다음에 `eof` 규칙이 `Unterminated string`을 낸다. `"abc\"`도 마지막 `"`가 이스케이프되므로 같은 오류다.
- **유니코드**: 이 프로젝트는 `--unicode`로 렉서를 만든다. `_`가 UTF-16 문자 하나씩 일치하므로 한글과 이모지(서로게이트 쌍)가 그대로 값에 들어간다. 테스트로 확인했다.

### Step 4: 출력 함수를 렉서의 역함수로 만들고 왕복을 property-based testing으로 확인한다

값을 출력할 때 이스케이프를 되돌리면 "출력을 다시 입력하면 같은 값"이라는 성질을 테스트할 수 있다.

```fsharp
/// 문자열을 리터럴 형태로 출력 (렉서가 받는 이스케이프로 되돌림)
let quote (s: string) =
    let escaped =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r")
    "\"" + escaped + "\""
```

`\\`를 **가장 먼저** 바꿔야 한다. 나중에 바꾸면 앞에서 만든 `\"`, `\n`의 `\`까지 두 배가 된다.

```fsharp
testProperty "formatValue 출력을 다시 읽으면 같은 문자열" <| fun (NonNull (s: string)) ->
    calc (formatValue (VString s)) = VString s
```

이 프로젝트에서는 처음에 `\r` 이스케이프를 빠뜨렸다. 렉서는 날것의 CR을 값에 그대로 넣어서 왕복 테스트는 통과했다. 하지만 CLI 출력에서 CR이 커서를 줄 앞으로 돌려 결과가 `"ab\nc"`처럼 깨져 보였다. 그래서 출력 쪽에 `\r`을 추가했고, 출력이 읽을 수 있도록 렉서에도 `\r`을 추가했다. **출력에 쓰는 이스케이프 집합 ⊆ 렉서가 받는 이스케이프 집합**이 되도록 두 곳을 함께 고친다.

## Example

렉서 테스트로 경계를 고정한다.

```fsharp
test "문자열 안의 키워드와 기호는 그대로" {
    Expect.equal (tokens "\"let x = [1] // \"") [ STRING "let x = [1] // "; EOF ] ""
}
test "문자열 이스케이프" {
    Expect.equal (tokens "\"a\\\"b\\\\c\\nd\\te\"") [ STRING "a\"b\\c\nd\te"; EOF ] ""
}
test "닫히지 않은 문자열" {
    Expect.throwsC (fun () -> tokens "\"abc" |> ignore)
                   (fun ex -> Expect.equal ex.Message "Unterminated string" "")
}
test "문자열 끝의 \\ 는 닫는 \" 를 이스케이프" {
    Expect.throwsC (fun () -> tokens "\"abc\\\"" |> ignore)
                   (fun ex -> Expect.equal ex.Message "Unterminated string" "")
}
```

테스트 코드의 F# 문자열 안에 언어의 문자열을 넣으면 이스케이프가 두 겹이 된다. 헷갈리면 F#의 `"""..."""` 삼중 따옴표 문자열을 쓴다.

## 체크리스트

- [ ] 여는 `"`에서 `and readString (sb: StringBuilder) = parse`로 넘어간다
- [ ] 구체적인 이스케이프(`\"`, `\\`, `\n` ...)가 `'\\' _`보다 위에 있다
- [ ] `eof` 규칙이 `Unterminated string`을 낸다
- [ ] 문자열 안의 줄바꿈에서도 `EndPos.NextLine`을 호출한다
- [ ] 출력 함수가 `\\`를 가장 먼저 이스케이프한다
- [ ] 출력 이스케이프 집합과 렉서 이스케이프 집합이 맞고, 왕복 property-based testing이 있다

## 관련 문서

- `12-escape-quotes-and-braces-in-fslex-actions.md` - 액션 코드에서 `'"'`를 쓰면 빌드가 깨지는 문제
- `03-order-fslex-keywords-before-identifiers.md` - 가장 긴 일치와 규칙 순서
- `06-write-fscheck-properties-for-parser.md` - 문자열을 거치는 property-based testing
