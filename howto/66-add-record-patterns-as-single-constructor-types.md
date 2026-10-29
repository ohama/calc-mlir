---
order: 66
created: 2026-10-29
commit: acd9357
version: 0.40.0
description: 레코드 패턴은 빠진 분기 검사에서 생성자가 하나인 타입이다. 생성자 이름에 필드 이름을 넣어 값의 예를 출력하고, 패턴이 어느 레코드 타입인지는 타입 추론이 노드별로 기록해 둔다. 컴파일러에서는 Lower가 패턴을 선언 순서의 모든 필드로 채워 Codegen이 칸 번호만 쓰게 한다
---

# 레코드 패턴을 생성자 하나인 타입으로 넣기

> 관련 커밋: [acd9357](https://github.com/ohama/calc-mlir/commit/acd9357) (v0.40.0)

레코드(`62-add-records-typed-by-field-names.md`)에 패턴 `{ x = p1; y = p2 }`를 더한다. F#처럼 쓴 필드만 검사한다. 단계마다 할 일은 다음과 같다.

- **파서:** 규칙 하나를 더한다.
- **타입 추론:** 레코드 식과 같은 방법으로 타입을 정한다.
- **빠진 분기 검사와 컴파일러:** 레코드 패턴만의 문제가 각각 하나씩 있다.

## The Insight

**레코드는 생성자가 하나인 ADT다.** 빠진 분기 검사(Maranget usefulness)에서 레코드 타입은 생성자 `record` 하나와, 선언한 순서의 필드 타입들을 하위 타입으로 가진다. 튜플과 같다. 쓰지 않은 필드는 `_`다.

```
{ y = true }        ==>  Con ("record:x;y", [ Any; Con ("true", []) ])
{ x = 0; y = q }    ==>  Con ("record:x;y", [ Con ("int:0", []); toPat q ])
```

생성자 이름에 필드 이름을 넣는다(`record:x;y`). 그러면 검사기가 만든 "빠진 값의 예"를 필드 이름과 함께 출력할 수 있다. 같은 이름이 타입의 signature 쪽에서도 나오므로 두 쪽이 그대로 맞는다.

```
this match is not exhaustive. Example of a value not matched: { x = _; y = false }
```

## Why This Matters

- **패턴만 보고는 필드 순서를 알 수 없다.** `{ y = true }`는 쓴 필드 하나뿐이다. 행렬의 열을 맞추려면 선언한 순서의 모든 필드가 필요하다. 그런데 필드 이름으로 타입을 찾는 것은 범위에 따라 달라진다. 같은 필드 이름을 가진 레코드 타입이 여럿이면 마지막 선언이 이긴다. 빠진 분기 검사는 추론이 끝난 뒤 따로 돌기 때문에 그때는 범위가 없다.

  ```
  type a = { x: int; y: bool } in let f (r: a) = match r with { y = true } -> 1 in
  type b = { x: string; z: bool } in let g r = match r with { z = true } -> 2 in …
  Warning: … Example of a value not matched: { x = _; y = false }
  Warning: … Example of a value not matched: { x = _; z = false }
  ```

- **컴파일러에서도 같은 문제가 있다.** Codegen은 레코드 블록의 칸 번호로 필드를 읽는다(`[헤더, 필드 이름들, 값...]`). 패턴에 쓴 필드 이름만으로는 번호를 모른다.

## Recognition Pattern

- 이름 붙은 필드를 가진 값(레코드, 구조체, named field 생성자)의 패턴을 넣을 때
- 빠진 분기 검사가 패턴 문법만 보고 돌기 때문에 타입 정보가 모자랄 때
- 같은 AST를 쓰는 뒤 단계(검사기, 코드 생성)가 범위 정보 없이 이름을 번호로 바꿔야 할 때

## The Approach

### Step 1: 타입 추론은 레코드 식과 같게, 그리고 노드별로 필드 순서를 기록한다

```fsharp
| PRecord fields ->
    let t, fieldTypes = recordType st ctx (fst fields.Head)        // 첫 필드가 타입을 정함
    st.RecordPatterns[box pattern] <- List.map fst fieldTypes       // 이 패턴 노드의 필드 (선언한 순서)
    let binds =
        fields |> List.collect (fun (f, p) ->
            let tp, binds = inferPattern st ctx p
            locate p (fun () -> unifyAt (fieldOf t fieldTypes f) tp)
            binds)
    t, binds
```

- **기록하는 표:** `RecordPatterns`는 참조로 비교하는 `Dictionary<obj, string list>(HashIdentity.Reference)`다. 이 언어가 오류 위치를 노드 참조로 기록하는 것(`41-track-source-positions-without-changing-ast.md`)과 같은 방법이다.
- **이유:** 타입 추론만 범위를 알므로, 범위가 필요한 결정은 추론이 내리고 노드에 붙여 둔다.
- **레코드 타입 쪽:** 선언할 때 `RecordTypes[고유 이름] <- (타입 매개변수 변수, 필드와 타입)`도 기록한다. signature가 쓴다.

### Step 2: 빠진 분기 검사에서 레코드를 생성자 하나로

```fsharp
// 패턴 → 검사용 패턴
| PRecord fields ->
    let names = st.RecordPatterns[box pattern]
    let sub n = fields |> List.tryFind (fun (f, _) -> f = n) |> Option.map (snd >> toPat) |> Option.defaultValue Any
    Con ("record:" + String.concat ";" names, List.map sub names)

// 타입 → 생성자 전부
| TyCon (name, args) when st.RecordTypes.ContainsKey name ->
    let paramVars, fields = st.RecordTypes[name]
    let subst = substitute paramVars args              // 'a box 의 'a 를 실제 타입으로
    Some [ "record:" + (fields |> List.map fst |> String.concat ";"), fields |> List.map (snd >> subst) ]

// 값의 예 출력
| Con (name, items) when name.StartsWith "record:" ->
    let names = name.Substring(7).Split(';')
    "{ " + (Array.zip names (Array.ofList items) |> Array.map (fun (n, p) -> n + " = " + patToString 0 p) |> String.concat "; ") + " }"
```

`substitute`는 ADT의 signature에 있던 코드를 꺼낸 것이다. type parameter가 있는 레코드(`{ v: 'a }`)에서 `{ v = Some _ }`가 `{ v = None }`을 놓쳤다고 알려면 `'a`를 실제 타입으로 바꿔야 한다. 쓰이지 않는 분기 경고는 같은 검사기를 쓰므로 따로 할 일이 없다.

### Step 3: Lower가 패턴을 선언 순서의 모든 필드로 채운다

```fsharp
let rec private fillRecordFields (scope: Scope) (pattern: Pattern) : Pattern =
    match pattern with
    | PRecord fields ->
        let names = scope.Fields[fst fields.Head]                    // Lower 의 범위가 아는 선언 순서
        PRecord (names |> List.map (fun n -> n, fields |> List.tryFind (fun (f, _) -> f = n)
                                                   |> Option.map (snd >> fillRecordFields scope)
                                                   |> Option.defaultValue PWildcard))
    | PCons (h, t) -> PCons (fillRecordFields scope h, fillRecordFields scope t)
    ...
```

Lower는 레코드 식을 낮출 때 이미 범위(`Scope.Fields`)로 필드 순서를 알고 있다. 패턴도 같은 범위에서 채우면 Codegen은 이름을 볼 필요가 없다.

```fsharp
// Codegen: i 번째 하위 패턴이 칸 2 + i (0 은 헤더, 1 은 필드 이름)
| Ast.PRecord items -> fields (items |> List.mapi (fun i (_, p) -> p, 2 + i))
```

생성자가 하나라서 확인할 태그가 없다. 하위 패턴이 모두 변수나 `_`면 irrefutable이고 검사 코드가 하나도 생기지 않는다. `match { x = 1; y = 2 } with { y = b } -> b`의 MLIR에는 비교(`cmpi`)도 `scf.if`도 없다.

### Step 4: 패턴을 훑는 곳을 모두 고친다

F#의 incomplete match 경고(FS0025)가 고칠 곳을 알려 준다. 이 프로젝트에서는 열 곳이었다.

- 파서의 `patternVars`, `checkOrPatterns`
- 모듈 이름 바꾸기(`Modules.renamePatternWith`, `patternNames`)
- REPL의 `patternNames`
- 평가기 `matchPattern`, 타입 추론 `inferPattern`, 검사기 `toPat`
- `Core.patternVars`
- Codegen의 `irrefutable`, `emitTest`, `emitBind`

Release 빌드를 경고 0개로 두면 하나도 빠뜨리지 않는다.

## Example

```
let describe p =
    match p with
    | { x = 0; y = 0 } -> "origin"
    | { x = 0 } -> "on y axis"
    | { x = a; y = b } when a = b -> "diagonal"
    | _ -> "somewhere"

let { x = px; y = py } = { x = 3; y = 4 }
```

| 입력 | 결과 |
|---|---|
| `match r with { y = true } -> 1` (`y : bool`) | 경고: `{ x = _; y = false }` |
| `match r with _ -> 0 \| { x = 1 } -> 1` | 경고: `this match case is unused` |
| `type 'a b = { v: 'a } in fun r -> match r with { v = Some _ } -> 1` | 경고: `{ v = None }` |
| `match r with { z = 1 } -> 1` | `Unknown record field: z` |
| `match { x = 1; y = false } with { y = true } -> 1` (컴파일) | `Error: Match failure: { x = 1; y = false }` |

## 체크리스트

- [ ] 빠진 분기 검사에서 레코드가 생성자 하나("record:필드들")인가? signature와 패턴 쪽 이름이 같은가?
- [ ] 패턴 노드가 어느 레코드 타입인지(필드 순서)를 타입 추론이 기록하는가? (같은 필드 이름의 타입이 여럿일 때)
- [ ] type parameter가 있는 레코드에서 필드 타입을 실제 타입으로 바꾸는가?
- [ ] Lower가 패턴을 선언 순서의 모든 필드로 채워 Codegen이 칸 번호만 쓰는가?
- [ ] 하위 패턴이 모두 irrefutable이면 검사 코드가 없는가?
- [ ] 패턴을 훑는 모든 곳을 고쳤는가? (Release 빌드 경고 0개)

## 관련 문서

- `62-add-records-typed-by-field-names.md` - 레코드 식과 필드 이름으로 정하는 타입
- `41-track-source-positions-without-changing-ast.md` - 노드 참조로 정보를 붙이는 방법
- `42-check-match-exhaustiveness-with-maranget.md` - 빠진 분기 검사와 signature
- `49-detect-unused-match-cases-with-usefulness.md` - 쓰이지 않는 분기 경고
- `17-implement-pattern-matching-evaluator.md` - 패턴 매칭 평가기
