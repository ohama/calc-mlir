---
order: 57
created: 2026-10-29
commit: 1663f01
version: 0.33.0
description: sprintf의 타입은 HM의 타입 하나로 쓸 수 없다. 포맷 문자열 리터럴에서 타입을 계산하고, 환경에는 타입 대신 표시를 넣어 리터럴 없이 쓰면 오류로 막는다. 실행은 포맷을 내장 함수만 쓰는 닫힌 코드로 펼쳐 인터프리터와 컴파일러가 함께 쓴다
---

# 포맷 문자열 리터럴로 sprintf의 타입 정하기

> 관련 커밋: [1663f01](https://github.com/ohama/calc-mlir/commit/1663f01) (v0.33.0)

`sprintf "%d is %s"`는 `int -> string -> string`이고, `sprintf "%b"`는 `bool -> string`이다. 인자의 수와 타입이 **문자열의 내용**에 따라 달라진다. HM의 타입 언어로는 이것을 쓸 수 없다.

| 시도한 타입 | 문제 |
|---|---|
| `string -> 'a` | `sprintf "%d" + 1`이 통과한다. 결과가 아무 타입이므로 아무것도 검사하지 않는다 (soundness가 깨짐) |
| `string -> int -> string` 같은 고정 타입 | 포맷마다 다른 인자 수를 받을 수 없다 |

F#과 OCaml은 포맷 문자열 **리터럴**을 타입 검사기가 특별히 다룬다. F#의 `sprintf`는 `Format<'T, ...>`를 받고, 컴파일러는 리터럴을 보고 `'T`를 정한다. Calc도 같은 방법을 쓴다.

## The Insight

**타입은 "함수의 타입"이 아니라 "함수와 리터럴 한 쌍의 타입"이다.** `sprintf` 혼자에는 타입이 없다. `sprintf "<리터럴>"`이 되는 순간 리터럴에서 타입을 계산할 수 있다.

```
sprintf "%d is %s"       ==>  포맷 해석: [Hole int; Text " is "; Hole string]
                         ==>  타입:      int -> string -> string
sprintf "%A"             ==>  %A 마다 새 type variable:  'a -> string
printfn "%d"             ==>  결과가 unit:               int -> unit
```

나머지는 이 생각에서 나온다.

- **`sprintf`가 리터럴 없이 나타나면** 타입을 정할 수 없으므로 오류다.
- **사용자가 `sprintf`라는 이름을 다시 묶으면** 평범한 함수다.
- **실행은 포맷을 해석해서 만든 평범한 함수**다. 리터럴이 정해지면 Calc 코드로 쓸 수 있다.

## Why This Matters

- **`string -> 'a`로 두면** 타입 검사는 조용히 통과하고, 실행 중에 오류가 나거나 컴파일한 코드가 int를 문자열 포인터로 읽는다.
- **리터럴이 아닌 경우를 막지 않으면** `let f = sprintf in f "%d" 1`에서 `f`의 타입을 정할 방법이 없다. 이것을 "알 수 없음"으로 두면 첫 항목과 같은 구멍이 생긴다.
- **이름으로만 특별 취급하면** `let sprintf x = x + 1 in sprintf 2`처럼 사용자가 같은 이름을 정의한 프로그램이 깨진다.

## Recognition Pattern

- 인자의 수나 타입이 **값**(문자열, 숫자)에 따라 달라지는 내장 함수를 HM 언어에 넣을 때 (`printf`, `scanf`, 정규식 그룹, SQL 질의)
- 타입 검사기에서 "이 함수는 리터럴과 함께일 때만 타입이 있다"고 말하고 싶을 때
- 같은 기능을 인터프리터와 컴파일러에 두 번 구현하고 싶지 않을 때

## The Approach

### Step 1: 포맷 문자열을 조각으로 해석한다 (한 곳에서)

```fsharp
type Spec = SInt | SString | SBool | SAny
type Piece = Text of string | Hole of Spec

/// "%d is %s%%" → [Hole SInt; Text " is "; Hole SString; Text "%"]. 잘못된 지정자는 Error 메시지
let parse (format: string) : Result<Piece list, string> = ...
```

타입 추론, 인터프리터, Lower가 모두 이 함수를 쓴다. 지정자의 뜻이 세 곳에서 어긋나지 않는다.

### Step 2: 리터럴과 함께 나타나면 타입을 계산한다

```fsharp
let private formatType (newVar: unit -> Type) (kind: string) (format: string) =
    match Format.parse format with
    | Error message -> raise (TypeError (message, None))
    | Ok pieces ->
        let argTypes =
            pieces |> List.choose (function
                | Format.Hole Format.SInt -> Some (TyCon ("int", []))
                | Format.Hole Format.SString -> Some (TyCon ("string", []))
                | Format.Hole Format.SBool -> Some (TyCon ("bool", []))
                | Format.Hole Format.SAny -> Some (newVar ())     // %A: 아무 값
                | Format.Text _ -> None)
        let result = if kind = "sprintf" then TyCon ("string", []) else TyCon ("unit", [])
        List.foldBack (fun a acc -> TyFun (a, acc)) argTypes result
```

`infer`에서 `App (Var "sprintf", Str format)`을 만나면 `sprintf`를 instantiate하지 않고 이 타입을 쓴다. 나머지 인자는 평범한 함수 적용이므로, 인자의 타입이 틀리면 보통의 unification 오류가 그 인자를 가리킨다.

```fsharp
| App (Var name, (Str format as literal)) when Map.tryFind name ctx.Vars |> Option.exists isFormatMarker ->
    locate literal (fun () -> formatType (fun () -> newVar st) name format)
```

`locate literal`로 감싸서 `%q` 같은 지정자 오류는 리터럴을 가리키게 한다.

```
Error: Unknown format specifier: %q (line 1, column 9)
  1 | sprintf "%q" 1
    |         ^
Error: Type mismatch: expected int but got string (line 1, column 14)
  1 | sprintf "%d" "x"
    |              ^
```

### Step 3: 처음 환경에는 타입 대신 표시를 넣는다

`sprintf`가 리터럴 없이 나타난 것을 알아채고, 사용자가 이름을 가린 것과 구분해야 한다. 이름 비교(`name = "sprintf"`)로는 구분할 수 없다. 그래서 처음 환경에 **타입 자리에 표시**를 넣는다.

```fsharp
let private formatMarker kind = TyCon ("$format:" + kind, [])   // 사용자가 쓸 수 없는 이름

// 처음 환경
|> fun vars -> Format.kinds |> Seq.fold (fun acc kind -> Map.add kind (formatMarker kind) acc) vars

// Var 를 instantiate 할 때
| Some scheme when isFormatMarker scheme ->
    typeError (sprintf "The format string of %s must be a string literal" name)
```

- `let sprintf x = x + 1 in …`은 환경의 `sprintf`를 보통 타입으로 덮어쓴다. 표시가 사라지므로 Step 2의 `when`도, 위의 오류도 걸리지 않는다. 가리기를 위해 따로 할 일이 없다.
- 표시는 타입 언어에 없는 이름이라 unify될 일이 없다. 표시가 `Var`를 지나 다른 곳으로 흘러가지 않으므로, 생긴다면 그것은 추론기의 버그다.

### Step 4: 실행은 닫힌 Calc 코드로 펼친다

리터럴이 정해지면 그 함수는 평범한 Calc 코드로 쓸 수 있다.

```fsharp
///   sprintf "%d is %s" == fun $f0 -> fun $f1 -> toString $f0 + " is " + $f1
///   printfn "%d"       == fun $f0 -> println (toString $f0)
let expand (kind: string) (pieces: Piece list) : Expr = ...
```

인터프리터와 Lower는 이 식을 실행하기만 한다. C 런타임에는 새 함수가 없다.

```fsharp
// Eval: 내장 함수 환경(builtins)에서 평가
and private formatFunction kind format =
    match Format.parse format with
    | Ok pieces -> evalIn builtins (Format.expand kind pieces)
    | Error message -> failwith message          // --no-typecheck 일 때만 옴

// Lower: 사용자 이름이 없는 범위(Set.empty)에서 낮춤
| Printf (kind, format) ->
    match Format.parse format with
    | Ok pieces -> lower state ctors Set.empty (Format.expand kind pieces)
```

**펼친 식은 사용자의 범위 밖에서 실행해야 한다.** 그렇지 않으면 `let toString x = "no" in sprintf "%d!" 3`에서 사용자의 `toString`을 부른다. 인터프리터는 처음 환경(`builtins`)에서 평가하고, Lower는 빈 범위에서 낮춘다. `$f0` 같은 매개변수 이름은 `$`로 시작해서 사용자 이름과 겹치지 않는다.

### Step 5: 리터럴이 붙은 자리를 각 단계가 알아본다

파서는 `sprintf "%d" 1`을 평범한 `App (App (Var "sprintf", Str "%d"), Number 1)`로 만든다. 단계마다 이 모양을 알아본다.

| 단계 | 알아보는 모양 | 하는 일 |
|---|---|---|
| Infer | `App (Var k, Str f)`, `k`의 환경 값이 표시 | `formatType` |
| Lower | 적용 사슬의 머리가 내장 `k`, 첫 인자가 `Str f` | `Printf (k, f)`에 나머지 인자를 적용 |
| Eval | 값 `VBuiltin (k, [])`에 `VString f` 적용 | `formatFunction` |

Eval은 값으로 처리하므로 `--no-typecheck`에서 `let f = sprintf in f "%d" 1`도 실행된다. 타입 검사가 그 프로그램을 막을 뿐이다. 보간 문자열은 파서가 바로 `Printf ("sprintf", f)` 노드를 만든다(`58-lex-interpolated-strings-by-reparsing-holes.md`).

## Example

```
$ calc -e 'sprintf "%d is %s"'
Type:   int -> string -> string
$ calc -e 'let f x = sprintf "[%A]" x in (f 1, f "a")'
Type:   string * string
Result: ("[1]", "[a]")
$ calc -e 'map (sprintf "<%d>") [1; 2]'
Result: ["<1>"; "<2>"]
$ calc -e 'let fmt = "%d" in sprintf fmt 1'
Error: The format string of sprintf must be a string literal (line 1, column 19)
$ calc -e 'let sprintf x = x + 1 in sprintf 2'
Result: 3
$ calc -e 'let toString x = "no" in sprintf "%d!" 3'
Result: "3!"
```

마지막 예제는 컴파일한 실행 파일에서도 `"3!"`이다.

## 체크리스트

- [ ] 리터럴 없이 쓰인 포맷 함수를 타입 오류로 막는가? (`string -> 'a` 같은 타입으로 통과시키지 않는가?)
- [ ] 사용자가 같은 이름을 정의하면 평범한 함수가 되는가?
- [ ] `%A`마다 **새** type variable을 만드는가? (`sprintf "%A %A" 1 "a"`)
- [ ] 지정자 오류는 리터럴을, 인자 오류는 그 인자를 가리키는가?
- [ ] 펼친 코드가 사용자가 가린 `toString`, `print`를 부르지 않는가?
- [ ] 포맷 해석을 한 곳(`Format.parse`)에 두고 모든 단계가 쓰는가?

## 관련 문서

- `58-lex-interpolated-strings-by-reparsing-holes.md` - `$"..."`를 같은 `Printf` 노드로
- `20-implement-hindley-milner-with-levels.md` - instantiate와 type variable
- `41-track-source-positions-without-changing-ast.md` - `locate`로 오류 위치 정하기
- `52-write-standard-library-as-prelude-in-the-language.md` - 기능을 언어 코드로 써서 두 백엔드가 함께 쓰기
