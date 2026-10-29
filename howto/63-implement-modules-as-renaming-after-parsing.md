---
order: 63
created: 2026-10-29
commit: b2ae97e
version: 0.37.0
description: 시그니처가 없는 모듈은 이름 공간일 뿐이라, 파싱 직후 M.f 같은 이름의 최상위 선언으로 펼치면 뒤 단계는 바뀌지 않는다. 튜플로 묶으면 value restriction에 걸린다. 한정한 이름은 렉서가 토큰 하나로 만들어야 필드 읽기와 충돌하지 않고, 바꾼 노드에 위치를 옮겨야 오류가 제자리를 가리킨다
---

# 모듈을 파싱 직후의 이름 바꾸기로 구현하기

> 관련 커밋: [b2ae97e](https://github.com/ohama/calc-mlir/commit/b2ae97e) (v0.37.0)

`module M = struct … end`, `M.f`, `open M`을 타입 추론, 인터프리터, 네이티브 컴파일러가 있는 언어에 넣는다. 이 방법을 쓰면 타입 추론, 인터프리터, 컴파일러를 한 줄도 고치지 않는다. 파서 뒤에 이름 바꾸기 단계 하나만 더한다.

## The Insight

**시그니처(`sig … end`)와 functor가 없는 모듈은 이름 공간일 뿐이다.** 모듈 `M` 안의 `let f = …`는 "이름이 `M.f`인 최상위 선언"과 같다. 그렇다면 모듈은 두 가지 이름 바꾸기로 바꿀 수 있다.

- **모듈 안:** 앞에서 선언한 짧은 이름 `f`를 `M.f`로 바꾼다.
- **`open M` 뒤:** `M`의 짧은 이름을 `M.f`로 바꾼다.

```
module M = struct let x = 1 let f y = y + x end;; M.f 2
  ==>  let M.x = 1 in let M.f = fun y -> y + M.x in M.f 2
```

뒤 단계에는 `M.f`가 그냥 이름이다. 점이 들어간 이름은 렉서가 한정한 이름으로만 만들므로, 사용자가 쓴 다른 이름과 겹치지 않는다.

## Why This Matters

모듈을 값으로 바꾸는 쉬운 방법이 둘 있지만 둘 다 틀린다.

- **튜플로 묶기:** `let (M.f, M.r) = (let r = ref 0 in let f x = x in (f, r))`는 모듈 전체가 식 하나다. 그 식에 `ref 0` 같은 함수 호출이 있으면 value restriction(`60-add-value-restriction-to-hm-with-levels.md`)이 모든 멤버의 generalize를 막는다.

  ```
  $ calc -e 'let (id, r) = (let r = ref 0 in let id x = x in (id, r)) in (id 1, id "a")'
  Error: Type mismatch: expected int but got string
  ```

  최상위 선언으로 펼치면 멤버마다 따로 `let`이 되어 `M.id`는 polymorphic하게 남는다.
- **멤버마다 모듈 본문을 반복하기:** 본문의 부수 효과가 멤버 수만큼 실행된다.
- **문법으로 `CTOR DOT IDENT`를 넣기:** 생성자도 `Primary`라서 레코드 필드 읽기 `Primary DOT IDENT`와 충돌한다. fsyacc는 `1 shift/reduce conflicts`를 보고하고 빌드는 그대로 성공시킨다.
- **바꾼 노드에 위치를 옮기지 않기:** 오류가 엉뚱한 곳을 가리킨다. 이 언어는 위치를 노드 참조로 따로 기록하므로(`41-track-source-positions-without-changing-ast.md`) 새로 만든 노드에는 위치가 없다. 다음 예제의 오류는 `y 2`를 가리켜야 하는데, 위치를 옮기지 않으면 바깥 `let g`를 가리킨다.

  ```
  module M = struct let y = 1 let g z = z + y 2 end
  위치를 옮기지 않음:  (line 1, column 29)   ← let g
  위치를 옮김:          (line 1, column 43)   ← y 2
  ```

## Recognition Pattern

- 시그니처, functor 없이 이름 공간만 필요한 모듈이나 namespace를 넣을 때
- 백엔드가 여럿이라 새 개념을 모든 단계에 넣기 부담스러울 때
- AST 노드를 새로 만드는 변환을 넣을 때, 오류 위치를 노드 참조로 기록하는 구조라면

## The Approach

### Step 1: 한정한 이름은 렉서가 토큰 하나로 만든다

```
| (ctor_start ident_char* '.')+ ident_start ident_char*   { QIDENT (lexeme lexbuf) }   // M.f, M.N.g, M.t
| (ctor_start ident_char* '.')+ ctor_start ident_char*    { QCTOR (lexeme lexbuf) }    // M.C
```

- **충돌이 없음:** 가장 긴 일치 덕분에 `M.f`는 `CTOR DOT IDENT`가 아니라 `QIDENT` 하나가 된다. 파서에는 `QIDENT`가 `IDENT`처럼 들어간다.
- **공백 규칙:** 점 앞뒤에 공백이 있으면(`M . f`) 예전처럼 생성자의 필드 읽기다.
- **넣을 자리:** 식(`Var`), 생성자와 그 패턴(`Ctor`, `PCtor`), 타입 표기(`TName`)에 `QIDENT`와 `QCTOR`를 더한다.

### Step 2: 항목을 차례로 처리하며 "짧은 이름 → 전체 이름" 표를 들고 간다

```fsharp
type Env =
    { Values: Map<string, string>        // f → M.f
      Ctors: Map<string, string>         // C → M.C
      Types: Map<string, string>         // t → M.t
      Modules: Map<string, string> }     // N → M.N (모듈 안의 모듈)
```

모듈 안의 선언 하나를 처리하는 순서는 정해져 있다.

1. 값을 지금 표로 바꾼다.
2. 선언을 전체 이름으로 낸다.
3. 그 이름을 표에 더한다.

```fsharp
| TopLet (name, value, pos) ->
    let value = renameExpr env value              // 1. 값은 지금까지의 이름으로
    output.Add(TopLet (addValue name, value, pos))   // 2, 3. M.name 으로 내고, 뒤 항목을 위해 name → M.name
| TopLetRec (name, param, body, pos) ->
    let full = addValue name                       // 재귀: 이름을 먼저 더하고 본문을 바꿈
    output.Add(TopLetRec (full, param, renameExpr (unbind [ param ] env) body, pos))
| TopType (name, typeParams, ctors, pos) ->
    let full = addType name                        // 생성자 인자에 자기 자신(t)을 쓸 수 있음
    ...
```

- **최상위 선언:** 같은 함수로 처리하되 전체 이름이 짧은 이름과 같다. 이때는 표에서 그 이름을 **뺀다.** 그래야 `open M` 뒤에 다시 선언한 `x`가 `M.x`를 가린다.
- **모듈이 끝나면:** 표를 모듈 앞의 것으로 되돌린다. 모듈의 이름표는 따로 보관해서 `open`이 쓰게 한다.

### Step 3: 식 안에서는 묶는 곳마다 이름을 표에서 뺀다

```fsharp
| Var name -> Var (resolveIn env.Values env name)
| Let (name, value, body) -> Let (name, r value, renameExpr (unbind [ name ] env) body)
| Lambda (param, body) -> Lambda (param, renameExpr (unbind [ param ] env) body)
| Match (s, cases) -> Match (r s, arms cases)           // 패턴이 묶는 이름을 분기 안에서 뺌
| TypeDecl (name, _, ctors, body) -> ...                // 식 안의 type 선언은 타입과 생성자 이름을 가림
```

`module M = struct let x = 1 let f x = x end`의 `f` 안의 `x`는 인자다. `M.x`로 바꾸면 안 된다.

`M.N.g`처럼 점이 있는 이름은 첫 모듈 이름만 표(`Modules`)로 바꾼다. 모듈 `M` 안에서 `N.g`라고 쓰면 `M.N.g`가 된다.

### Step 4: `open`은 모듈의 이름표를 지금 표에 합친다

```fsharp
| TopOpen (name, pos) ->
    let path = resolveIn env.Modules env name        // 모듈 안에서는 그 안의 모듈 N 이 M.N
    match Map.tryFind path declared with
    | Some m -> env <- merge env m                     // 뒤의 선언이 다시 가릴 수 있음
    | None -> Errors.syntaxError (sprintf "Unknown module: %s" name) pos
```

처음에는 모듈 이름을 `Map.empty`로 찾아서 모듈 안에서 `open N`이 `Unknown module: N`이 됐다. 점이 없는 모듈 이름도 `Modules` 표에서 찾아야 한다. 최상위 모듈만 테스트하면 이 버그가 보이지 않으므로, 모듈 안의 모듈을 `open`하는 예제를 테스트에 넣는다.

### Step 5: 바꾼 노드에는 원래 위치를 옮긴다

```fsharp
let private keep (old: 'a) (node: 'a) : 'a = Errors.copyPosition (box old) (box node) :?> 'a

keep expr (match expr with
           | Var name -> Var (resolveIn env.Values env name)
           ...)
```

- **위치가 붙는 곳:** 식뿐 아니라 `match` 분기 튜플(쓰이지 않는 분기 경고)과 패턴에도 위치가 붙는다. 이것들도 `keep`으로 감싼다.
- **비용 줄이기:** 표가 비어 있으면 식을 그대로 돌려준다. 모듈이 없는 프로그램은 노드를 하나도 새로 만들지 않는다.

### Step 6: REPL은 모듈 표를 다음 입력으로 넘긴다

REPL 상태에 모듈 scope(지금 표와 선언한 모듈들)를 두고, 입력마다 `Modules.resolve scope items`로 펼친 뒤 처리한다. 입력의 항목이 모두 성공했을 때만 새 scope를 남긴다.

```
# module M = struct let x = 1 let f y = y + x end;;
val M.x : int = 1
val M.f : int -> int = <fun>
# open M;;
# f 5;;
- : int = 6
```

## Example

| 입력 | 펼친 결과 |
|---|---|
| `module M = struct let x = 1 let f y = y + x end` | `let M.x = 1 in let M.f = fun y -> y + M.x` |
| `module M = struct let x = 1 let f x = x end` | `let M.f = fun x -> x` (인자가 가림) |
| `module M = struct type t = A \| B of t end` | `type M.t = M.A \| M.B of M.t` |
| `module M = struct module N = struct let g = 1 end let h = N.g end` | `let M.N.g = 1 in let M.h = M.N.g` |
| `module M = struct let x = 1 end open M let x = 2;; x` | 마지막 `x`는 `x` (다시 선언한 이름) |

출력은 전체 이름으로 나온다. `Stack.Push (1, Stack.Empty)`, `int Stack.t`처럼 보인다.

## 체크리스트

- [ ] 한정한 이름이 렉서에서 토큰 하나인가? (fsyacc 출력에 `conflict`가 없는가)
- [ ] 멤버마다 따로 `let`이 되어 polymorphism이 유지되는가? (`M.id 1, M.id "a"`)
- [ ] 식 안의 `let`, 인자, 패턴, 식 안의 `type`이 모듈 이름을 가리는가?
- [ ] 최상위에서 다시 선언한 이름이 `open`한 이름을 가리는가?
- [ ] 모듈 안에서 그 안의 모듈을 `N.g`와 `open N`으로 쓸 수 있는가?
- [ ] 바꾼 노드, 분기, 패턴에 위치를 옮겼는가? (모듈 안의 타입 오류가 제자리를 가리키는가)
- [ ] REPL이 모듈과 `open`을 다음 입력으로 넘기는가?

## 관련 문서

- `41-track-source-positions-without-changing-ast.md` - 노드 참조로 기록하는 위치
- `60-add-value-restriction-to-hm-with-levels.md` - 튜플로 묶으면 안 되는 이유
- `62-add-records-typed-by-field-names.md` - 필드 읽기 `.`와의 문법 충돌
- `45-implement-repl-by-reinferring-declarations.md` - 입력을 넘는 REPL 상태
