---
order: 56
created: 2026-10-28
commit: 583d1f6
version: 0.32.0
description: 실행 중 예외를 이름으로 구분하면, 범위가 겹치지 않는 같은 이름의 두 예외 선언도 값이 흘러가서 섞인다. 타입 검사는 둘을 다른 타입으로 보지만 실행 중에는 같은 예외라 soundness가 깨진다. 예외 이름을 프로그램 전체에서 하나로 막는다. 같은 표로 열린 타입 exn의 생성자 인자 타입도 찾는다
---

# 이름으로 구분하는 예외는 이름을 프로그램에 하나로

> 관련 커밋: [583d1f6](https://github.com/ohama/calc-mlir/commit/583d1f6) (v0.32.0)

Calc의 생성자 값은 실행 중에 이름으로 구분한다. 인터프리터는 `VData (typeName, ctor, payload)`의 이름을, 컴파일한 코드는 생성자 이름 전역의 포인터를 비교한다(`38-compile-adt-constructors-with-name-pointers.md`). 예외도 `exn` 타입의 생성자라서 같은 방식으로 만들었다.

처음에는 OCaml처럼 "안쪽에서 같은 이름의 예외를 다시 선언하면 가린다"를 허용하고, 범위 안에 같은 이름이 보일 때만 막았다. 그런데 범위가 겹치지 않는 두 선언에서 구멍이 생겼다.

```
let v = (exception E of int in E 1) in      (* v : exn, 실행 중에는 E 1 *)
exception E of string in                     (* 바깥 E 는 이미 범위 밖이라 "다시 선언"이 아님 *)
match v with E s -> length s | _ -> 0        (* 타입 검사: s 는 string. 실행 중: v 의 이름도 E 라 맞음 → s = 1 *)
```

타입 검사를 건너뛰고 실행하면 실제로 섞인다.

```
$ calc --no-typecheck -e '...위 프로그램...'
Error: Type error: expected string or list but got int
```

인터프리터는 동적 검사가 있어서 오류로 끝난다. 하지만 컴파일한 코드는 검사하지 않으므로, int를 문자열 포인터로 읽게 된다.

## The Insight

**실행 중 identity와 타입 검사의 identity가 같아야 soundness가 지켜진다.** 타입 검사는 두 `E`를 다른 생성자로 본다. 각각 `int -> exn`, `string -> exn`이다. 그런데 실행 중에는 같은 이름이라 같은 생성자다. 둘을 맞추는 방법은 두 가지다.

| 방법 | 하는 일 | 비용 |
|---|---|---|
| 실행 중 identity를 선언마다 따로 | 선언마다 고유한 tag(인터프리터)와 이름 전역(컴파일러) | `Lower`와 `Eval`에서 생성자 이름을 범위에 따라 바꿔야 함. 선언이 함수 안에서 여러 번 평가될 때의 의미(OCaml은 평가마다 다른 예외)도 정해야 함 |
| 이름을 프로그램에 하나로 | 타입 검사가 같은 이름의 두 번째 선언을 거부 | OCaml과 다름. REPL에서 같은 이름을 다시 선언할 수 없음 |

이 프로젝트는 두 번째를 골랐다. 이름이 하나면 이름으로 구분해도 둘이 어긋날 일이 없다.

## Why This Matters

- **"범위 안에 보이면 오류"만 검사한 경우:** 위처럼 값이 범위 밖으로 흘러가는 경우를 놓친다. 타입이 있는 언어에서 생성자 값은 자유롭게 흘러다니므로, 범위만 봐서는 부족하다.
- **처음부터 있는 예외도 포함해야 한다:** `exception Failure of int`를 허용하면, `failwith`가 만든 `Failure "x"`를 사용자의 `Failure n` 분기가 받는다.
- **컴파일한 코드에서는 조용히 틀린다:** 값의 종류를 검사하지 않으므로, 잘못된 포인터를 읽거나 엉뚱한 결과를 낸다.

## Recognition Pattern

- 생성자, 예외, 타입을 **이름으로** 구분하는 런타임을 만들 때
- 같은 이름의 선언을 "가리기"로 허용하는데, 그 선언이 만든 값이 범위 밖으로 나갈 수 있을 때
- 열린 타입(extensible variant, `exn`)을 추가할 때

## The Approach

### Step 1: 프로그램 전체의 예외 표를 추론 상태에 둔다

```fsharp
type private State =
    { ...
      /// 예외 생성자 → 인자 타입. 프로그램 전체에서 이름이 하나
      Exceptions: Dictionary<string, Type option> }

// 처음부터 있는 예외도 표에 넣어 둠
Exceptions = Dictionary(dict [ "Failure", Some tString; "Not_found", None ])
```

범위(`Context`)가 아니라 추론 한 번의 상태(`State`)에 둔다. 범위를 벗어나도 지워지지 않아야 하기 때문이다.

### Step 2: 두 번째 선언을 거부한다

```fsharp
| ExceptionDecl (name, argType, body) ->
    if st.Exceptions.ContainsKey name then typeError (sprintf "Exception %s is already defined" name)
    let arg = argType |> Option.map (convertType ctx noTypeVar)   // 'a 는 오류: 예외는 polymorphic 할 수 없음
    st.Exceptions[name] <- arg
    ...
```

```
Error: Exception E is already defined (line 1, column 40)
  1 | let v = (exception E of int in E 1) in exception E of string in match v with E s -> length s | _ -> 0
    |                                        ^
```

REPL은 지금까지의 선언을 `let … in` 사슬로 붙여 다시 추론하므로(`45-implement-repl-by-reinferring-declarations.md`), 앞 입력의 예외 선언도 같은 표에 들어간다. 그래서 REPL에서도 같은 이름을 다시 선언하면 이 오류가 난다.

### Step 3: 같은 표로 열린 타입의 생성자 인자 타입을 찾는다

`exn`은 선언할 때마다 생성자가 늘어나는 열린 타입이다. 그래서 빠진 분기 검사의 "타입의 생성자 목록"(`signature`)이 없다. `int`처럼 `_`가 있어야 모든 값을 덮는다. 그런데 쓰이지 않는 분기 검사는 `E true` 같은 패턴의 **하위 패턴 타입**이 필요하다. 그 타입을 생성자 목록에서 찾고 있었기 때문에, `exn`이면 하위 타입 목록이 비어서 열 수가 어긋났다. 그러면 모든 `exn` 분기가 "쓰이지 않음"으로 잘못 판정된다.

```fsharp
let argTypes =
    match signature st t, prune t with
    | Some ctors, _ -> ctors |> List.tryFind (fst >> (=) name) |> Option.map snd |> Option.defaultValue []
    | None, TyCon ("exn", []) ->
        match st.Exceptions.TryGetValue name with
        | true, arg -> Option.toList arg
        | _ -> []
    | None, _ -> []
```

이름이 프로그램에 하나이므로 이 표의 조회도 정확하다. 이제 하위 타입을 알아서 다음 분기를 정확히 판정한다.

```
$ calc -e 'exception E of bool in try 1 with E true -> 2 | E false -> 3 | E _ -> 4 | _ -> 5'
Warning: this match case is unused (line 1, column 64)      ← E _ 는 E true, E false 가 덮음
```

### Step 4: 테스트

```fsharp
expectTypeError "exception E in exception E in 1" "Exception E is already defined"
expectTypeError "let v = (exception E of int in E 1) in exception E of string in match v with E s -> s | _ -> \"\"" "Exception E is already defined"
expectTypeError "exception Failure in 1" "Exception Failure is already defined"
```

## Example

| 프로그램 | 결과 |
|---|---|
| `exception E in exception E in 1` | `Exception E is already defined` |
| `let v = (exception E of int in E 1) in exception E of string in …` | `Exception E is already defined` (범위가 겹치지 않아도) |
| `exception Failure of int in 1` | `Exception Failure is already defined` |
| `exception E of 'a in 1` | `Unbound type variable: 'a` |

## 체크리스트

- [ ] 실행 중 구분 방법(이름, 포인터, tag)이 타입 검사의 구분과 같은가?
- [ ] 값이 선언의 범위 밖으로 흘러가는 경우를 생각했는가?
- [ ] 처음부터 있는 예외(`Failure`, `Not_found`)도 다시 선언을 막는가?
- [ ] 열린 타입의 패턴에서 하위 패턴 타입을 찾을 수 있는가? (쓰이지 않는 분기 검사)
- [ ] OCaml과 다른 점(선언마다 다른 예외가 아님, REPL에서 다시 선언 불가)을 문서에 적었는가?

## 관련 문서

- `55-compile-exceptions-with-setjmp-in-c-runtime.md` - 예외의 실행
- `38-compile-adt-constructors-with-name-pointers.md` - 이름 포인터로 생성자 구분
- `49-detect-unused-match-cases-with-usefulness.md` - usefulness와 하위 패턴 타입
