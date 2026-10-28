---
created: 2026-10-27
description: 문자열을 만들어 파서까지 통과시키는 property-based testing은 렉서가 표현할 수 있는 값만 생성해야 한다. FsCheck의 기본 정수 범위는 작아서 경계값은 따로 테스트한다
---

# 파서와 평가기를 FsCheck property-based testing으로 검증하기

언어 구현의 property-based testing은 두 종류다. **AST를 직접 만드는 테스트**와 **소스 문자열을 만들어 렉서·파서를 거치는 테스트**다. 두 번째는 생성한 값이 소스 코드로 표현 가능한지를 따져야 한다.

## The Insight

F# 자체를 **정답(oracle)** 으로 쓴다. "이 언어로 계산한 결과 = F#으로 계산한 결과"가 무작위 입력에서 성립하는지 확인한다.

```fsharp
testProperty "a < b 는 F# 의 < 와 같음" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
    calc (sprintf "%d < %d" a b) = VBool (a < b)
```

문자열을 거치는 테스트에서는 **생성기 → 문자열 → 렉서**의 경로에서 값이 바뀌거나 표현할 수 없게 될 수 있다.

| 생성된 값 | `sprintf "%d"` | 렉서·파서 결과 | 문제 |
|---|---|---|---|
| `5` | `"5"` | `Number 5` | 없음 |
| `-5` | `"-5"` | `Negate (Number 5)` | AST 모양이 다르지만 계산 결과는 같음 |
| `Int32.MinValue` | `"-2147483648"` | **렉서 오류** | `2147483648`이 `int` 범위를 넘음 |

이 언어에는 음수 리터럴이 없다. `-`는 항상 단항 연산자다. 그래서 `-2147483648`은 `MINUS`와 `NUMBER 2147483648`로 나뉘고, `2147483648`을 `int`로 바꾸는 순간 오류가 난다.

```
Value was either too large or too small for an Int32.
```

## Why This Matters

**FsCheck의 기본 정수 범위는 생각보다 작다.** Expecto.FsCheck 11.1.1이 쓰는 FsCheck 3.0.0에서, 평범한 `int` 인자로 20,000번 돌렸을 때 실제로 생성된 범위는 **-100 ~ 99**였다. FsCheck는 "size" 매개변수에 비례해서 값을 만든다. 기본 설정에서는 `Int32.MinValue` 같은 경계값이 나오지 않는다.

그래서 다음 두 가지를 기억해야 한다.

1. 평범한 `int`로 property를 써도 테스트는 **통과한다**. 경계 사례를 실제로 만들어 보지 않았기 때문이다.
2. property-based testing이 통과했다고 해서 **경계값이 안전하다는 뜻은 아니다**. 경계값은 예시 테스트로 따로 확인한다.

## Recognition Pattern

- `sprintf`로 소스 코드를 만들어 `parse`에 넘기는 property-based testing을 쓸 때
- 언어에 음수 리터럴이 없거나, 리터럴의 범위가 호스트 언어 타입과 다를 때
- `NonNegativeInt` 같은 패턴을 썼더니 `error FS0039: The pattern discriminator 'NonNegativeInt' is not defined.`가 날 때

## The Approach

### Step 1: `open FsCheck`를 추가한다

`NonNegativeInt`, `PositiveInt`, `NonEmptyString` 같은 **생성기 수식 타입**은 FsCheck 네임스페이스에 있다. `open Expecto`만으로는 보이지 않는다.

```fsharp
open Expecto
open FsCheck      // 빠지면 FS0039: The pattern discriminator 'NonNegativeInt' is not defined.
```

### Step 2: 렉서가 표현할 수 있는 값만 생성하도록 의도를 드러낸다

리터럴로 쓸 값은 `NonNegativeInt`로 받는다. 그러면 **"이 숫자는 소스에 리터럴로 들어간다"** 는 의도가 코드에 드러난다. 음수가 필요하면 `-%d`로 단항 마이너스를 명시한다.

```fsharp
testProperty "a + b" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
    calc (sprintf "%d + %d" a b) = VInt (a + b)

testProperty "-a" <| fun (NonNegativeInt a) ->
    calc (sprintf "-%d" a) = VInt -a
```

### Step 3: 문자열을 거치지 않아도 되면 AST를 직접 만든다

평가기만 검증할 때는 렉서를 거칠 필요가 없다. 이때는 **모든 `int`** 를 쓸 수 있다.

```fsharp
testProperty "AST 직접 평가: Add" <| fun (a: int) (b: int) ->
    eval (Add (Number a, Number b)) = VInt (a + b)
```

### Step 4: 경계값은 예시 테스트로 고정한다

property-based testing이 만들어 내지 않는 값은 직접 적는다.

```fsharp
test "int 범위를 넘는 리터럴은 렉서 오류" {
    Expect.throws (fun () -> calc "2147483648" |> ignore) ""
}
test "오버플로는 한 바퀴 돈다" {
    Expect.equal (calc "2147483647 + 1") (VInt System.Int32.MinValue) ""
}
```

### Step 5: 실행 횟수를 늘리고 싶으면 설정을 바꾼다

```fsharp
let cfg = { FsCheckConfig.defaultConfig with maxTest = 1000 }   // 기본 100회

testPropertyWithConfig cfg "a < b" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
    calc (sprintf "%d < %d" a b) = VBool (a < b)
```

## Example

불리언과 `if`도 같은 방식으로 F#을 정답으로 쓴다. 불리언은 소스에 넣을 때 F#의 `%b`가 아니라 이 언어의 키워드로 바꾼다. `%b`도 `true`, `false`를 출력하지만, 이 언어의 표기에 의존한다는 것을 드러내려고 직접 바꾼다.

```fsharp
testProperty "&& 와 || 는 F# 와 같음" <| fun (a: bool) (b: bool) ->
    let s (x: bool) = if x then "true" else "false"
    calc (sprintf "%s && %s" (s a) (s b)) = VBool (a && b)
    && calc (sprintf "%s || %s" (s a) (s b)) = VBool (a || b)

testProperty "if 는 조건에 따라 분기" <| fun (c: bool) (NonNegativeInt a) (NonNegativeInt b) ->
    let cond = if c then "true" else "false"
    calc (sprintf "if %s then %d else %d" cond a b) = VInt (if c then a else b)
```

## 체크리스트

- [ ] `open FsCheck`가 있다
- [ ] 문자열을 거치는 property는 렉서가 표현할 수 있는 값만 쓴다 (`NonNegativeInt` 등)
- [ ] 평가기만 볼 때는 AST를 직접 만들어 전체 범위의 값을 쓴다
- [ ] 경계값(최댓값, 최솟값, 오버플로, 0으로 나누기)은 예시 테스트로 따로 확인했다
- [ ] 정답(oracle)으로 쓴 F# 식이 이 언어의 의미와 정말 같은지 확인했다 (예: 정수 나눗셈의 버림 방향)

## 관련 문서

- `setup-expecto-with-dotnet-test.md` - Expecto 테스트 프로젝트 설정
