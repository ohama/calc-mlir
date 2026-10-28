---
created: 2026-10-28
description: 이미 테스트가 많은 AST에 위치 필드를 넣으면 구조 비교 테스트가 모두 깨진다. 파서가 노드 참조를 키로 하는 ConditionalWeakTable에 시작 위치를 기록하면 AST는 그대로 두고 타입 오류에 위치를 붙일 수 있다. 값이 하나뿐인 노드와 fsyacc ResultRange의 시작 위치를 조심한다
---

# AST를 바꾸지 않고 소스 위치 기록하기

타입 오류에 줄과 열을 붙이려면 타입 추론기가 "이 식이 소스의 어디에 있었나"를 알아야 한다. 정석은 AST 노드마다 위치 필드를 두는 것이다. 하지만 이 프로젝트에는 AST를 `Expect.equal`로 비교하는 테스트가 수백 개 있었다. 위치를 넣으면 테스트의 기대값마다 위치를 적어야 한다.

그래서 위치를 **AST 밖의 표**에 둔다. 키는 노드 객체 자체(참조)다.

```fsharp
// Errors.fs
let private positions = System.Runtime.CompilerServices.ConditionalWeakTable<obj, int * int>()

let recordPosition (node: obj) (pos: Position) =
    positions.AddOrUpdate(node, (pos.Line + 1, pos.Column + 1))

let positionOf (node: obj) : (int * int) option =
    match positions.TryGetValue node with
    | true, p -> Some p
    | _ -> None
```

## The Insight

**F# 유니온 노드는 참조 타입이라서, 같은 모양의 노드도 서로 다른 객체다.** 노드 객체를 키로 쓰면 structural 같음(`=`)과 상관없이 "바로 이 노드"의 위치를 찾을 수 있다. `ConditionalWeakTable`을 쓰는 이유는 두 가지다.

- **키를 붙잡지 않습니다:** AST가 버려지면 기록도 함께 사라집니다. 파싱을 많이 하는 테스트에서도 메모리가 쌓이지 않습니다.
- **스레드에 안전합니다:** Expecto가 테스트를 병렬로 돌려도 됩니다.

**단, 값이 하나뿐인 노드는 키가 될 수 없다.** 인자 없는 유니온 경우(`Nil`, `PNil`, `PWildcard`)는 F#이 정적 싱글턴 하나를 공유한다(직접 확인함).

```
obj.ReferenceEquals(Nil, Program.parse "[]")          → true   (모든 [] 가 같은 객체)
obj.ReferenceEquals(Bool true, Bool true)             → false  (인자가 있으면 새 객체)
PNil, PWildcard                                       → 싱글턴
```

여기에 위치를 기록하면 마지막에 파싱한 `[]`의 위치가 모든 `[]`의 위치가 된다. 그래서 이 노드들은 기록하지 않는다. 오류가 나면 위치가 있는 바깥 식이 대신 가리킨다.

## Why This Matters

- **AST에 위치 필드를 넣은 경우:** 파서 테스트의 기대값이 모두 바뀌고, `%A` 출력(사용자에게 보여 주는 `AST:` 줄)에도 위치가 섞입니다.
- **싱글턴 노드에 기록한 경우:** 오류 위치가 엉뚱한 곳을 가리킵니다. 같은 프로그램 안의 다른 `[]`일 수도 있고, 다른 테스트가 파싱한 소스의 위치일 수도 있습니다.
- **일반 `Dictionary`를 쓴 경우:** 키가 계속 살아 있어서 메모리가 늘고, 병렬 테스트에서 race condition이 생깁니다.

## Recognition Pattern

- 이미 쓰고 있는 AST에 위치, 타입, 주석 같은 **부가 정보**를 붙이고 싶을 때
- AST 구조 비교 테스트가 많을 때
- 정보가 일부 단계(오류 보고)에서만 필요할 때

## The Approach

### Step 1: 파서 액션에서 노드를 만들 때 기록한다

fsyacc 헤더에 도우미를 두고, 노드를 만드는 액션을 감싼다.

```fsharp
let at (parseState: FSharp.Text.Parsing.IParseState) (node: 'a) : 'a =
    Errors.recordPosition (box node) (parseState.InputStartPosition 1)
    node
```

```
| Expr PLUS Expr                { at parseState (Add($1, $3)) }
| IF Expr THEN Expr ELSE Expr   { at parseState (If($2, $4, $6)) }
| LPAREN Expr RPAREN            { $2 }               // 새 노드가 아님: 안쪽 위치 그대로
| LBRACKET RBRACKET             { Nil }              // 싱글턴: 기록하지 않음
```

이 프로젝트에서는 스크립트로 식·패턴 규칙의 액션 43곳을 한 번에 감쌌다. `$1`, `$2`(감싸기만 하는 규칙)와 싱글턴은 건너뛰었다.

### Step 2: 시작 위치는 `InputStartPosition 1`로 (ResultRange 함정)

처음에는 규칙 전체의 범위인 `parseState.ResultRange`의 시작을 썼다. 그런데 `match Some 1 with ...`의 위치가 1열(`match`)이 아니라 7열(`Some 1`)로 나왔다. `let (a, 1) = ...`도 1열이 아니라 5열이었다. 키워드 토큰으로 시작하는 규칙에서 시작 위치가 그다음 기호를 가리켰다(fsyacc 내부의 원인은 확인하지 않았다). 첫 기호의 시작 위치인 `parseState.InputStartPosition 1`로 바꾸니 맞았다.

### Step 3: 파서가 한 번에 여러 노드를 만들면 위치를 나눠 준다

`[1; true]`는 `makeList`가 `Cons(1, Cons(true, Nil))`를 만든다. 규칙의 `at`은 바깥 `Cons`에만 기록하므로, `true`에서 난 오류가 리스트 시작을 가리켰다. 안쪽 cons에는 그 원소의 위치를 옮겨 적는다.

```fsharp
let makeList (items: Expr list) =
    List.foldBack (fun item acc -> Errors.copyPosition (box item) (box (Cons(item, acc))) :?> Expr) items Nil
```

### Step 4: 추론기는 가장 안쪽의 위치를 붙이고, 문제가 된 식을 직접 가리킨다

위치가 없는 오류에는 가장 안쪽 식의 위치를 붙인다. 바깥 식은 이미 위치가 있는 오류를 그대로 통과시킨다.

```fsharp
let private locate (node: obj) (action: unit -> 'a) : 'a =
    try action ()
    with TypeError (message, None) -> raise (TypeError (message, Errors.positionOf node))

let rec private infer st ctx expr = locate expr (fun () -> inferNode st ctx expr)
```

unification이 실패한 자리가 아니라 **문제가 된 식**을 가리키게 하려면, 그 식을 알 때 직접 넘긴다. OCaml도 이렇게 한다.

```fsharp
let expect (e: obj) expected actual = locate e (fun () -> unifyAt expected actual)
| App (f, arg) -> ... expect arg param ta        // f true 의 true
| If (c, t, e) -> expect c tBool (infer' c) ... expect e t (infer' e)   // 조건, else 분기
```

### Step 5: 메시지와 위치를 분리한다

`TypeError`에 `position` 필드를 더하되 `Message`에는 넣지 않는다. 기존 메시지 테스트는 그대로 통과하고, 위치는 `calc`가 출력할 때 파서 오류와 같은 형식으로 붙인다.

```
Error: Type mismatch: expected int but got bool (line 2, column 5)
  2 |   f true
    |     ^
```

## Example

```fsharp
let e = Program.parse "let x = 1 in\n  x + 2"
// Let 노드 (1, 1), 값 1 (1, 9), 본문 x + 2 (2, 3)
```

이 변경 뒤에도 기존 테스트 706개가 하나도 바뀌지 않고 통과했다.

## 체크리스트

- [ ] 키가 참조 비교인가? (`ConditionalWeakTable`)
- [ ] 값이 하나뿐인 노드(인자 없는 유니온 경우)를 기록하지 않는가?
- [ ] 시작 위치를 첫 기호(`InputStartPosition 1`)에서 읽는가?
- [ ] 파서가 한 규칙에서 여러 노드를 만들면 안쪽 노드에도 위치를 주는가?
- [ ] 추론기가 가장 안쪽 위치를 붙이고, 문제가 된 식을 직접 가리키는가?
- [ ] 메시지 문자열과 위치가 분리되어 기존 테스트가 그대로인가?

## 관련 문서

- `report-parse-error-positions-with-fsyacc.md` - 파서 오류의 위치 (lexbuf 의 StartPos/EndPos)
- `implement-hindley-milner-with-levels.md` - 위치를 붙이는 대상인 타입 추론기
