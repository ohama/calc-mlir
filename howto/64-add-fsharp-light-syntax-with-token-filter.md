---
order: 64
created: 2026-10-29
commit: 555b77c
version: 0.38.0
description: 들여쓰기 문법은 문법을 고치지 말고 렉서와 파서 사이의 token filter가 가상 토큰(블록 괄호, in, ;, ;;)을 넣게 한다. 구분자는 앞 토큰이 식을 끝내고 다음 토큰이 식을 시작할 때만 넣는다. fsyacc는 IsPastEndOfStream이 켜지면 토큰 함수를 부르지 않는다. 기존 입력 전부의 AST 비교가 안전망이다
---

# token filter로 F# light syntax 넣기

> 관련 커밋: [555b77c](https://github.com/ohama/calc-mlir/commit/555b77c) (v0.38.0)

`let … in`, `;`, `;;`로 쓰는 ML 문법(F#의 verbose syntax, OCaml)에 F#처럼 들여쓰기로 블록을 구분하는 문법을 더한다. 문법(`Parser.fsy`)에는 규칙 하나만 더하고, 렉서와 파서 사이에 token filter를 둔다. 조사와 설계는 `docs/indentation-design.md`에 있다.

## The Insight

**들여쓰기 문법은 "빠진 토큰을 대신 써 주는 것"이다.**

```
let f x =                     let f x = ·begin
    let y = x + 1                 let y = ·begin x + 1 ·end ·in
    y * 2                         y * 2 ·end
                              ·;;
println (f 1)                 println (f 1)
```

들여쓰기가 하는 일은 사용자가 생략한 `in`, `;`, `;;`과 블록의 괄호를 되살리는 것뿐이다. 그래서 파서는 verbose syntax 그대로 두고, filter가 offside rule로 그 토큰을 넣는다. F# 컴파일러의 `LexFilter.fs`가 이 방식이다. Python처럼 문법이 `INDENT`/`DEDENT`를 받게 하면 식이 오는 모든 자리의 문법을 다시 써야 하고, 두 문법이 서로 어긋난다.

**구분자는 두 조건이 모두 맞을 때만 넣는다.**

- 새 줄이 블록과 같은 열이다.
- 앞 토큰이 식을 끝낼 수 있고(이름, 리터럴, 닫는 괄호), 새 토큰이 식을 시작할 수 있다.

두 번째 조건은 Go의 세미콜론 규칙과 같다. 이 조건 덕분에 `… in`, `a +`, `let x =`로 끝나는 verbose 코드의 여러 줄이 그대로 이어진다.

## Why This Matters

- **fsyacc가 filter의 남은 토큰을 받지 않는다.** 렉서가 입력 끝에 닿으면 `lexbuf.IsPastEndOfStream`이 켜진다. 그 뒤로 파서는 토큰 함수를 부르지 않고 입력이 끝났다고 본다. 그래서 입력 끝 앞에 넣은 블록 닫기 토큰이 전달되지 않는다. 이 한 줄을 빼면 모든 프로그램이 실패한다.

  ```
  $ calc -e 'let x = 1'          (IsPastEndOfStream 을 되돌리지 않음)
  Error: parse error: unexpected end of input (line 1, column 10)
  ```

- **위치가 밀린다.** fsyacc는 토큰을 받은 뒤 `lexbuf.StartPos`/`EndPos`를 읽어 노드 위치로 쓴다. filter가 토큰을 쌓아 두었다가 내주면 렉서의 위치와 내주는 토큰이 다르다.
- **한 줄 코드의 뜻이 바뀐다.** 블록을 어디서 닫는지가 원래 문법과 조금만 달라도 그렇다. 문법은 `if c then a else b; d`를 `(if …); d`로 읽는데, else 블록이 `;`까지 먹으면 `if c then a else (b; d)`가 된다. 이런 차이는 눈으로 찾을 수 없다.

## Recognition Pattern

- yacc 계열 파서를 쓰는 언어에 Haskell, F#, Python 같은 layout 문법을 더할 때
- 문법을 바꾸지 않고 토큰 열을 고쳐서 해결할 수 있는 문법 기능(자동 세미콜론, 들여쓰기 블록)
- 기존 문법과 새 문법을 함께 받아야 할 때

## The Approach

### Step 1: filter는 토큰 함수를 감싸고, 위치와 입력 끝 표시를 되돌린다

```fsharp
let create (input: string) (tokenize: LexBuffer<char> -> token) : LexBuffer<char> -> token =
    ...
    fun (lexbuf: LexBuffer<char>) ->
        while output.Count = 0 do
            let t = tokenize lexbuf
            processToken { Token = t; Start = lexbuf.StartPos; End = lexbuf.EndPos }
        let tok = output.Dequeue()
        lexbuf.StartPos <- tok.Start
        lexbuf.EndPos <- tok.End
        lexbuf.IsPastEndOfStream <- (tok.Token = EOF)
        tok.Token
```

- **미리 읽지 않는다.** 실제 토큰을 하나 읽을 때마다 그 앞에 넣을 가상 토큰과 그 토큰을 큐에 넣는다. "다음 토큰이 블록의 열을 정한다" 같은 결정은 다음 토큰이 왔을 때 내린다(`pending`). 그래서 큐의 마지막은 늘 방금 읽은 토큰이다. 마지막 토큰을 내줄 때 `EndPos`가 렉서의 실제 위치와 같아진다. 렉서는 다음 토큰의 위치를 `EndPos`에서 이어서 센다.
- **가상 토큰의 위치:** 바로 뒤 실제 토큰의 위치를 준다. parse error가 사용자가 쓴 토큰을 가리킨다.
- **보간 문자열의 구멍:** filter 없이 원래 렉서로 파싱한다(한 줄 식).

### Step 2: context 스택과 블록을 여는 토큰

| context | 쌓는 토큰 | 닫는 것 |
|---|---|---|
| 블록 | `let … =`, `->`, `then`, `else`, `try`, `do` 다음의 첫 토큰 | offside, 짝이 맞는 닫는 토큰 |
| `let` | `let` | `in`, 같은 열의 새 줄(가상 `in`), offside |
| `match` | `match`, `try`, `function` | offside |
| 괄호 | `(`, `[`, `{` | 짝이 맞는 닫는 괄호만 |
| 최상위, 모듈 | 시작, `module M =` | 입력 끝, offside(가상 `end`) |

블록은 가상 괄호로 감싼다. 문법에 더하는 규칙은 하나뿐이다.

```
Primary:
    | OBLOCKBEGIN SeqExpr OBLOCKEND { $2 }       // 괄호처럼 AST 에 남지 않음
```

- **어느 토큰이 블록을 여는지 맥락으로 가린다.** `=`는 맨 위 context가 `=` 앞의 `let`일 때만 블록을 연다. 비교 연산자, `type t =`, 레코드 필드의 `=`와 구분하기 위해서다. `->`는 맨 위가 `fun`이거나 `with` 뒤의 `match`일 때만 연다. 타입 `int -> int`의 `->`와 구분하기 위해서다.
- **`let` 두 가지:** 최상위와 모듈의 `let`은 선언이라 몸이 없어도 된다. 블록 안의 `let`은 같은 열의 새 줄에서 `in`을 받는다. `in` 뒤의 `let`은 최상위여도 식이다.

### Step 3: 새 줄의 첫 토큰을 처리한다

```fsharp
while go do
    let top = stack.Peek()
    match top.Kind with
    | KTop -> (if col = 0 && not (continues t) && separate () then emit SEMISEMI tok); go <- false
    | KParen _ -> (if col = top.Col && not (continues t) && separate () then emit SEMI tok); go <- false
    | KMatch when t = BAR && col >= top.BarCol -> go <- false
    | _ when top.Col > effective -> pop tok                     // offside: 닫는 가상 토큰을 냄
    | _ when top.Col = col && not (continues t) ->
        match top.Kind with
        | KBlock _ -> (if separate () then emit SEMI tok); go <- false
        | KLet when not top.Decl -> (if separate () then (stack.Pop() |> ignore; emit IN tok)); go <- false
        | _ -> pop tok                                          // 선언 let, match 는 끝나고 바깥을 다시 봄
    | _ -> go <- false
```

- **`separate ()`:** 앞 토큰이 식을 끝낼 수 있고 지금 토큰이 식을 시작할 수 있는가.
- **`continues t`:** 줄 머리가 infix 연산자, `|`, `then`, `else`, `with`, `in`, `and`, 닫는 괄호이면 앞 줄을 잇는다. `effective`는 줄 머리의 연산자가 블록보다 "연산자 길이 + 1"만큼 왼쪽에 있어도 블록을 닫지 않게 하는 F#의 허용이다.
- **`BarCol`:** `let f x = match x with` 다음 줄에 들여쓴 `|`를 받기 위한 값이다. 분기의 `|`는 `match`가 있는 줄의 머리까지 왼쪽에 와도 된다. 그 줄이 `|`로 시작하면(안쪽 `match`) 이 허용이 없다. 그래서 바깥 열의 `|`는 안쪽 `match`를 닫는다.

### Step 4: 한 줄에서 블록을 닫는 토큰을 원래 문법에 맞춘다

이 부분이 가장 틀리기 쉽다. 블록은 원래 문법이 식을 끝내는 곳에서 정확히 닫혀야 한다.

| 토큰 | 닫는 것 | 이유 |
|---|---|---|
| `in` | 가장 가까운 `let`까지 | |
| `)` `]` `}` | 짝이 맞는 괄호까지 | |
| `,`, 타입 표기의 `:` | 괄호 안의 블록 | 문법에 `,`, `:`로 이어지는 식이 없음 (`(fun x -> x, 1)`) |
| `\|` | 가장 가까운 `match`의 분기 블록 | 같은 줄이면 안쪽 `match`의 분기 (지금 문법과 같음) |
| `else` | 가장 가까운 `then` 블록 | |
| `with` | `match`/`try`의 머리까지 | 레코드 `{ p with … }`는 괄호가 막음 |
| 같은 줄의 `;` | then, else 블록 | 문법에서 then, else의 몸은 `;`를 받지 않는 `Expr` |
| 같은 줄의 `let`, `type`, `module` | 앞 선언 `let`의 값 | `let x = 1 let y = 2` (한 줄의 선언들) |
| `;;`, 입력 끝 | 최상위까지 모두 | |

- **offside를 먼저 보지 않는 토큰:** `else`, `with`, `in`, 닫는 괄호는 새 줄에 있어도 짝을 직접 찾는다. `if`의 열에 쓴 `else`가 offside로 then 블록을 먼저 닫아 버리면 짝을 잃기 때문이다.
- **같은 줄 규칙은 같은 줄에서만:** 새 줄이면 Step 3이 이미 처리했다. 처음에는 이 조건이 빠져서 블록 안의 두 번째 `let`이 바깥 선언까지 닫았다.

### Step 5: 기존 입력 전부의 AST를 비교한다

```fsharp
for input in EvalTests.cases @ InferTests.inferred @ examples @ [ prelude ] do
    Expect.equal (result Frontend.parse input) (result Frontend.parseVerbose input) input
```

`Frontend.parseVerbose`는 filter 없이 파싱한다. 한 줄 코드의 뜻이 바뀌지 않았다는 것을 이 비교가 보장한다. 새 문법은 "들여쓴 프로그램"과 "같은 뜻의 verbose 프로그램"의 AST를 비교해서 테스트한다.

```fsharp
same "match a with\n| A ->\n    match b with\n    | X -> 1\n    | Y -> 2\n| B -> 3"
     "match a with A -> (match b with X -> 1 | Y -> 2) | B -> 3"
```

이 방법으로 찾은 것:

- 같은 줄 규칙이 새 줄에서도 동작한 버그
- `then`/`else`와 `;`의 버그
- 타입 표기 `:`가 블록을 닫지 않던 것

모두 기존 테스트가 먼저 잡았다.

## Example

```
let area shape =
    let pi = 3
    match shape with
    | Circle r -> pi * r * r
    | Rect (w, h) -> w * h

let total =
    shapes
    |> map area
    |> sum

println total
```

| 모양 | 뜻 |
|---|---|
| 블록과 같은 열의 `let` 다음 줄 | `in` |
| 블록과 같은 열의 새 줄 | `;` |
| 0열의 새 줄 | `;;` |
| 더 들여쓴 새 줄 | 앞 줄을 이음 (`f a`⏎`  b` == `f a b`) |
| `\|>`로 시작하는 줄 | 앞 줄을 이음 |
| `[ 1`⏎`  2 ]` | `[ 1; 2 ]` |
| `while c do`⏎`    body` 다음의 왼쪽 줄 | `done` |

## 체크리스트

- [ ] filter가 토큰을 내줄 때 `lexbuf`의 위치와 `IsPastEndOfStream`을 그 토큰에 맞추는가?
- [ ] 미리 읽지 않아서, 내주는 마지막 토큰의 끝이 렉서의 위치와 같은가?
- [ ] 구분자를 "같은 열 + 앞이 끝남 + 다음이 시작함"일 때만 넣는가?
- [ ] 한 줄에서 블록을 닫는 토큰이 원래 문법이 식을 끝내는 곳과 같은가? (`,`, `:`, then/else의 `;`, 같은 줄의 선언)
- [ ] `else`, `with`, `in`, 닫는 괄호가 offside보다 먼저 짝을 찾는가?
- [ ] 기존 입력 전부를 filter를 켜고 끄고 파싱해서 AST를 비교하는가?
- [ ] 들여쓴 코드 안의 타입 오류와 경고가 제자리를 가리키는가?

## 관련 문서

- `10-handle-nested-match-ambiguity-in-fsyacc.md` - 한 줄의 중첩 `match`는 지금처럼 안쪽
- `58-lex-interpolated-strings-by-reparsing-holes.md` - 구멍을 원래 렉서로 다시 파싱
- `41-track-source-positions-without-changing-ast.md` - 파서가 lexbuf에서 읽는 위치
- `63-implement-modules-as-renaming-after-parsing.md` - `module M =` 다음의 들여쓴 모듈이 펼쳐지는 곳
