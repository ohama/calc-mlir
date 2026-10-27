---
created: 2026-10-27
description: fslex는 가장 긴 일치를 먼저 보고, 길이가 같으면 앞에 있는 규칙을 고른다. 키워드 규칙은 식별자 규칙보다 앞에 둔다
---

# fslex에서 키워드와 식별자 규칙 순서 정하기

fslex에서 `let`이 키워드(`LET`)가 될지 식별자(`IDENT "let"`)가 될지는 **규칙을 쓴 순서**가 정한다. `letx`가 식별자가 되는 것은 **일치 길이**가 정한다.

## The Insight

fslex(그리고 lex, ocamllex)는 토큰 하나를 고를 때 두 기준을 순서대로 적용한다.

1. **가장 긴 일치 (longest match)**: 입력을 가장 길게 소비하는 규칙을 고른다.
2. **먼저 쓴 규칙 (first rule)**: 길이가 같으면 `.fsl` 파일에서 **위에 있는** 규칙을 고른다.

| 입력 | 후보 규칙 | 일치 길이 | 선택 | 기준 |
|---|---|---|---|---|
| `let` | `"let"`, `ident` | 3, 3 | `LET` | 2. 먼저 쓴 규칙 (키워드가 위에 있으므로) |
| `letx` | `"let"`, `ident` | 3, **4** | `IDENT "letx"` | 1. 가장 긴 일치 |
| `<=` | `'<'`, `"<="` | 1, **2** | `LESSEQUAL` | 1. 가장 긴 일치 (규칙 순서는 상관없음) |
| `2x` | `digit+`, `ident` | 1 (`2`), 0 | `NUMBER 2`, 다음은 `IDENT "x"` | 식별자는 숫자로 시작할 수 없으므로 |

따라서 **키워드는 식별자 규칙보다 앞에** 있어야 한다. 반대로 `<=`와 `<`처럼 길이가 다른 토큰끼리는 순서를 신경 쓸 필요가 없다.

## Why This Matters

식별자 규칙을 키워드보다 위에 두면 `let`도 `IDENT "let"`이 된다. 길이가 같으므로 먼저 쓴 규칙이 이기기 때문이다. 키워드 규칙은 절대 선택되지 않는다.

이때 생기는 증상이 헷갈린다.

- 렉서는 아무 오류도 내지 않는다.
- 파서가 `IDENT "let"`, `IDENT "x"`, `EQUALS` ... 를 받아서 **`parse error`** 를 낸다.
- 오류 메시지에 위치 정보가 없으면 문법(`.fsy`)이 잘못된 것처럼 보여서 엉뚱한 곳을 디버깅하게 된다.

## Recognition Pattern

- 새 키워드를 추가했는데 그 키워드를 쓰는 식이 전부 `parse error`가 난다
- 렉서 테스트에서 키워드가 `IDENT "..."`로 나온다
- 반대로 `letter`, `iffy`, `notify`처럼 키워드로 **시작하는** 이름이 키워드로 잘린다고 의심될 때 (fslex에서는 가장 긴 일치 때문에 생기지 않는다)

## The Approach

### Step 1: 규칙을 이 순서로 배치한다

```
rule tokenize = parse
    | whitespace    { tokenize lexbuf }
    | digit+        { NUMBER (int (lexeme lexbuf)) }
    // 키워드는 IDENT 보다 먼저 와야 함 (같은 길이로 매치되면 앞의 규칙이 이김)
    | "let"         { LET }
    | "in"          { IN }
    | "if"          { IF }
    | "true"        { TRUE }
    ...
    | ident_start ident_char*
                    { IDENT (lexeme lexbuf) }
    // 연산자: 길이가 달라서 순서는 무관하지만 읽기 쉽게 긴 것부터
    | "<>"          { NOTEQUAL }
    | "<="          { LESSEQUAL }
    | '<'           { LESS }
    ...
    | eof           { EOF }
    | _             { failwithf "Unexpected character: %s" (lexeme lexbuf) }   // 항상 마지막
```

`_`(아무 문자)는 길이 1로 거의 모든 입력에 일치한다. 그래서 반드시 **맨 마지막**에 둔다. 이 규칙 덕분에 `&` 하나, `|` 하나 같은 입력이 명확한 오류 메시지를 낸다.

### Step 2 (대안): 키워드가 많아지면 식별자 규칙 안에서 표로 찾는다

키워드가 수십 개가 되면 규칙을 하나씩 나열하는 대신, 식별자로 읽은 다음 키워드 표에서 찾는다. 이렇게 하면 **순서 문제가 아예 사라진다**.

```
{
let keywords =
    dict [ "let", LET; "in", IN; "if", IF; "then", THEN; "else", ELSE
           "true", TRUE; "false", FALSE; "not", NOT ]
}

rule tokenize = parse
    ...
    | ident_start ident_char*
        { let s = lexeme lexbuf
          match keywords.TryGetValue s with
          | true, tok -> tok
          | _ -> IDENT s }
```

### Step 3: 경계 사례를 렉서 테스트로 고정한다

## Example

이 프로젝트의 렉서 테스트다.

```fsharp
test "키워드로 시작하는 식별자는 IDENT" {
    Expect.equal (tokens "letx inner") [ IDENT "letx"; IDENT "inner"; EOF ] ""
}
test "키워드로 시작하는 식별자: iffy, trueish, notx" {
    Expect.equal
        (tokens "iffy trueish notx")
        [ IDENT "iffy"; IDENT "trueish"; IDENT "notx"; EOF ] ""
}
test "비교 연산자: 공백 없이 (가장 긴 일치)" {
    Expect.equal (tokens "1<=2") [ NUMBER 1; LESSEQUAL; NUMBER 2; EOF ] ""
}
test "숫자 뒤 식별자는 분리" {
    Expect.equal (tokens "2x") [ NUMBER 2; IDENT "x"; EOF ] ""
}
```

`tokens`는 EOF가 나올 때까지 `Lexer.tokenize`를 반복해서 호출하는 보조 함수다.

```fsharp
let tokens (input: string) =
    let lexbuf = LexBuffer<char>.FromString input
    let rec loop acc =
        match Lexer.tokenize lexbuf with
        | EOF -> List.rev (EOF :: acc)
        | t -> loop (t :: acc)
    loop []
```

키워드는 대소문자를 구분한다. `Let`, `True`는 `IDENT`다. 대소문자를 구분하지 않으려면 Step 2 방식에서 `s.ToLowerInvariant()`로 찾는다.

## 체크리스트

- [ ] 키워드 규칙이 식별자 규칙보다 **위에** 있다 (또는 키워드 표 방식을 쓴다)
- [ ] `_` 규칙이 맨 마지막에 있다
- [ ] "키워드 그 자체", "키워드로 시작하는 식별자", "공백 없이 붙은 연산자" 를 렉서 테스트로 확인했다
- [ ] 키워드를 변수 이름으로 쓰면 오류가 나는지 확인했다 (예: `let in = 1 in 2` → `parse error`)

## 관련 문서

- `control-let-if-scope-with-precedence.md` - 키워드를 추가한 뒤 파서 쪽 우선순위 정하기
