---
created: 2026-10-28
description: 동적 언어를 네이티브로 컴파일할 때 모든 값을 i64 하나로 두고 하위 3비트로 종류를 표시한다. GC 포인터는 8-byte aligned라 하위 비트가 비어 있다. 태그를 붙이는 쪽은 F#/MLIR, 푸는 쪽은 arithmetic shift다
---

# 모든 값을 tagged 64비트 단어로 표현하기

인터프리터에서는 값이 `Value` 유니언(`VInt`, `VBool`, `VList`, ...)이다. 이것을 네이티브 코드로 컴파일하려면 **모든 값을 같은 크기의 기계어 단어 하나**로 표현해야 한다. 그래야 함수 인자, 리스트 원소, 클로저 환경에 종류를 가리지 않고 넣을 수 있다. 이 프로젝트는 OCaml처럼 `i64` 하나에 **하위 3비트 태그**를 둔다.

| 값 | 표현 | 예 |
|---|---|---|
| int n (32비트) | `(n << 3) \| 1` | `42` → `337`, `-1` → `-7` |
| bool b | `(b << 3) \| 3` | `false` → `3`, `true` → `11` |
| `[]` | `5` | |
| 힙 객체(문자열, cons, 튜플, 클로저 등) | 포인터 그대로 (하위 3비트 `000`) | |

## The Insight

**힙 포인터는 하위 비트가 공짜로 비어 있다.** Boehm GC의 `GC_MALLOC`은 8-byte aligned 주소를 돌려준다(크기 1~64바이트로 10만 번 할당해서 misaligned 주소가 0개였다). 그러면 포인터의 하위 3비트는 항상 `000`이다. 나머지 일곱 패턴은 포인터가 아닌 immediate value(immediate)에 쓸 수 있다.

이렇게 하면 두 가지를 얻는다.

1. **박싱이 없다.** 정수와 불리언은 할당 없이 레지스터에 그대로 들어간다.
2. **런타임이 종류를 안다.** 출력, structural equality(`=`), 디버깅에서 `v & 7`만 보면 된다.

타입 추론을 통과한 프로그램이므로 생성한 코드는 대부분 종류를 검사할 필요가 없다. 태그는 `print`나 `=`처럼 **아무 값이나 받는 런타임 함수**를 위해 있다.

## Why This Matters

- **태그 없이 값을 그대로 넣은 경우:** `calc_print_result(42)`가 받은 `42`가 정수인지 포인터인지 알 수 없다. 타입별로 다른 출력 함수를 만들어야 하고, 리스트 안의 리스트처럼 중첩된 값에서 곧 막힌다.
- **정수를 박싱하는 경우:** `1 + 2` 같은 연산마다 할당이 생긴다.
- **복원에 logical shift를 쓴 경우:** 음수가 깨진다. `-1`은 `-7`로 저장되는데, 이것을 부호 없는 수로 보고 `>> 3`하면 `2305843009213693951`이 나온다(직접 확인함).

## Recognition Pattern

- 타입이 여러 개인 언어를 C 런타임과 함께 네이티브로 컴파일할 때
- 런타임 함수(출력, 비교, 해시)가 값의 종류를 알아야 할 때
- GC가 있고 할당의 alignment이 보장될 때

## The Approach

### Step 1: 태그 배치를 정하고 한 곳에 적는다

컴파일러(F#)와 런타임(C)이 같은 약속을 지켜야 한다. 양쪽에 상수를 두고, 테스트로 고정한다.

```c
/* runtime.h */
typedef int64_t value;
#define CALC_TAG_MASK 7
#define CALC_TAG_INT 1
#define CALC_TAG_BOOL 3
#define CALC_NIL 5
#define CALC_IS_INT(v)  (((v) & CALC_TAG_MASK) == CALC_TAG_INT)
#define CALC_INT_VAL(v) ((int32_t)((v) >> 3))   /* 부호 있는 시프트 */
```

```fsharp
// Codegen.fs
let tagInt (n: int) = (int64 n <<< 3) ||| 1L
let tagBool (b: bool) = if b then (1L <<< 3) ||| 3L else 3L
```

### Step 2: 태그를 붙이는 곳은 F#과 MLIR, C에서는 풀기만 한다

C에서 음수를 왼쪽으로 시프트하는 것은 undefined behavior다. clang도 `-Wshift-negative-value`로 경고한다. 그래서 태그를 붙이는 일은 다음 두 곳에서만 한다. C 런타임은 `>>`로 풀기만 한다.

- **F#:** 상수는 컴파일할 때 계산한다.
- **MLIR:** 실행 중 계산은 `arith.shli`와 `arith.ori`로 한다. 둘 다 two's complement으로 잘 정의되어 있다.

`int64_t`의 `>>`는 C 표준에서 구현 정의지만, clang과 gcc는 arithmetic shift로 처리한다.

### Step 3: 정수 범위를 확인한다

이 언어의 `int`는 32비트이고, 3비트 시프트 후에도 64비트에 여유 있게 들어간다. `Int32.MinValue`와 `MaxValue`가 그대로 돌아오는지 테스트한다.

```
2147483647  -> 17179869177  -> 2147483647
-2147483648 -> -17179869183 -> -2147483648
```

63비트 정수를 원하면 OCaml처럼 태그를 1비트로 줄여야 하고, immediate value 종류가 줄어든다. 이것은 설계에서 고를 일이다.

## Example

```fsharp
// 태그 값을 테스트로 고정: 런타임의 매크로와 어긋나면 바로 드러남
test "값 표현: int 와 bool 의 태그" {
    Expect.equal (Codegen.tagInt 0) 1L ""
    Expect.equal (Codegen.tagInt 5) 41L ""
    Expect.equal (Codegen.tagInt -1) -7L "음수도 산술 시프트로 복원됨"
    Expect.equal (Codegen.tagBool false) 3L ""
    Expect.equal (Codegen.tagBool true) 11L ""
}
```

```c
/* 런타임: 종류를 보고 인터프리터와 같은 형식으로 출력 */
void calc_print_result(value v) {
    if (CALC_IS_INT(v))       printf("%d\n", CALC_INT_VAL(v));
    else if (CALC_IS_BOOL(v)) printf("%s\n", CALC_BOOL_VAL(v) ? "true" : "false");
    else calc_error("calc_print_result: unsupported value");
}
```

## 체크리스트

- [ ] 할당의 alignment이 태그 비트 수 이상인가? (8-byte alignment → 3비트)
- [ ] 복원은 부호 있는 타입(`int64_t`)의 `>>`인가?
- [ ] C에서 음수를 `<<`하지 않는가? (태그는 F#/MLIR에서 붙임)
- [ ] 정수 범위 양 끝이 그대로 돌아오는가?
- [ ] F#과 C의 태그 상수를 테스트로 고정했는가?

## 관련 문서

- `emit-mlir-as-llvm-func-and-lower.md` - 이 값을 다루는 MLIR 생성
- `represent-adt-values-in-dynamic-interpreter.md` - 인터프리터의 value representation
- 이 프로젝트: `docs/compiler-design.md` D3, `src/Calc/runtime/runtime.h`
