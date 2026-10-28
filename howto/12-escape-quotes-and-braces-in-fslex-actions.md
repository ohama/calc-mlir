---
order: 12
created: 2026-10-28
commit: fa0dbad
version: 0.8.0
description: fslex/fsyacc는 { } 액션 코드를 F# 문법이 아니라 자체 규칙으로 훑는다. 문자 리터럴 '"'와 '}'는 '\"'와 '}'로 쓴다
---

# fslex 액션 코드에서 `'"'`와 `'}'` 쓰기

> 관련 커밋: [fa0dbad](https://github.com/ohama/calc-mlir/commit/fa0dbad) (v0.8.0)

fslex와 fsyacc는 `{ ... }` 안의 액션 코드를 F# 컴파일러처럼 파싱하지 않는다. 액션이 어디서 끝나는지 찾으려고 **중괄호 짝과 문자열 `"..."`만** 따라가는 간단한 스캐너를 쓴다. 이 스캐너는 F# 문자 리터럴을 모른다. 그래서 `'"'`의 `"`는 문자열의 시작으로, `'}'`의 `}`는 액션의 끝으로 읽는다.

## The Insight

```
| '\\' '"'   { sb.Append('"') |> ignore; ... }
                         ^
                         fslex: "여기서 문자열이 시작된다"
                         → 닫는 " 를 찾다가 파일 끝에 도달
```

F# 컴파일러가 보기 전에 fslex가 먼저 실패한다. 오류 위치도 **파일의 마지막 줄 다음**을 가리키므로 원인을 찾기 어렵다.

```
Lexer.fsl(71): error : unterminated string in code
```

(이 프로젝트의 `Lexer.fsl`은 70줄이었다.)

## Why This Matters

문자열 리터럴을 렉싱하려면 `"`를 다루는 코드가 반드시 필요하다. 처음 쓰는 코드가 바로 `sb.Append('"')`이다. 패턴 쪽(`| '"' { ... }`)은 문제가 없기 때문에 더 헷갈린다.

FsLexYacc 11.3.0에서 액션 코드만 바꿔 가며 확인한 결과다.

| 액션 코드 | fslex | fsyacc |
|---|---|---|
| `'"'` | ❌ `unterminated string in code` | ✅ |
| `let a = '"' in let b = '"' in 1` | ✅ (우연히 통과) | ✅ |
| `'\"'` | ✅ | ✅ |
| `"\""` | ✅ | — |
| `char 34` | ✅ | — |
| `'}'` | ❌ `Unexpected character '''` | ❌ 같은 오류 |
| `"}"` | ✅ | — |
| `'}'` | ✅ | — |
| `(* " *)` 주석 | ❌ `unterminated string in code` | ❌ 같은 오류 |
| `// "` 한 줄 주석 | ✅ | — |
| `%{ %}` / `{ }` 헤더 안의 `'"'` | ❌ (fslex 헤더) | — |

- **`'"'` 두 개는 통과한다.** 두 `"` 사이가 문자열 하나로 짝지어지기 때문이다. 고칠 때 하나를 지우면 갑자기 빌드가 깨진다.
- **`'}'`는 액션을 그 자리에서 끝낸다.** 그 뒤의 `'`가 규칙 문법에 맞지 않아서 `Unexpected character '''`가 난다.
- fslex와 fsyacc의 동작이 다르다(`'"'`). 도구마다 외우기보다 둘 다에서 통과하는 형태를 쓴다.

## Recognition Pattern

- `.fsl`이나 `.fsy`를 빌드할 때 `unterminated string in code`가 나고, 줄 번호가 파일 끝을 가리킨다
- `Unexpected character '''`가 나는데 그 줄의 문법은 F#으로 보면 멀쩡하다
- 문자열 리터럴, 문자 리터럴, 중괄호를 다루는 렉서 규칙을 쓰고 있다

## The Approach

**액션 코드의 문자 리터럴에는 `"`나 `}`를 그대로 쓰지 않는다.** F#에서 뜻이 같은 이스케이프 형태로 쓴다.

### Step 1: 오류 위치가 파일 끝이면 따옴표를 의심한다

```bash
grep -n "'\"'\|'}'" src/Calc/Lexer.fsl src/Calc/Parser.fsy
```

블록 주석 `(* ... *)` 안의 `"`도 같은 문제를 일으키므로 함께 찾는다.

### Step 2: 이스케이프 형태로 바꾼다

| 원래 | 바꾼 형태 |
|---|---|
| `'"'` | `'\"'` |
| `'}'` | `'}'` 또는 `"}"`를 문자열로 쓰기 |
| `(* "따옴표" *)` | `// "따옴표"` 또는 주석에서 따옴표 빼기 |

`'\"'`는 F#에서 `'"'`와 같은 값이다. 위 표처럼 fslex와 fsyacc 모두 이 형태를 통과시킨다. 스캐너가 `\` 바로 뒤의 `"`를 문자열 시작으로 세지 않는 것으로 보인다(실험 결과로 추정한 것이고 소스는 확인하지 않았다).

### Step 3: 이유를 주석으로 남긴다

누군가 `'\"'`를 "불필요한 이스케이프"로 보고 `'"'`로 되돌리면 빌드가 깨진다.

## Example

```
// 여는 " 다음부터 닫는 " 까지 읽어서 STRING 토큰 하나를 만듦
and readString (sb: StringBuilder) = parse
    | '"'           { STRING (sb.ToString()) }                      // 패턴의 '"' 는 괜찮음
    // 액션 코드에서 '"' 라고 쓰면 fslex 가 " 를 문자열 시작으로 보고 "unterminated string in code" 오류를 냄
    | '\\' '"'      { sb.Append('\"') |> ignore; readString sb lexbuf }
    | '\\' '\\'     { sb.Append('\\') |> ignore; readString sb lexbuf }
```

```fsharp
// ❌ fslex: unterminated string in code
| '\\' '"'      { sb.Append('"') |> ignore; readString sb lexbuf }

// ✅
| '\\' '"'      { sb.Append('\"') |> ignore; readString sb lexbuf }
```

## 체크리스트

- [ ] 액션 코드와 헤더에 `'"'`, `'}'`가 없다
- [ ] 블록 주석 `(* *)` 안에 짝이 맞지 않는 `"`가 없다
- [ ] `'\"'`처럼 일부러 이스케이프한 곳에 이유를 주석으로 적었다
- [ ] 빌드 오류 줄 번호가 파일 끝이면 이 문서를 떠올린다

## 관련 문서

- `11-lex-string-literals-with-fslex.md` - 이 문제를 처음 만난 문자열 리터럴 렉서
