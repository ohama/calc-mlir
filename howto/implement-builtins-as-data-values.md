---
created: 2026-10-28
description: 내장 함수를 F# 함수 값으로 Value에 넣으면 구조적 equality가 깨진다. 이름과 모은 인자만 저장하는 데이터로 표현하고 호출 시점에 디스패치한다
---

# 인터프리터 내장 함수를 데이터 값으로 구현하기

내장 함수를 `VFn of (Value -> Value)`처럼 F# 함수로 `Value`에 넣고 싶어진다. 하지만 그러면 `Value` 전체가 `=`를 잃는다. 대신 **`VBuiltin of name * args`** 처럼 "어떤 내장 함수에 지금까지 어떤 인자를 줬는지"만 데이터로 저장한다. 인자 수가 다 차면 이름으로 디스패치해서 실행한다.

## The Insight

F# 판별 유니온의 `=`는 **모든 필드가 equality를 지원할 때만** 자동으로 생긴다. 함수 타입은 equality를 지원하지 않는다. 그래서 케이스 하나에 함수를 넣는 순간 `Value` 전체가 비교 불가능한 타입이 된다.

```fsharp
type Value =
    | VInt of int
    | VFn of (Value -> Value)

VInt 1 = VInt 1
// error FS0001: The type 'Value' does not support the 'equality' constraint because it is a
// record, union or struct with one or more structural element types which do not support the
// 'equality' constraint.
```

`VInt`끼리 비교해도 컴파일 오류다. 테스트의 `Expect.equal (calc "1 + 2") (VInt 3)`, 리스트 값 비교, `Map<string, Value>` 비교가 모두 깨진다.

내장 함수에 필요한 정보는 사실 **이름과 지금까지 받은 인자**뿐이다. 실행할 코드는 이름으로 찾으면 된다. 클로저(`VClosure of param * body * env`)도 F# 함수가 아니라 AST와 환경을 데이터로 들고 있다. 같은 방식이다.

## Why This Matters

| 방법 | `=` | 출력/디버깅 | 커링 | 비용 |
|---|---|---|---|---|
| `VFn of (Value -> Value)` | ❌ 컴파일 오류 | `%A`가 `<fun:...>` | 함수를 반환하는 함수로 직접 구현 | 테스트 전부 수정 |
| `[<CustomEquality; NoComparison>]` + `VFn of name * (Value -> Value)` | Equals/GetHashCode 직접 구현 | 이름은 보임 | 위와 같음 | 케이스 추가할 때마다 Equals 수정, 비교 연산 잃음 |
| **`VBuiltin of name * args` (이 문서)** | ✅ 자동 | `VBuiltin ("substr", [VString "abc"; VInt 1])` | 인자를 모으는 일반 규칙 하나 | 이름 → 구현 디스패치 한 곳 |

마지막 방법이면 부분 적용한 값도 테스트에서 그대로 비교할 수 있다.

```fsharp
Expect.equal (calc "substr \"abc\" 1") (VBuiltin ("substr", [ VString "abc"; VInt 1 ])) ""
```

## Recognition Pattern

- 트리 순회 인터프리터에 `print`, `length` 같은 내장 함수를 넣을 때
- 값 타입에 F# 함수를 넣었더니 FS0001(`does not support the 'equality' constraint`)이 났을 때
- 내장 함수마다 `fun a -> VFn (fun b -> ...)`처럼 커링을 손으로 중첩하고 있을 때

## The Approach

"내장 함수 값"을 **실행 코드가 아니라 실행 요청서**로 본다. 요청서에는 함수 이름과 지금까지 모은 인자가 적혀 있고, 인자가 다 모이면 한 곳에서 처리한다.

### Step 1: 값 케이스와 인자 수 표를 만든다

```fsharp
type Value =
    ...
    /// 내장 함수. args 는 지금까지 받은 인자 (커링: 인자가 다 모이면 실행)
    | VBuiltin of name: string * args: Value list

/// 이름과 인자 수
let builtinArity =
    Map.ofList [
        "length", 1
        "substr", 3
        "contains", 2
        ...
    ]
```

### Step 2: 인자가 다 모인 호출을 이름으로 디스패치한다

```fsharp
let callBuiltin name args =
    match name, args with
    | "length", [ VString s ] -> VInt s.Length
    | "length", [ VList items ] -> VInt (List.length items)
    | "length", [ v ] -> typeError "string or list" v
    | "substr", [ s; start; len ] -> ...
    | _ -> failwithf "Unknown builtin: %s" name
```

`match name, args`로 이름과 인자 패턴을 함께 맞추면 타입별 오버로드(`length`가 문자열과 리스트를 모두 받음)도 자연스럽다.

### Step 3: 함수 호출(`apply`)에 인자를 모으는 규칙 하나를 추가한다

```fsharp
and apply fv av =
    match fv with
    | VClosure ... | VRecClosure ... -> ...
    | VBuiltin (name, args) ->
        let args = args @ [ av ]
        if List.length args = builtinArity.[name] then callBuiltin name args
        else VBuiltin (name, args)
    | v -> typeError "function" v
```

- 커링과 부분 적용이 이 규칙 하나로 모든 내장 함수에 생긴다.
- 인자 타입 검사는 **인자가 다 모였을 때** 일어난다. `let f = substr 1 in f 2 3`의 오류는 `f 2 3`에서 난다. 이 동작을 테스트로 고정한다.

### Step 4: 내장 함수를 처음 환경에 넣는다

```fsharp
/// 처음 환경. 내장 함수는 보통 변수처럼 let 으로 가릴 수 있음
let builtins: Env =
    builtinArity |> Map.map (fun name _ -> VBuiltin (name, []))

let eval expr = evalIn builtins expr
```

- 렉서와 파서는 바꾸지 않는다. `length`는 그냥 `IDENT`이고 `Var "length"`다.
- 같은 이유로 `let length = 5 in length`처럼 가릴 수 있다. 키워드로 만들면 이것이 불가능하다.
- `evalIn Map.empty`는 내장 함수 없는 환경으로 남는다. 평가기 단위 테스트에서 환경을 통제할 때 쓸 수 있다.

### Step 5: 표와 디스패치가 어긋나지 않게 테스트한다

`builtinArity`와 `callBuiltin`은 같은 이름 목록을 두 곳에 적는다. 하나만 고치면 `Unknown builtin` 오류나 인자 수 불일치가 생긴다.

```fsharp
test "모든 내장 함수가 builtins 환경에 있음" {
    Expect.equal (builtins |> Map.keys |> Set.ofSeq) (builtinArity |> Map.keys |> Set.ofSeq) ""
}
```

그리고 내장 함수마다 정상 호출 테스트를 하나 이상 둔다. 그래야 `callBuiltin`에 그 이름의 분기가 있는지 확인된다.

### Step 6: 고차 내장 함수에는 `apply`를 인자로 넘긴다

`map f xs`처럼 사용자 함수를 호출하는 내장 함수가 생기면 `callBuiltin`이 `apply`를 불러야 한다. 그런데 F#은 **정의된 순서대로만 이름을 볼 수 있다**. `callBuiltin`은 `evalIn`/`apply`보다 위에 있고, `apply`는 다시 `callBuiltin`을 부른다. 서로를 부르는 순환이다.

| 방법 | 장단점 |
|---|---|
| `callBuiltin`을 `let rec evalIn ... and apply ... and callBuiltin ...` 체인에 넣기 | 동작하지만, 내장 함수 구현 전체가 평가기의 재귀 묶음 안으로 들어가 파일 구조가 섞임 |
| `mutable` 전방 참조 (`let mutable applyRef = fun _ _ -> failwith "..."`를 나중에 채움) | 초기화 순서를 잊으면 실행 중에 실패함 |
| **`apply`를 인자로 받기 (이 프로젝트)** | 순서 문제가 사라지고, 내장 함수 코드는 평가기를 몰라도 됨 |

```fsharp
/// apply 는 map, filter 같은 고차 함수가 사용자 함수를 호출할 때 사용 (evalIn 보다 먼저 정의되므로 인자로 받음)
let callBuiltin (apply: Value -> Value -> Value) name args =
    match name, args with
    ...
    | "map", [ f; xs ] -> VList (list' xs |> List.map (apply f))
    | "filter", [ f; xs ] -> VList (list' xs |> List.filter (fun x -> bool' (apply f x)))
    | "fold", [ f; acc; xs ] -> list' xs |> List.fold (fun acc x -> apply (apply f acc) x) acc

// apply 안에서
    | VBuiltin (name, args) ->
        ...
        if List.length args = builtinArity.[name] then callBuiltin apply name args
```

- `apply f`는 클로저든 다른 내장 함수든 똑같이 호출한다. 그래서 `map upper xs`, `map (substr "abc" 0) [1; 2]`처럼 내장 함수를 넘겨도 동작한다.
- 인자가 여러 개인 함수(`fold`의 `fun acc x -> ...`)는 커링된 값이므로 `apply (apply f acc) x`로 하나씩 적용한다.
- 콜백이 던진 예외가 라이브러리 함수에 감싸지지 않는지 확인한다. 예를 들어 `List.sortWith`는 비교 함수의 예외를 감싼다. 자세한 내용은 `guard-fsharp-core-semantics-in-builtins.md`에 있다.

## Example

```
let first = substr "hello" 0 in first 2          → "he"
let rec map f xs = match xs with [] -> [] | h :: t -> f h :: map f t in
map upper (split "a b" " ")                       → ["A"; "B"]
let length = 5 in length                          → 5
length = length                                   → Type error: expected int, bool, string or list but got function
```

함수끼리의 `=`는 언어 차원에서 오류로 막는다. F# 쪽에서는 비교가 가능하지만, 사용자 언어에서 함수 비교는 의미가 애매하기 때문이다.

## 체크리스트

- [ ] `Value`에 F# 함수 타입 필드가 없다 (`=`가 자동으로 생긴다)
- [ ] 내장 함수는 `이름 + 모은 인자`로 표현한다
- [ ] `apply`에서 인자 수가 차면 실행하고, 아니면 인자를 더한 새 값을 반환한다
- [ ] 내장 함수는 처음 환경의 보통 변수다 (렉서/파서 수정 없음, `let`으로 가릴 수 있음)
- [ ] 인자 수 표와 디스패치의 이름 목록이 같은지 테스트한다
- [ ] 부분 적용, 가리기, 인자 타입 오류가 나는 시점을 테스트로 고정한다
- [ ] 고차 내장 함수는 `apply`를 인자로 받아 사용자 함수와 내장 함수를 똑같이 호출한다

## 관련 문서

- `implement-closures-and-let-rec.md` - 사용자 정의 함수도 F# 함수가 아니라 데이터(클로저)로 표현한 방법
- `use-ordinal-string-apis-for-language-builtins.md` - 문자열 내장 함수에서 .NET API를 쓸 때의 문화권 함정
- `guard-fsharp-core-semantics-in-builtins.md` - FSharp.Core 함수의 오버플로 검사, 예외 감싸기가 언어로 새어 나오는 문제
