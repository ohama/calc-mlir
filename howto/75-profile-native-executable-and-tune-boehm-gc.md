---
order: 75
created: 2026-10-29
commit: ce33561
version: 0.49.0
description: 컴파일한 실행 파일은 macOS sample로 함수별 시간을 잰다. 타입 없는 IR이 부르는 polymorphic 런타임 함수는 생성 코드에서 왼쪽 값의 태그만 보고 int 경로를 바로 내면 없어진다. Boehm GC는 할당 경로(GC_malloc_many로 한꺼번에 받기)와 수거 빈도(free space divisor)를 따로 다룬다
---

# 컴파일한 실행 파일을 sample로 재고 Boehm GC 다루기

> 관련 커밋: [ce33561](https://github.com/ohama/calc-mlir/commit/ce33561) (v0.49.0)

컴파일러는 tagged `i64` 값으로 MLIR을 만들고, 런타임은 C와 Boehm GC로 되어 있다(`docs/compiler-design.md`). 0.48.0까지 실행 파일은 계산 위주의 예제에서 Python보다 9~14배 빨랐다. 그러나 할당이 많은 merge sort와 BST는 Python과 비슷했다. 실행 파일을 macOS의 `sample`로 재 보니, 시간이 생성한 코드가 아니라 C 런타임과 GC에 있었다.

| 실행 파일 (초) | Python | 0.48.0 | 0.49.0 |
|---|---|---|---|
| fib 41 | | 0.86 | 0.42 |
| merge sort 20만 | 0.205 | 0.171 | 0.097 |
| BST 20만 | 0.352 | 0.215 | 0.131 |

## The Insight

**타입 없는 IR의 polymorphic 연산은 런타임 함수 호출 하나로 끝나지만, 그 호출이 가장 뜨거운 곳에 있다.**

- **무엇이 일어나는가:** `<`와 `+`는 int인지 string인지 모르는 채로 `calc_compare`, `calc_add`를 부른다. 런타임은 따로 컴파일한 오브젝트라 LLVM이 inline할 수 없다. 그래서 fib에서 시간의 78%가 이 두 함수였다.
- **어떻게 피하는가:** 타입 정보가 없어도 된다. 타입 검사를 통과한 프로그램만 컴파일하므로, 왼쪽 값의 tag가 int면 오른쪽도 int다. 생성 코드가 tag 하나를 보고 int 경로를 바로 내면 된다.

**GC 비용은 두 갈래로 나눠 본다.**

- **할당 경로:** 블록 하나를 받는 데 드는 비용이다.
- **수거:** 살아 있는 데이터를 표시하고 나머지를 거두는 비용이다. Boehm GC는 세대 구분이 없어서, 수거할 때마다 살아 있는 데이터 전체를 다시 표시한다. 그래서 수거 횟수가 곧 비용이다.

## Why This Matters

- **생성 코드만 보면 놓친다.** LLVM IR에는 `call i32 @calc_compare(...)` 한 줄뿐이라 싸 보인다. 실제로는 함수 호출, 분기, 다시 tag 붙이기를 거치고, 상수 접기도 막는다(`total > 3000`이 상수로 접히지 않는다).
- **GC 설정만 바꾸면 메모리가 는다.** 처음 힙을 256MB로 잡으면 빨라지지만, 작은 프로그램도 279MB를 쓴다.
- **할당 경로의 비용은 GC 설정으로 줄지 않는다.** `GC_malloc`은 부를 때마다 dylib stub과 `pthread_getspecific`(스레드별 free list 찾기)을 거친다.

## Recognition Pattern

- tagged value를 쓰는 컴파일러에서, 런타임의 polymorphic 함수(`add`, `compare`, `eq`)가 profile 상위에 있을 때
- Boehm GC를 쓰는 실행 파일에서 `GC_mark_from`, `GC_reclaim_generic`, `GC_malloc_kind`가 profile 상위에 있을 때
- 할당이 많은 프로그램이 Python 같은 인터프리터와 비슷한 속도일 때

## The Approach

### Step 1: 실행 중인 실행 파일을 sample로 잰다

```bash
calc --compile=bst10 bst10.calc            # 입력을 10배로 키워 1초 넘게 돌게 함
./bst10 > /dev/null &
sample $(pgrep -n bst10) 1 -file bst.sample
grep -A12 "Sort by top of stack" bst.sample
```

```
GC_mark_from  (in libgc.1.5.6.dylib)        277
lambda_0_direct  (in bst10)                 162
GC_reclaim_generic  (in libgc.1.5.6.dylib)  132
GC_malloc_kind  (in libgc.1.5.6.dylib)       73
calc_compare  (in bst10)                     25
DYLD-STUB$$GC_malloc  (in bst10)             18
```

- **도구:** `sample`은 Xcode 없이 macOS에 들어 있다.
- **처음 실행:** 방금 만든 실행 파일은 처음 실행할 때 macOS가 검사해서 느리다. 한 번 실행한 뒤에 잰다.
- **이름:** `lambda_0_direct` 같은 이름은 생성한 함수다. `--keep-temps`로 남긴 `prog.ll`에서 찾는다.

### Step 2: 런타임 호출을 생성 코드에서 먼저 처리한다

```fsharp
// Codegen: x 가 int 인가 (하위 3비트가 1). 타입 검사를 통과했으므로 그러면 y 도 int
let isInt x =
    let tag = emitOp fb "arith.andi" x (emitConst fb "i64" 7L) "i64"
    ... arith.cmpi eq, tag, 1

let compare predicate a b =
    let x, y = emitExpr fb env a, emitExpr fb env b
    emitIf fb (isInt x)
        (fun () -> retagBool fb (cmpi predicate x y : i64))   // 태그를 붙인 채로 비교해도 순서가 같음
        (fun () -> ... llvm.call @calc_compare(x, y) ...)     // string
```

- **tag를 붙인 채 비교하는 이유:** int `n`은 `(n << 3) | 1`이다. 같은 tag끼리는 tag를 붙인 채 비교해도 순서가 같으므로 tag를 뗄 필요가 없다.
- **`+`:** tag를 떼고 i32로 더한 뒤 다시 붙인다. `-`와 같은 코드라 32비트에서 한 바퀴 도는 규칙도 같다.
- **`=`:** 왼쪽이 즉시값(int, bool, `[]`, `()`, 하위 3비트가 0이 아님)이면 비트를 비교한다. `[] = [1]`처럼 오른쪽이 블록이면 비트가 달라 `false`다. 왼쪽이 블록이면 전처럼 런타임이 내용을 비교한다.
- **결과:** 상수 프로그램은 clang이 끝까지 접는다. 문서의 예제 `discount.calc`의 `calc_main`이 `mov w0, #0x6541; ret`이 되었다.

### Step 3: 작은 블록은 GC_malloc_many로 한꺼번에 받는다

```c
#define CALC_SMALL_WORDS 8
static void *calc_free_lists[CALC_SMALL_WORDS + 1];   /* 정적 배열: GC 의 root 라 받아 둔 블록이 거두어지지 않음 */

void *calc_alloc(int64_t bytes) {
    size_t words = ((size_t)bytes + 7) >> 3;
    if (words == 0) words = 1;
    if (words <= CALC_SMALL_WORDS) {
        void *p = calc_free_lists[words];
        if (p == NULL) {
            p = GC_malloc_many(words << 3);            /* 같은 크기의 블록 여러 개, 첫 단어로 이어짐 */
            if (p == NULL) calc_error("out of memory");
        }
        calc_free_lists[words] = GC_NEXT(p);
        GC_NEXT(p) = NULL;                             /* 링크로 쓰던 첫 단어를 지움 */
        return p;
    }
    return GC_MALLOC((size_t)bytes);
}
```

- **스레드:** 프로그램이 스레드 하나라서 잠금이 필요 없다.
- **런타임 안의 할당도 같은 경로로:** 런타임이 직접 부르던 `GC_MALLOC`(`calc_cons`)도 이 경로로 바꿨다. 바꾸기 전에는 내장 함수 `reverse`, `append`가 만드는 칸이 sample에 `DYLD-STUB$$GC_malloc`으로 남아 있었다.

### Step 4: 수거 빈도는 free space divisor로 정한다

| 설정 (환경 변수로 먼저 잼) | msort | bst | 최대 메모리 (msort / bst) |
|---|---|---|---|
| 기본 (divisor 3) | 0.171 | 0.215 | 38MB / 13MB |
| `GC_FREE_SPACE_DIVISOR=2` | 0.117 | 0.154 | 51MB / 16MB |
| `GC_FREE_SPACE_DIVISOR=1` | 0.129 | 0.156 | 72MB / 20MB |
| `GC_INITIAL_HEAP_SIZE=64MB` | 0.127 | 0.147 | 71MB / 71MB |
| `GC_ENABLE_INCREMENTAL=1` | 0.256 | 0.435 | |

divisor 2 행은 Step 2, 3의 변경을 넣은 사본에서 잰 값이고, 나머지 행은 0.48.0의 실행 파일에서 잰 값이다.

- **divisor:** 살아 있는 크기에 비해 힙을 얼마나 비워 둘지 정한다. 작을수록 힙을 크게 잡아 수거가 드물다.
- **처음 힙 크기와의 차이:** 처음 힙 크기는 작은 프로그램에도 그만큼 메모리를 쓰게 한다. divisor는 살아 있는 데이터에 비례해 메모리가 는다.
- **확인한 뒤 코드로:** 환경 변수로 효과를 확인한 뒤 `calc_init`에서 `GC_set_free_space_divisor(1)`로 정했다.

## Example

| 변경 (실행 파일, 초) | fib | sieve | msort | bst |
|---|---|---|---|---|
| 0.48.0 | 0.014 | 0.022 | 0.171 | 0.215 |
| int 경로를 생성 코드에서 | 0.010 | 0.018 | 0.164 | 0.208 |
| + `GC_malloc_many` | | | 0.150 | 0.199 |
| + free space divisor 1 | 0.010 | 0.018 | 0.103 | 0.129 |
| + `calc_cons`도 같은 경로 | 0.010 | 0.018 | 0.097 | 0.130 |

넣지 않은 것도 있다.

- **LTO:** 런타임과 함께 최적화해서 C 함수를 inline하는 방법이다. fib 41이 16% 빨라졌지만 int 경로(2배)보다 작았다. 또 homebrew LLVM의 `lld`는 큰 스택 옵션(`-stack_size`)을 지원하지 않고, Apple `ld`는 새 LLVM의 bitcode를 읽지 못했다.

## 체크리스트

- [ ] 입력을 키워 1초 넘게 도는 실행 파일을 `sample`로 쟀는가?
- [ ] profile 상위의 런타임 함수가 생성 코드에서 먼저 처리할 수 있는 경우(int, 즉시값)인가?
- [ ] 타입 검사가 보장하는 것(왼쪽이 int면 오른쪽도 int)만 가정했는가?
- [ ] tag를 붙인 값의 비교와 계산이 tag를 뗀 값과 같은 결과인가? (32비트에서 한 바퀴 돌기 포함)
- [ ] 할당 경로의 비용과 수거 비용을 나눠 보았는가?
- [ ] 런타임 안에서 GC를 직접 부르는 곳도 같은 할당 경로로 바꿨는가?
- [ ] GC 설정은 환경 변수로 먼저 재고, 시간과 최대 메모리를 함께 보았는가?
- [ ] 고치기 전 컴파일러에서도 새 테스트가 통과하는가?

## 관련 문서

- `72-profile-fsharp-interpreter-and-read-its-il.md` - 인터프리터 쪽의 profiler와 IL
- `73-reduce-long-lived-objects-not-short-lived-ones.md` - .NET GC와 오래 사는 객체
- `74-move-list-builders-back-to-builtins-and-count-jit-methods.md` - 리스트 내장 함수
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
