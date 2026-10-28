---
created: 2026-10-28
description: 컴파일한 재귀 함수는 기본 8MB 스택에서 깊이 100만이면 죽는다. macOS에서는 -Wl,-stack_size로 메인 스레드 스택을 키워 인터프리터의 큰 스택 스레드와 한계를 맞춘다. 한계는 프레임 크기로 계산할 수 있고, 넘으면 SIGBUS로 끝난다
---

# 깊은 재귀를 위해 네이티브 실행 파일의 스택 키우기

tail call 최적화가 없는 컴파일러에서 `let rec f n = ... 1 + f (n - 1)`은 호출 깊이만큼 기계 스택을 쓴다. 인터프리터는 512MB 스택 스레드에서 평가해서 깊이 100만도 버틴다. 컴파일한 실행 파일도 같은 한계를 갖게 하려면 **링크할 때 메인 스레드 스택 크기를 정한다.**

```
clang ... -Wl,-stack_size,0x20000000      # 512MB (macOS ld)
```

## The Insight

**스택 크기는 실행 파일 헤더에 적히는 값이다.** macOS Mach-O의 `LC_MAIN` 로드 명령에 `stacksize`가 있다. 커널은 프로그램을 띄울 때 그 크기로 메인 스레드 스택을 잡는다(직접 확인함).

```
$ otool -l deep | grep -A3 LC_MAIN
       cmd LC_MAIN
   cmdsize 24
  entryoff 1496
 stacksize 536870912
```

`ulimit -s`(이 기기 기본 8176KB, 상한 65520KB)보다 커도 동작했다. 따라서 셸 설정을 바꾸지 않아도 되고, 실행 파일 하나로 결과가 정해진다.

**한계는 계산할 수 있다.** `otool -tV`로 본 재귀 함수 `lambda_0`의 프레임은 32바이트다(`stp x20, x19, [sp, #-0x20]!`). 512MB ÷ 32B ≈ 1,670만이 한계다. 실제로 측정한 결과도 맞았다.

| 깊이 | 기본 스택(8MB) | 512MB |
|---|---|---|
| 100만 | 종료 코드 139 (SIGSEGV, C로 만든 같은 재귀) | `1000000` |
| 1,000만 | | `10000000` (0.04초) |
| 2,000만 | | 종료 코드 138 (SIGBUS) |

## Why This Matters

- **스택을 키우지 않은 경우:** 인터프리터가 계산하는 프로그램을 컴파일한 실행 파일이 세그폴트로 죽습니다. differential testing에서 "같은 프로그램, 다른 결과"가 됩니다.
- **종료 신호가 다름:** stack overflow는 macOS에서 SIGSEGV(139)나 SIGBUS(138)로 끝나고, 어느 쪽인지는 상황마다 다릅니다. 종료 코드로 원인을 찾을 때 둘 다 확인해야 합니다.

## Recognition Pattern

- 재귀가 반복문 역할을 하는 언어(ML 계열)를 tail call 보장 없이 네이티브로 컴파일할 때
- 같은 프로그램이 인터프리터에서는 되는데 컴파일하면 139나 138로 죽을 때
- 컴파일러 테스트에 "재귀 깊이 N" 기준이 있을 때

## The Approach

### Step 1: 인터프리터의 한계를 먼저 잰다

인터프리터는 깊이 100만을 1초 안에 계산했다. 1,000만은 5분 안에 끝나지 않았다. 그러니 컴파일러의 목표는 "최소 100만"이다.

### Step 2: 링크 옵션으로 스택을 키운다

```fsharp
// Toolchain.fs: clang 인자
"-Wl,-stack_size,0x20000000"   // 512MB, 인터프리터의 큰 스택 스레드와 같은 크기
```

- **macOS:** `ld` 전용 옵션입니다. 값은 16진수로, 페이지 크기의 배수여야 합니다.
- **Linux:** 메인 스레드에 같은 옵션이 없습니다. 다음 방법이 흔히 쓰입니다(이 프로젝트에서는 확인하지 않음).
  - 런타임에서 큰 스택의 `pthread`를 만들어 `calc_main`을 그 안에서 실행합니다. 인터프리터의 `runWithLargeStack`과 같은 방식입니다.
  - `setrlimit` 후 자기 자신을 다시 `exec`합니다.

### Step 3: 실행 파일에서 확인한다

`otool -l <실행 파일> | grep -A3 LC_MAIN`의 `stacksize`가 기대한 값인지 본다. 프레임 크기는 `otool -tV`로 재귀 함수의 프롤로그(`stp ..., [sp, #-N]!`)에서 읽는다.

### Step 4: 테스트로 고정한다

```fsharp
test "재귀 깊이 100만 (512MB 스택)" {
    Expect.equal (compileAndRun "let rec f n = if n = 0 then 0 else 1 + f (n - 1) in f 1000000") ("1000000\n", "", 0) ""
}
```

## Example

```bash
# 같은 C 재귀 함수, 깊이 100만
clang -O1 deep.c -o d_default                              && ./d_default 1000000   # exit 139
clang -O1 deep.c -Wl,-stack_size,0x20000000 -o d_big       && ./d_big 1000000       # 1000000
clang -O1 deep.c -Wl,-stack_size,0x4000000  -o d_64        && ./d_64 1000000        # 1000000 (64MB)
```

## 체크리스트

- [ ] 인터프리터의 재귀 한계를 쟀는가?
- [ ] 링크 옵션이 실행 파일의 `LC_MAIN stacksize`에 들어갔는가?
- [ ] 프레임 크기로 계산한 한계가 목표보다 큰가?
- [ ] 깊은 재귀 테스트가 있는가?
- [ ] macOS 전용 옵션이라는 것을 문서에 적었는가?

## 관련 문서

- `handle-deep-recursion-stack-overflow.md` - 인터프리터 쪽: 큰 스택 스레드에서 평가
- `implement-closure-conversion-to-top-level-functions.md` - 재귀 호출이 지나가는 클로저 calling convention
