---
order: 91
created: 2026-10-30
commit: a82cfa6
version: 0.65.0
description: stack map이 없는 컴파일러에도 세대 구분 GC를 넣을 수 있다. stack과 레지스터는 conservative하게 보고 그것이 가리키는 nursery 블록은 옮기지 않고(pinning) old space로 넘기며, 힙은 tagged 값이라 정확하게 본다. 먼저 같은 값 표현의 C 모델로 효과를 확인하고, 런타임에는 빌드 스위치로 넣는다. 헤더의 크기 필드를 다른 용도로 쓰는 곳(클로저의 인자 수)과, 블록 하나를 통째로 pin 해 복사를 시험하지 못하는 stress 모드를 조심한다
---

# stack map 없이 mostly-copying 세대 구분 GC 넣기

> 관련 커밋: [a82cfa6](https://github.com/ohama/calc-mlir/commit/a82cfa6) (v0.65.0)

0.64.0까지 실행 파일은 Boehm GC를 썼다. 할당이 많은 프로그램에서 Boehm이 시간의 36~79%를 썼고, 대부분은 수거할 때마다 살아 있는 힙 전체를 다시 mark하는 비용이었다. 0.65.0은 직접 만든 세대 구분 GC를 `CALC_GC=gen`으로 고를 수 있게 했다. 기본은 여전히 Boehm이다.

| 프로그램 (실행 파일) | Boehm | `CALC_GC=gen` |
|---|---|---|
| msort 2백만 개 | 0.53초, 316MB | 0.37초, 275MB |
| 영속 트리 백만 개 | 0.94초, 108MB | 0.70초, 114MB |
| 2백만 개를 붙잡은 채 2천만 번 할당 | 0.35초, 404MB | 0.15초, 204MB |
| 체 1억, int 배열 반복 | 같음 | 같음 |

설계와 측정은 `docs/generational-gc-design.md`에 있다.

## The Insight

**정확한 GC가 아니어도 객체를 옮길 수 있다. stack이 가리키는 것만 안 옮기면 된다.** 세대 구분 GC가 어려운 이유는 객체를 옮기면 그것을 가리키는 모든 포인터를 고쳐야 하기 때문이다. 힙과 stack이 고칠 대상이다.

- **힙:** 이 언어의 값은 모두 tagged라서, 힙의 단어가 포인터인지 태그(0이나 2)와 주소 범위로 정확히 안다.
- **stack:** LLVM이 SSA 값을 레지스터와 stack slot 어디에 둘지 정하고, derived pointer도 만든다. stack map 없이는 정확히 모른다.

그래서 stack은 고치지 않고 **stack이 가리키는 블록을 그 자리에서 old space로 넘긴다**(Bartlett의 mostly-copying, Mono SGen과 같은 구조). minor GC 때 살아 있는 프레임이 쥔 nursery 블록은 몇 개뿐이라, 나머지 살아 있는 객체는 모두 옮길 수 있다. 생성 코드에는 값을 바꾸는 곳의 write barrier만 더하면 된다.

## Why This Matters

- **다른 방법은 생성 코드를 다시 쓴다.** LLVM statepoint는 GC 포인터를 `ptr addrspace(1)`로 구별한다. tagged `i64`와 `주소 | 2` 같은 정수 연산을 추적하지 못한다. shadow stack은 호출마다 살아 있는 값을 메모리에 썼다 읽는다.
- **Boehm의 generational 모드는 답이 아니었다.** manual VDB로 켜면 C 모델에서 오히려 느렸다(0.48초 → 0.85초). 옮기지 않으므로 할당이 free list 그대로이고, sweep도 그대로다.
- **C 런타임이 그대로 안전하다.** `calc_string *x = STR(a)`를 지역 변수에 들고 할당해도, stack이 가리키므로 pin 되어 옮겨지지 않는다.

## Recognition Pattern

- 값 표현이 tagged라서 힙은 정확하게 볼 수 있지만, 컴파일러가 stack map을 내지 않을 때
- conservative GC가 수거마다 큰 live 힙을 다시 mark하는 시간이 클 때
- 값을 바꾸는 곳(ref, 배열, 재귀 클로저 채우기)이 적은 함수형 언어

## The Approach

### Step 1: 지금 비용과 상한을 잰다

`sample`로 Boehm의 시간을 mark, sweep, 할당으로 나눈다. 같은 값 표현의 C 모델에서 할당기만 바꿔, 수거 없는 bump 할당을 상한으로 잰다(msort 모델 0.48초 → 0.21초).

### Step 2: C 모델에 collector를 먼저 붙인다

`benchmarks/gc-model/`의 모델 다섯(msort, live, bst, 쓰기가 많은 mut, 깊은 재귀 deep)은 같은 소스를 Boehm과 새 collector로 빌드한다. `run.sh`가 결과가 같은지 확인한 뒤 잰다.

- **구조:** 32GB 영역을 32KB 블록으로 나누고 블록마다 상태를 표에 둔다. nursery는 일반 블록과 cons 블록(헤더 없는 16바이트만)에 따로 bump 한다.
- **minor GC:**
  1. stack과 레지스터(`setjmp`)의 단어가 nursery 블록을 가리키면 그 블록을 pin 해서 old로 넘긴다.
  2. root(remembered set, 넘긴 블록의 객체, 런타임 전역)에서 Cheney 방식으로 복사한다. 첫 단어에 `새 주소 | 0xF`를 남긴다.
- **old space:** 크기별 블록의 free list와 큰 객체다. major GC는 mark-sweep이다.
- **조정한 것:** nursery를 4MB에서 16MB로 했다(살아남는 비율이 낮아짐). 다음 nursery를 훑은 stack의 2배로 키웠다. 깊은 재귀에서 minor GC마다 수십 MB의 stack을 다시 훑는 비용이 Boehm보다 커졌기 때문이다(deep 0.30초 → 0.10초).

### Step 3: 런타임에는 같은 이름의 두 구현을 둔다

```c
void *calc_alloc(int64_t bytes);          /* 헤더 블록. 부른 쪽이 다음 할당 전에 헤더를 씀 */
void *calc_alloc_atomic(int64_t bytes);   /* 문자열, bool 배열 */
void *calc_alloc_cons(void);              /* 헤더 없는 16바이트 */
void calc_write_barrier(value *slot, value v);
void calc_gc_add_root(value *slot);
```

`CALC_GC_GEN`이면 `gc.c`가, 아니면 `runtime.c`가 Boehm으로 구현한다. Boehm 쪽의 barrier와 root 등록은 아무것도 하지 않는다. Toolchain은 `CALC_GC=gen`이면 런타임을 `-DCALC_GC_GEN`으로 컴파일하고 `-lgc`를 빼며, 런타임 오브젝트 캐시의 키에 모드를 넣는다.

### Step 4: 값을 바꾸는 곳을 모두 찾아 barrier를 둔다

| 곳 | barrier |
|---|---|
| `:=`, 단어 배열 쓰기, `let rec … and …`의 클로저 칸 (생성 코드) | 값이 포인터(`(v & 5) = 0`)일 때만 `calc_write_barrier` |
| 리스트를 앞에서부터 만들 때 tail 고치기 (`push`, `append`) | 런타임에서 |
| `calc_array_set` | 런타임에서 |
| old space에 바로 만든 큰 배열을 채우기 | barrier 대신 다음 minor GC가 그 객체를 한 번 훑음 |

생성 코드의 barrier 함수에는 `memory_effects`로 "GC 기록만 바꾼다"고 알린다. 그래야 반복 안의 다른 읽기를 LLVM이 반복 밖으로 뺄 수 있다. minor GC가 객체를 옮겨도 레지스터의 값이 가리키는 객체는 pin 되어 그대로이므로 결과가 같다.

```mlir
llvm.func @calc_write_barrier(!llvm.ptr, i64) attributes {memory_effects = #llvm.memory_effects<other = none,
    argMem = none, inaccessibleMem = readwrite, errnoMem = none, targetMem0 = none, targetMem1 = none>, no_unwind}
```

### Step 5: 객체를 차례로 읽는 곳은 모두 헤더를 정확히 해석해야 한다

pin 한 블록은 처음부터 객체를 차례로 읽어 훑는다. 그래서 블록의 모든 객체에 헤더가 있어야 한다. 정렬용 임시 버퍼처럼 헤더 없이 할당하던 곳에도 헤더를 붙인다. 크기를 읽을 때는 헤더의 위쪽 비트를 다른 용도로 쓰는 종류를 가린다.

```c
static inline int64_t header_words(value h) {
    int64_t kind = h & 0xff, n = (uint64_t)h >> 8;
    if (kind == 5 || kind == 1) n &= 0xffffff;  /* data 는 생성자 번호, 클로저는 인자 수가 위쪽 32비트 */
    if (kind == 9) return 1 + (n + 7) / 8;      /* bool 배열: n 은 바이트 수 */
    return 1 + n;
}
```

클로저를 가리지 않았더니 크기를 3천3백만 단어로 읽어 객체 뒤의 메모리까지 mark했다. 죽은 리스트가 모두 살아 있는 것으로 보여 major GC가 아무것도 거두지 못했다(5.8초, 1.7GB). 1단계 C 모델에는 클로저가 없어서 드러나지 않았다. **모델에 없는 값 종류를 런타임에 넣을 때 다시 확인한다.**

### Step 6: stress 모드는 복사를 실제로 시험하는지 확인한다

`CALC_GC_STRESS=n`은 할당 n번마다 minor GC를 한다. 처음 만든 것은 통과는 했지만 "복사한 양 0MB"였다. 마지막 GC 뒤의 할당이 모두 한 블록에 들어가고, stack이 그 블록을 가리켜 통째로 pin 되었기 때문이다. stress 모드에서는 블록마다 객체를 4개만 넣어서, 최근 블록만 pin 되고 나머지는 복사되게 했다. **GC의 통계(복사한 양, pin 한 블록 수)를 보고 stress가 무엇을 시험하는지 확인한다.**

```bash
CALC_GC=gen dotnet test
CALC_GC=gen CALC_GC_STRESS=10 dotnet test
CALC_GC_STATS=1 ./prog      # gc: minor 120000, major 4, copied 16 MB, pinned blocks 60010, ...
```

## Example

C 모델에서 Boehm과 결과를 비교하며 잰다.

```
$ sh benchmarks/gc-model/run.sh
msort  boehm 0.50s 298MB   gcm 0.37s 217MB
live   boehm 0.34s 437MB   gcm 0.12s 165MB
bst    boehm 1.06s 108MB   gcm 0.91s 120MB
mut    boehm 0.20s 85MB   gcm 0.08s 73MB
deep   boehm 0.25s 364MB   gcm 0.10s 246MB
```

알려진 한계: pin 한 블록은 안의 객체가 모두 죽어야 돌려준다. stress 간격을 3처럼 아주 짧게 하면, 긴 리스트의 칸 몇 개씩 담은 블록이 쌓여 메모리가 바닥난다. 기본 설정에서는 minor GC마다 2~5블록이라 1% 안쪽이다.

## 체크리스트

- [ ] 힙의 모든 단어가 값이거나 헤더라서 정확하게 볼 수 있는가?
- [ ] stack과 레지스터는 고치지 않고 pin 만 하는가?
- [ ] 같은 값 표현의 C 모델로 효과와 정확성을 먼저 확인했는가?
- [ ] 값을 바꾸는 곳(생성 코드와 런타임)에 모두 barrier가 있는가?
- [ ] 블록을 차례로 읽으므로 모든 할당에 헤더가 있고, 헤더의 크기 필드를 종류마다 맞게 읽는가?
- [ ] 런타임 전역의 값(raise 한 예외)을 root로 등록했는가?
- [ ] stress 모드가 복사를 실제로 시험하는지 통계로 확인했는가?
- [ ] 옛 GC를 빌드 스위치로 남겨 두 모드에서 전체 테스트를 돌리는가?

## 관련 문서

- `79-drop-cons-headers-with-a-pointer-tag.md` - 헤더 없는 cons (cons 블록을 따로 두는 이유)
- `83-call-multi-argument-closures-through-direct-entry.md` - 클로저 헤더의 인자 수 (Step 5의 버그)
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
