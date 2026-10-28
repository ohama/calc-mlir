---
order: 36
created: 2026-10-28
commit: 9ad50fd
version: 0.20.0
description: structural =는 결과뿐 아니라 "어디서 멈추는가"도 기준 구현과 같아야 한다. 비교할 수 없는 값(함수)이 섞이면 검사 순서에 따라 false가 되기도 하고 오류가 나기도 한다. 인터프리터의 패턴 매칭 순서와 F# 라이브러리 함수의 단락 동작을 그대로 옮긴다
---

# 런타임의 structural equality를 기준 구현과 같은 순서로 만들기

> 관련 커밋: [9ad50fd](https://github.com/ohama/calc-mlir/commit/9ad50fd) (v0.20.0)

리스트와 튜플의 `=`는 원소를 차례로 비교하는 간단한 재귀다. 그런데 언어에 **비교할 수 없는 값**(이 언어에서는 함수)이 있으면, 비교 순서가 결과를 바꾼다. 먼저 다른 곳을 만나면 `false`로 끝나고, 먼저 함수를 만나면 오류로 끝난다. 그래서 C 런타임의 `equals`는 인터프리터의 `valueEquals`와 **같은 순서로** 멈춰야 한다.

## The Insight

**"같은 결과"에는 "같은 곳에서 멈춤"이 들어 있다.** 인터프리터의 리스트 비교는 다음과 같다.

```fsharp
| VList xs, VList ys -> List.length xs = List.length ys && List.forall2 valueEquals xs ys
```

이 한 줄에 규칙이 두 개 들어 있다.

1. **길이를 먼저 본다.** 길이가 다르면 원소는 보지도 않는다.
2. **`List.forall2`는 처음 `false`에서 멈춘다.** 그 뒤 원소는 보지 않는다.

그래서 다음 결과가 나온다(인터프리터와 컴파일한 실행 파일 모두 확인함).

| 식 | 결과 | 이유 |
|---|---|---|
| `[fun x -> x] = []` | `false` | 길이가 달라 원소를 보지 않음 |
| `[fun x -> x; fun x -> x] = [fun x -> x]` | `false` | 길이가 달라 원소를 보지 않음 |
| `[(1, fun x -> x)] = [(2, fun x -> x)]` | `false` | 튜플의 첫 원소 `1 ≠ 2`에서 멈춤 |
| `[(1, fun x -> x)] = [(1, fun x -> x)]` | `Error: Type error: ... but got function` | 첫 원소가 같아 함수까지 감 |
| `(fun x -> x, 1) = (fun x -> x, 2)` | `Error: ...` | 첫 원소가 함수 |

타입 추론기는 `=`를 `'a -> 'a -> bool`로 둔다. 그래서 이 프로그램들은 모두 타입 검사를 통과하고, 차이는 실행할 때만 드러난다.

## Why This Matters

- **"자연스러운" C 구현(원소를 끝까지 비교하거나 함수부터 검사)을 쓴 경우:** 위 표의 처음 두 줄이 오류가 됩니다. 인터프리터에서는 되는 프로그램이 컴파일하면 죽습니다.
- **길이 비교를 빼고 원소부터 비교한 경우:** `[fun x -> x] = []`는 그래도 `false`입니다(한쪽이 먼저 끝남). 하지만 `[fun x -> x; fun x -> x] = [fun x -> x]`는 인터프리터에서 `false`인데, 원소부터 비교하면 첫 함수에서 오류가 납니다.
- **이런 차이를 테스트로 잡지 못한 경우:** 평범한 예제(정수 리스트)로는 절대 드러나지 않습니다. "같지 않으면서 함수를 품은" 예제를 일부러 만들어야 합니다.

## Recognition Pattern

- 기준 구현(인터프리터)의 의미를 다른 언어(C 런타임)로 다시 구현할 때
- 연산이 실패할 수 있고(오류, 예외), 실패 여부가 검사 순서에 달려 있을 때
- 기준 구현이 라이브러리 함수(`List.forall2`, `Seq.compareWith`, `&&`)의 단락 동작에 기대고 있을 때

## The Approach

### Step 1: 기준 구현의 검사 순서를 문장으로 적는다

`valueEquals`를 읽고 순서를 적는다.

1. **두 값의 종류를 본다:** 정수, 불리언, 문자열은 바로 비교합니다. 왼쪽이 함수면 오류입니다(마지막 `v, _ -> typeError` 분기).
2. **리스트:** 길이를 비교하고, 같으면 원소를 앞에서부터 비교하다가 처음 `false`에서 멈춥니다.
3. **튜플:** 리스트와 같은 코드를 탑니다(길이는 타입 때문에 항상 같음).

### Step 2: 그 순서대로 C로 옮긴다

```c
static int equals(value a, value b) {
    if (CALC_IS_KIND(a, CALC_KIND_CLOSURE))
        calc_error("Type error: expected int, bool, string, list, tuple or data but got function");
    if (CALC_IS_INT(a) || CALC_IS_BOOL(a)) return a == b;
    ...
    if (a == CALC_NIL || CALC_IS_KIND(a, CALC_KIND_CONS)) {
        if (list_length(a) != list_length(b)) return 0;          /* 1. 길이 먼저 */
        for (; a != CALC_NIL; a = CALC_FIELD(a, 2), b = CALC_FIELD(b, 2))
            if (!equals(CALC_FIELD(a, 1), CALC_FIELD(b, 1))) return 0;   /* 2. 처음 false 에서 멈춤 */
        return 1;
    }
    ...
}
```

리스트를 따라가는 부분은 재귀가 아니라 반복문이다. 그래서 긴 리스트에서도 C 스택을 쓰지 않는다(원소 안으로 들어가는 재귀만 남는다).

### Step 3: 순서가 드러나는 예제를 differential testing에 넣는다

같지 않으면서 함수를 품은 값, 같으면서 함수를 품은 값을 짝으로 넣는다.

```fsharp
Expect.equal (compileAndRun "[fun x -> x] = []") ("false\n", "", 0) "길이가 다르면 원소를 보지 않음"
Expect.equal
    (compileAndRun "[fun x -> x] = [fun x -> x]")
    ("", "Error: Type error: expected int, bool, string, list, tuple or data but got function\n", 1)
    ""
```

### Step 4: 오류 메시지는 기준 구현의 문장을 그대로 쓴다

인터프리터는 `typeError "int, bool, string, list, tuple or data" v`로 메시지를 만든다. 런타임은 그 결과 문장을 상수로 쓴다. 문장이 바뀌면 differential testing이 바로 알려 준다.

## Example

```c
// ❌ BAD: 함수를 먼저 찾아서 오류 → [(1, f)] = [(2, f)] 가 오류가 됨 (인터프리터는 false)
if (contains_closure(a) || contains_closure(b)) calc_error(...);
return deep_equal(a, b);

// ✅ GOOD: 인터프리터와 같은 순서로 비교하다가, 함수에 닿았을 때만 오류
return equals(a, b);
```

## 체크리스트

- [ ] 기준 구현의 검사 순서를 문장으로 적었는가?
- [ ] 리스트는 길이를 먼저 비교하는가?
- [ ] 원소 비교가 처음 `false`에서 멈추는가?
- [ ] 비교할 수 없는 값을 만났을 때만(먼저 찾지 않고) 오류를 내는가?
- [ ] 순서가 드러나는 예제(같지 않으면서 함수를 품은 값)가 테스트에 있는가?

## 관련 문서

- `15-guard-fsharp-core-semantics-in-builtins.md` - FSharp.Core 함수의 동작이 언어 의미가 되는 문제
- `26-write-differential-tests-for-compiler.md` - 오류 예제까지 비교하는 differential testing
- `27-match-interpreter-integer-semantics-in-native-code.md` - 정수 연산의 경계 의미 맞추기
