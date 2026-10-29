---
order: 79
created: 2026-10-29
commit: 872cded
version: 0.53.0
description: 리스트 칸의 헤더를 없애고 값의 포인터 태그로 cons를 나타내면 칸이 24바이트에서 16바이트가 된다. 그러나 Boehm GC는 interior pointer를 켜 두면 16바이트 요청을 32바이트로 올리므로, 설정을 끄고 태그만큼의 displacement를 등록해야 효과가 난다. 큰 표현 변경 전에 같은 할당기를 쓰는 작은 C 모델로 효과를 먼저 잰다
---

# 포인터 태그로 cons 칸의 헤더 없애기

> 관련 커밋: [872cded](https://github.com/ohama/calc-mlir/commit/872cded) (v0.53.0)

컴파일러의 값은 하위 3비트가 종류를 나타내는 tagged `i64`다. 블록 포인터는 태그 0이다. 리스트 칸은 `[헤더, head, tail]` 블록이었는데, 컴파일한 merge sort의 시간 대부분이 이 칸을 만드는 비용이었다(`77-match-tuple-components-without-building-tuples.md`).

| 실행 파일 | merge sort 20만 | 최대 메모리 |
|---|---|---|
| Python | 0.206초 | |
| 0.52.0 (`[헤더, head, tail]`) | 0.064초 | 72MB |
| 0.53.0 (`[head, tail]`, 값은 주소 \| 2) | 0.049초 | 38MB |

## The Insight

**표현에서 바이트를 줄여도, 할당기가 그 바이트를 실제로 아끼는지는 따로 확인해야 한다.**

Boehm GC의 객체 크기는 요청 크기와 다르다.

| 요청 | interior pointer 켬 (기본) | interior pointer 끔 |
|---|---|---|
| 16B (`[head, tail]`) | **32B** | 16B |
| 24B (`[헤더, head, tail]`) | 32B | 32B |
| 32B | 48B | 32B |

interior pointer 인정이 켜져 있으면 Boehm은 객체 끝 바로 뒤를 가리키는 포인터를 위해 1바이트를 더 붙인다. 그래서 16바이트 요청이 다음 granule(16B)로 올라간다. 헤더만 없애면 칸은 여전히 32바이트다. **interior pointer를 꺼야** 16바이트가 된다.

**헤더가 하던 일은 포인터 태그가 대신한다.** 헤더의 역할은 "이 블록은 cons"라고 알리는 것뿐이었다. 쓰지 않던 태그 `2`(`010`)를 cons에 주면 값만 보고도 cons인지 안다.

## Why This Matters

- **바로 구현하면 헛수고가 될 수 있다.** 헤더만 없애고 interior pointer를 그대로 두면 효과가 0이다. C 모델에서 16바이트 칸은 0.92초로, 24바이트 칸(0.90초)과 같았다.
- **GC 설정을 바꾸면 안전 조건이 생긴다.** interior pointer를 끄면, 힙 안에서 객체 가운데를 가리키는 포인터는 그 객체를 살려 두지 못한다. 그래서 cons 값(주소 + 2)을 Boehm에 알려야 한다.

## Recognition Pattern

- tagged 값 표현에서 작은 블록(2~3단어)이 대부분인 자료 구조(리스트, 쌍)가 뜨거울 때
- Boehm GC를 쓰는데 `GC_size(GC_MALLOC(n))`이 `n`보다 크게 나올 때
- 쓰지 않는 포인터 태그가 남아 있을 때

## The Approach

### Step 1: 같은 할당기를 쓰는 작은 C 모델로 효과를 먼저 잰다

```c
/* 칸: WORDS 단어. 16B(2단어) 또는 24B(3단어: 헤더, head, tail) */
static cell cons(intptr_t h, cell t) {
    cell c = GC_MALLOC(WORDS * 8);
    c[WORDS - 2] = h; c[WORDS - 1] = (intptr_t)t;
    return c;
}
/* split, merge(rev_append 포함), msort 를 Calc 의 msort 와 같은 모양으로 2백만 개에 */
```

```bash
clang -O2 ms.c -lgc -DWORDS=3 -o ms24                 # 0.90초 575MB
clang -O2 ms.c -lgc -DWORDS=2 -o ms16i                # 0.92초 575MB (interior pointer 켬: 여전히 32B)
clang -O2 ms.c -lgc -DWORDS=2 -DNOINTERIOR -o ms16    # 0.64초 281MB (GC_set_all_interior_pointers(0))
```

컴파일러와 런타임을 고치기 전에, 20분짜리 C 모델로 "할 만한가"와 "무엇이 필요한가(interior pointer 설정)"를 함께 알아냈다.

### Step 2: 값 표현과 GC 설정을 정한다

```c
/* runtime.h */
#define CALC_TAG_CONS 2
#define CALC_IS_CONS(v) (((v) & CALC_TAG_MASK) == CALC_TAG_CONS)
#define CALC_HEAD(v) (((value *)((v) - CALC_TAG_CONS))[0])
#define CALC_TAIL(v) (((value *)((v) - CALC_TAG_CONS))[1])

/* runtime.c */
void calc_init(void) {
    GC_set_all_interior_pointers(0);         /* GC_INIT 보다 앞: 16B 요청이 16B */
    GC_INIT();
    GC_register_displacement(CALC_TAG_CONS); /* 힙 안의 "주소 + 2" 도 그 객체를 가리킨다고 알림 */
    GC_set_free_space_divisor(1);
}
```

- **왜 안전한가:** 이 언어의 값은 힙 안에서 언제나 블록의 처음(태그 0) 또는 처음 + 2(cons)만 가리킨다.
- **스택과 레지스터:** 계산 중의 파생 포인터(`&block[1]`)는 스택과 레지스터에만 있다. Boehm은 이것들을 설정과 상관없이 interior pointer로 인정한다.
- **확인한 곳:** `gc.h`의 `GC_register_displacement` 설명에 "All pointers to the interior of objects from the stack are considered valid in any case"라고 적혀 있다.

### Step 3: 리스트를 다루는 곳을 매크로 하나로 모은다

- **런타임:** `HEAD`/`TAIL`을 `CALC_HEAD`/`CALC_TAIL`로 정의하면 `builtins.c`의 리스트 함수는 그대로 동작한다. `CALC_IS_KIND(v, CALC_KIND_CONS)`(헤더 읽기)는 `CALC_IS_CONS(v)`(태그 비교)로 바꾼다.
- **Codegen:** 만들기와 패턴만 고친다.

```fsharp
// :: 는 16바이트를 할당하고 head, tail 을 쓴 뒤 | 2
fb.Emit(sprintf "%s = llvm.call @calc_alloc(%s) : (i64) -> !llvm.ptr" block (emitConst fb "i64" 16L))
...
emitOp fb "arith.ori" address (emitConst fb "i64" consTag) "i64"

// x :: rest 의 필드: head 는 값 - 2, tail 은 값 + 6 바이트
fb.Emit(sprintf "%s = llvm.getelementptr %s[%d] : (!llvm.ptr) -> !llvm.ptr, i8" address block (8 * index - int consTag))
```

- **패턴의 필드 읽기:** 패턴 검사와 이름 묶기에서 "index 번째 필드를 읽는 함수"를 인자로 받게 바꿨다(`fieldsBy read`, `fieldBy read`). 그러면 튜플과 레코드는 `emitField`, cons는 `emitConsField`로 같은 코드를 쓴다.

### Step 4: 태그에 기대던 다른 규칙을 다시 본다

0.49.0의 `=` 빠른 경로는 "태그가 0이 아니면 즉시값이니 비트를 비교"였다. cons가 태그 2를 쓰면 이 규칙이 리스트를 즉시값으로 잘못 분류한다. 그러면 같은 내용의 다른 리스트가 `false`가 된다. 그래서 "태그가 홀수(int 1, bool 3, `[]` 5, `()` 7)"로 바꿨다.

새 태그를 쓰기 전에 태그를 검사하는 곳을 모두 찾는다.

- `isInt`: 태그 1과 비교하므로 그대로 맞다.
- `emitIsTuple`: 태그 0과 헤더를 보므로 그대로 맞다.
- `=`의 즉시값 판정: 고쳐야 한다.

### Step 5: GC가 여러 번 도는 테스트를 넣는다

interior pointer를 끈 뒤의 위험은 "살아 있는 칸을 거두는 것"이다. 그래서 테스트는 수거가 여러 번 일어나는 크기로 쓴다.

| 경우 | 프로그램 |
|---|---|
| 리스트의 리스트 30만 개 | `build 300000 []` 뒤 `head`, `nth`, `map` |
| 튜플 안의 리스트 50만 개 | `map (fun x -> (x, [x])) (range 1 500000)` |
| 오래 사는 칸과 버리는 칸이 섞임 | 2백만 번 돌며 1000번마다 리스트를 버림 |
| 다른 값 안의 리스트 | ref, 배열, 예외 인자, 생성자 인자, 클로저가 붙잡은 리스트 |

기대값은 인터프리터의 출력으로 만든다. 인터프리터는 표현이 다르므로 결과의 독립적인 근거가 된다.

## Example

| 변경 | msort | 최대 메모리 |
|---|---|---|
| 0.52.0 | 0.064초 | 72MB |
| 헤더만 없앰 (C 모델로 보면) | 그대로 | 그대로 |
| 헤더를 없애고 interior pointer를 끔 + displacement 2 | 0.049초 | 38MB |

같은 조사에서 넣지 않은 것도 있다.

- **할당을 생성 코드에서 바로:** free list에서 꺼내는 코드를 생성 코드에 넣어도 변화가 없었다. 할당 함수를 부르는 비용은 병목이 아니었다.
- **타입으로 int를 확정해 태그 검사 없애기:** fib 41은 0.43초에서 0.33초가 되었지만 다른 예제는 그대로였다.

## 체크리스트

- [ ] `GC_size(GC_MALLOC(n))`으로 할당기가 실제로 쓰는 크기를 확인했는가?
- [ ] 같은 할당기를 쓰는 작은 C 모델로 효과를 먼저 쟀는가?
- [ ] interior pointer를 끈다면, 힙 안의 포인터가 언제나 블록 처음이나 등록한 displacement인가?
- [ ] 쓰지 않던 포인터 태그를 쓰기 전에, 태그를 검사하는 모든 규칙(`=` 빠른 경로 등)을 다시 보았는가?
- [ ] 리스트를 다루는 런타임 코드를 매크로 하나로 모았는가?
- [ ] 수거가 여러 번 일어나는 크기의 테스트로 살아 있는 칸이 거두어지지 않는지 확인했는가?

## 관련 문서

- `75-profile-native-executable-and-tune-boehm-gc.md` - 실행 파일의 profile과 Boehm GC 설정
- `76-flatten-constructor-tuple-payloads-by-block-size.md` - 블록의 단어 수로 표현 구분하기
- `77-match-tuple-components-without-building-tuples.md` - 튜플 match와 `rev_append`
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
