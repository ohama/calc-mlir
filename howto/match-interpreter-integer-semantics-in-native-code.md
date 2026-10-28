---
created: 2026-10-28
description: .NET 정수 연산은 정의된 동작(한 바퀴 돌기, 0으로 나누기 예외)이지만 LLVM과 C에서는 같은 연산이 undefined behavior다. arm64는 1/0을 조용히 0으로 낸다. 연산마다 어느 쪽이 정의되어 있는지 확인하고 검사를 직접 넣는다
---

# 네이티브 코드에서 인터프리터(.NET)의 정수 의미 맞추기

F#으로 만든 인터프리터의 정수 연산을 MLIR/LLVM으로 컴파일하면, 연산자 이름은 같아도 **경계에서의 의미가 다르다**. .NET은 모든 경우를 정해 두었는데, LLVM과 C는 몇몇 경우를 "undefined behavior(UB)"으로 남겨 둔다. 그래서 컴파일러가 그 경우를 직접 검사하거나 정의된 연산으로 바꿔야 한다.

| 연산 | .NET (`int`, 기본 unchecked) | LLVM `arith` (i32) | C `int32_t` |
|---|---|---|---|
| `+ - *` overflow | 한 바퀴 돎 | 한 바퀴 돎 (`nsw` 플래그가 없으면) | **UB** |
| `x / 0` | `DivideByZeroException` | **UB** | **UB** |
| `MinValue / -1` | `OverflowException` | **UB** | **UB** |
| `-MinValue` | `MinValue` (한 바퀴) | `0 - x`는 한 바퀴 | **UB** |

## The Insight

**UB는 "아무 결과나 나온다"가 아니라 "실행할 때마다, 최적화 수준마다 다를 수 있다"이다.** 직접 확인한 결과는 다음과 같다(Apple Silicon, arm64).

- **LLVM `arith.divsi`:** 검사 없이 0으로 나누면 `1 / 0`은 **`0`**, `MinValue / -1`은 **`MinValue`** 가 나온다. `-O0`과 `-O2` 모두 오류 없이 종료 코드 0이다. arm64의 `sdiv` 명령은 트랩하지 않기 때문이다. x86의 `idiv`는 이때 예외를 일으켜 SIGFPE로 죽는다(이 기기에서는 확인하지 못했다). 같은 코드가 CPU에 따라 다르게 틀린다.
- **C 함수 `x + 1 < x`:** `x = INT32_MAX`일 때 `-O0`에서는 `1`, `-O2`에서는 `0`을 돌려준다. 컴파일러는 signed overflow가 일어나지 않는다고 가정하고 식을 `false`로 바꿔 버린다.

인터프리터와 결과를 비교하는 테스트가 없었다면 이 둘은 "그럴듯한 숫자"로 지나갔을 것이다.

## Why This Matters

- **조용히 틀린 결과:** `1 / 0`이 오류 없이 `0`으로 출력되면 사용자는 버그를 알아채지 못한다.
- **최적화 수준에 따라 달라지는 결과:** 디버그 빌드에서는 맞고 `-O2`에서만 틀린다. 재현이 어렵다.
- **플랫폼에 따라 달라지는 결과:** 개발 기기(arm64)와 CI(x86)가 다르게 행동한다.

## Recognition Pattern

- 관리형 언어(.NET, JVM)로 만든 인터프리터를 네이티브로 컴파일할 때
- C 런타임 함수에서 사용자 정수를 계산할 때
- `sdiv`/`srem`, 시프트(시프트 양 ≥ 비트 수도 UB), signed overflow가 나오는 모든 곳

## The Approach

**연산마다 "인터프리터의 의미"와 "대상의 의미"를 표로 비교하고, 다른 칸마다 해결책을 정한다.** 해결책은 세 가지 중 하나다.

### Step 1: 인터프리터의 실제 동작을 먼저 측정한다

추측하지 말고 경계값을 넣어 본다.

```
$ calc -e "2147483647 + 1"            Result: -2147483648
$ calc -e "-(-2147483647 - 1)"        Result: -2147483648
$ calc -e "7 / -2"                    Result: -3          (0 쪽으로 버림)
$ calc -e "1 / 0"                     Error: Attempted to divide by zero.
$ calc -e "(-2147483647 - 1) / -1"    Error: Arithmetic operation resulted in an overflow.
```

오류 메시지는 .NET 예외의 `Message` 그대로다. 컴파일한 코드도 이 문장을 똑같이 내야 한다.

### Step 2: 정의된 연산이 있으면 그것을 쓴다 (한 바퀴 돌기)

- **MLIR `arith`:** `arith.addi`, `subi`, `muli`는 `nsw`/`nuw` 플래그를 붙이지 않으면 two's complement으로 한 바퀴 돈다. 32비트 의미가 필요하니 **i32로 내려서 계산하고** 다시 넓힌다. tagged i64에서 바로 더하면 overflow 위치가 달라진다.
- **C 런타임:** 부호 없는 수로 바꿔서 계산한다. 부호 없는 수의 overflow는 C에서도 정의되어 있다.

```c
/* ❌ UB: x + y 가 넘치면 무엇이든 될 수 있음 */
int32_t r = x + y;
/* ✅ 정의됨: 부호 없는 덧셈은 한 바퀴 돌고, 결과를 다시 부호 있는 수로 */
int32_t r = (int32_t)((uint32_t)x + (uint32_t)y);
```

### Step 3: 정의된 연산이 없으면 앞에서 검사한다 (나눗셈)

나누기 전에 두 조건을 검사하고, 맞으면 돌아오지 않는 런타임 함수를 부른다.

```mlir
%isZero = arith.cmpi eq, %y, %c0 : i32
scf.if %isZero {
  llvm.call @calc_error_divide_by_zero() : () -> ()   // "Error: Attempted to divide by zero.", exit(1)
}
%isMin = arith.cmpi eq, %x, %cMin : i32
%isM1  = arith.cmpi eq, %y, %cM1 : i32
%both  = arith.andi %isMin, %isM1 : i1
scf.if %both {
  llvm.call @calc_error_overflow() : () -> ()
}
%q = arith.divsi %x, %y : i32                          // 여기서는 UB 가 될 수 없음
```

`-O2`에서도 LLVM은 `sdiv`를 검사 앞으로 옮기지 않는다. 런타임 함수가 돌아올 수도 있다고 보고, 트랩할 수 있는 나눗셈을 speculative execution을 하지 않기 때문이다. 테스트에서는 `-O2` 빌드가 `1 / 0`과 `MinValue / -1`에서 정확한 메시지와 종료 코드 1을 냈다.

### Step 4: 경계값을 differential testing에 넣는다

평가기 예제는 대부분 평범한 값이다. 경계값과 오류 예제를 따로 목록으로 만들어 인터프리터 결과와 비교한다. stdout, stderr, 종료 코드를 모두 비교한다.

```fsharp
let private runtimeErrorCases =
    [ "1 / 0"; "10 / (5 - 5)"; "(-2147483647 - 1) / -1"; "true && 1 / 0 = 0" ]
```

## Example

```
같은 MLIR(검사 없음), arm64, clang -O0 / -O2:
  1 / 0                 → 0             (오류 없음!)
  MinValue / -1         → -2147483648   (오류 없음!)

같은 C 함수 wraps(x) = x + 1 < x, x = 2147483647:
  -O0 → 1
  -O2 → 0
  -fsanitize=undefined → "runtime error: signed integer overflow"
```

C 런타임을 고칠 때는 `-fsanitize=undefined`로 한 번 돌려 보면 이런 곳을 바로 찾을 수 있다.

## 체크리스트

- [ ] 인터프리터의 경계 동작(overflow, 0으로 나누기, `MinValue / -1`, 음수 나눗셈 방향)을 측정했는가?
- [ ] `arith` 연산에 `nsw`/`nuw`를 붙이지 않았는가? 원래 비트 폭(i32)에서 계산하는가?
- [ ] C 런타임의 정수 계산이 부호 없는 수를 거치는가?
- [ ] 나눗셈 앞에 0과 `MinValue / -1` 검사가 있는가?
- [ ] 오류 메시지가 인터프리터(.NET 예외 메시지)와 글자까지 같은가?
- [ ] 경계값 예제가 differential testing에 있는가?

## 관련 문서

- `represent-values-as-tagged-words.md` - 태그를 떼고 붙이는 법 (음수 시프트도 C에서는 UB)
- `write-differential-tests-for-compiler.md` - 오류 예제까지 비교하는 differential testing
- `guard-fsharp-core-semantics-in-builtins.md` - 반대 방향: FSharp.Core의 의미가 언어로 새는 문제
