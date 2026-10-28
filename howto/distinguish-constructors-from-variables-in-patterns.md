---
created: 2026-10-28
description: 패턴의 이름 하나는 생성자일 수도 변수일 수도 있다. 대소문자로 렉서에서 구분하고, 모르는 생성자는 오류로 만들어서 오타가 "모든 값과 일치"로 바뀌는 것을 막는다
---

# 패턴에서 생성자와 변수 구분하기

패턴 `match s with Empty -> ...`의 `Empty`는 두 가지로 읽힐 수 있다. **생성자 `Empty`와 일치**하거나, **아무 값이나 받아서 `Empty`라는 변수에 묶거나**. 구분하지 않으면 생성자 이름의 오타가 "모든 값과 일치하는 분기"가 되고, 그 뒤 분기는 영영 실행되지 않는다. 이름의 첫 글자로 렉서에서 구분하고, 모르는 생성자는 불일치가 아니라 오류로 만든다.

## The Insight

F#은 이 구분을 **이름 조회**로 한다. 패턴의 대문자 이름이 보이는 생성자면 생성자 패턴이고, 아니면 변수다. 그래서 오타는 변수가 된다. `dotnet fsi`로 확인한 결과다.

```fsharp
type Shape = Circle of int | Square of int | Empty
let describe s =
    match s with
    | Circle r -> "circle"
    | Emtpy -> "empty"        // 오타: Empty
    | Square w -> "square"
describe (Square 3)
```

```
warning FS0049: Uppercase variable identifiers should not generally be used in patterns,
                and may indicate a missing open declaration or a misspelt pattern name.
warning FS0026: This rule will never be matched
empty
```

경고 두 개만 나오고 실행된다. `Square 3`을 넣었는데 결과는 `"empty"`다. 경고를 놓치면 버그가 조용히 남는다.

## Why This Matters

동적 타입 인터프리터에는 F#의 컴파일 경고조차 없다. 같은 규칙(이름 조회)으로 구현하면 이렇게 된다.

- `Emtpy` 분기는 모든 값과 일치한다. 경고 없이 `"empty"`를 반환한다.
- 아래의 `Square` 분기는 절대 실행되지 않는다.
- 같은 이름이 다른 스코프에서는 생성자, 여기서는 변수가 될 수 있다. 같은 패턴 코드의 의미가 주변 선언에 따라 바뀐다.

## Recognition Pattern

- 언어에 생성자(ADT, enum)와 패턴 매칭을 함께 넣을 때
- 패턴 문법에서 `IDENT`가 변수 패턴과 인자 없는 생성자 패턴 둘 다로 쓰일 때
- "어떤 분기가 절대 실행되지 않는다"는 버그 보고를 받았을 때

## The Approach

**두 단계로 막는다.** 렉서는 첫 글자로 종류를 정하고, 평가기는 생성자 패턴이 실제로 있는 생성자인지 확인한다.

### Step 1: 첫 글자로 토큰을 나눈다 (OCaml 방식)

```
// 소문자나 _ 로 시작하면 변수, 대문자로 시작하면 생성자 (OCaml 과 같은 구분)
let ident_start = ['a'-'z'] | '_'
let ctor_start = ['A'-'Z']

    | ident_start ident_char*
                    { IDENT (lexeme lexbuf) }
    | ctor_start ident_char*
                    { CTOR (lexeme lexbuf) }
```

이제 파서는 이름을 조회하지 않고도 안다.

```
AtomPattern:
    | IDENT                         { PVar $1 }          // 항상 변수
    | CTOR                          { PCtor($1, None) }  // 항상 생성자
```

- 대문자로 시작하는 이름은 `let`이나 `fun`으로 묶을 수 없게 된다(`let X = 1 in X`는 parse error). 이건 의도한 제약이다.
- 기존 코드나 테스트에 대문자 변수가 있으면 깨진다. 이 프로젝트에서는 렉서 테스트의 `X2` 하나가 깨져서 고쳤다.

### Step 2: 생성자 패턴은 지금 보이는 생성자인지 확인한다

렉서 구분만으로는 `Emtpy`가 `CTOR`이 될 뿐, 없는 생성자라는 사실은 모른다. 이걸 그냥 "불일치"로 처리하면 그 분기는 조용히 죽은 코드가 된다. 오류로 만든다.

```fsharp
| PCtor (name, argPattern), v ->
    // 오타 난 생성자가 조용히 "불일치"가 되지 않도록, 지금 보이는 생성자인지와 인자 유무를 먼저 확인
    let typeName =
        match Map.tryFind name env, argPattern with
        | Some (VData (t, _, None)), None -> t
        | Some (VConstructor (t, _, _)), Some _ -> t
        | Some (VData _), Some _ -> failwithf "Constructor %s takes no argument" name
        | Some (VConstructor _), None -> failwithf "Constructor %s requires an argument" name
        | _ -> failwithf "Unknown constructor: %s" name
    match v, argPattern with
    | VData (t, c, None), None when t = typeName && c = name -> Some env
    | VData (t, c, Some payload), Some p when t = typeName && c = name -> matchPattern p payload env
    | _ -> None
```

- 인자 유무도 함께 확인한다. `Some -> ...`(인자 빠짐), `None x -> ...`(인자 있음)도 오타와 같은 종류의 실수다.
- **타입 이름까지 비교**한다. 안쪽 `type`이 같은 이름의 생성자를 다시 선언하면, 바깥 타입의 값은 안쪽 생성자 패턴과 일치하지 않는다.

### Step 3: 한계를 알고 문서에 적는다

이 언어에서 같은 예를 실행한 결과다.

```
match Square 3 with Circle r -> "circle" | Emtpy -> "empty" | Square w -> "square"
→ Error: Unknown constructor: Emtpy

match Circle 3 with Circle r -> "circle" | Emtpy -> "empty" | Square w -> "square"
→ Result: "circle"        (Emtpy 분기에 도달하지 않아서 오타를 모름)

match Square 3 with Circle r -> "circle" | emtpy -> "empty" | Square w -> "square"
→ Result: "empty"         (소문자 오타는 정상적인 변수 패턴)
```

- **검사는 그 분기에 도달했을 때** 한다. 앞 분기에서 일치하면 오타를 발견하지 못한다. 실행 전에 잡으려면 `type` 선언과 패턴을 정적으로 대조하는 검사(또는 타입 검사기)가 필요하다.
- **소문자 오타는 막을 수 없다.** 그건 문법상 변수다. 정적 검사기가 있다면 "이 분기 뒤의 분기는 절대 실행되지 않음"(F#의 FS0026)을 경고할 수 있다.

## Example

```fsharp
test "모르는 생성자 패턴은 불일치가 아니라 오류" {
    expectError "match Some 1 with Nome -> 0 | _ -> 1" "Unknown constructor: Nome"
    expectError "match Some 1 with Some -> 0 | _ -> 1" "Constructor Some requires an argument"
    expectError "match None with None 1 -> 0 | _ -> 1" "Constructor None takes no argument"
}
test "대문자 이름은 변수로 묶을 수 없음" {
    expectParseError "let X = 1 in X" "parse error: unexpected 'X'" 1 5
}
// 안쪽 type 선언의 같은 이름 생성자는 다른 타입이라 일치하지 않음
"type t = A in let v = A in type u = A in match v with A -> 1 | _ -> 2", VInt 2
```

| 방식 | 오타 `Emtpy` | 비용 |
|---|---|---|
| 이름 조회로 구분 (F#) | 변수가 되어 모든 값과 일치. 컴파일 경고만 | 대문자 변수 허용 |
| **대소문자 + 모르는 생성자 오류 (이 프로젝트)** | 그 분기에 도달하면 오류 | 대문자 변수 금지, 검사는 실행 시점 |
| 대소문자 + 정적 검사 (OCaml) | 컴파일 오류 `Unbound constructor` | 타입 검사기 필요 |

## 체크리스트

- [ ] 렉서가 첫 글자로 변수(`IDENT`)와 생성자(`CTOR`)를 나눈다
- [ ] 패턴의 `IDENT`는 항상 변수, `CTOR`는 항상 생성자다
- [ ] 생성자 패턴은 지금 보이는 생성자인지, 인자 유무가 맞는지 확인하고 아니면 오류를 낸다
- [ ] 생성자 일치는 생성자 이름과 타입 이름을 모두 비교한다
- [ ] 실행 시점 검사의 한계(도달하지 않은 분기, 소문자 오타)를 문서에 적었다

## 관련 문서

- `represent-adt-values-in-dynamic-interpreter.md` - `VData`, `VConstructor` value representation
- `implement-pattern-matching-evaluator.md` - `matchPattern`의 전체 구조
- `order-fslex-keywords-before-identifiers.md` - 렉서 규칙 순서와 가장 긴 일치
