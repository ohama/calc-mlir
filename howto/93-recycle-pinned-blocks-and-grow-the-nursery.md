---
order: 93
created: 2026-10-30
commit: 58331f1
version: 0.65.2
description: mostly-copying GC가 pin 해서 넘긴 블록은 안의 객체가 모두 죽어야 돌아온다. old space의 블록과 모양이 같은 블록(cons)은 major GC에서 그 종류로 바꿔 빈 칸을 다시 쓴다. GC가 세는 크기와 실제 메모리(RSS)를 따로 재야 단편화가 보인다. nursery는 작게 시작해 키워서 할당이 적은 프로그램의 메모리를 줄인다
---

# pin 한 블록을 다시 쓰고 nursery를 작게 시작하기

> 관련 커밋: [58331f1](https://github.com/ohama/calc-mlir/commit/58331f1) (v0.65.2)

0.65.0의 세대 구분 GC(`91-build-a-mostly-copying-generational-gc.md`)에는 메모리 문제가 둘 있었다.

- **pin 한 블록이 쌓임:** minor GC 때 stack이 가리키는 nursery 블록(32KB)은 옮기지 않고 old space로 넘긴다. 이 블록은 안의 객체가 모두 죽어야 돌려준다. 할당 3번마다 minor GC를 하는 stress(`CALC_GC_STRESS=3`)에서 old space가 1.5GB까지 커졌다.
- **작은 프로그램의 메모리:** nursery를 처음부터 16MB로 잡아, `println 42`도 최대 메모리가 16MB쯤이었다(Boehm은 2MB).

| | 0.65.1 | 0.65.2 |
|---|---|---|
| stress 3에서 old space 최고치 | 1.5GB | 64MB |
| stress 3에서 실제 메모리 (RSS) | 3.4GB | 3.4GB (남은 문제) |
| `println 42`의 최대 메모리 | 16MB쯤 | 1MB |

## The Insight

**pin 한 블록이라도 old space의 블록과 모양이 같으면 그 종류로 바꾸면 된다.** cons 블록은 nursery에서도 old space에서도 "블록 시작부터 16바이트 칸"이다. 그래서 major GC의 sweep이 넘겨받은 cons 블록을 old cons 블록으로 다시 표시하면, 죽은 칸이 free list로 가고 그 뒤로는 보통 old 블록처럼 다룬다. 옮기지 않으므로 stack이 가리키는 객체도 안전하다.

다만 **GC가 세는 크기와 실제 메모리는 다르다.** 빈 칸을 다시 쓸 수 있게 되어도, 살아 있는 칸이 블록마다 하나씩 흩어져 있으면 블록 수는 줄지 않는다. 그래서 RSS를 따로 재야 단편화가 보인다.

## Why This Matters

- **pin 한 블록은 오래 사는 데이터를 담기 쉽다.** stack이 가리키는 것은 지금 만들고 있는 자료 구조라서, 그 칸들은 오래 산다. 블록의 나머지가 죽어도 블록 전체가 남는다.
- **GC 통계만 보면 고쳐진 것처럼 보인다.** old space 최고치는 1.5GB에서 64MB가 되었지만 RSS는 3.4GB 그대로였다. 통계는 "살아 있는 칸의 크기"를 세고, RSS는 "건드린 블록 수"를 센다.
- **작은 프로그램이 대부분이다.** 테스트, 스크립트, 짧은 계산은 몇 MB만 할당한다. nursery 크기가 그대로 최대 메모리의 하한이 된다.

## Recognition Pattern

- mostly-copying GC나 pinning이 있는 GC에서, 블록 단위로 넘긴 메모리가 늘어날 때
- GC 통계의 live 크기와 `time -l`의 최대 메모리가 크게 다를 때
- 할당이 적은 프로그램도 최대 메모리가 nursery 크기쯤일 때

## The Approach

### Step 1: 쌓이는 상황을 일부러 만든다

```bash
CALC_GC=gen calc --compile=prog prog.calc
CALC_GC_STRESS=3 CALC_GC_STATS=1 /usr/bin/time -l ./prog
# gc: minor 400002, major 107, ... old peak 64 MB, blocks used 166819 (5213 MB)
#     3379085312  maximum resident set size
```

stress 간격을 짧게 하면 GC마다 블록이 pin 되어 넘어간다. 통계의 `old peak`(GC가 세는 크기)와 `blocks used`(건드린 블록), RSS를 함께 본다.

### Step 2: 모양이 같은 블록은 sweep에서 종류를 바꾼다

```c
case OLD_PROMOTED_CONS:
    /* 넘겨받은 cons 블록은 old cons 블록과 모양이 같음. 살아 있는 칸이 있으면 old cons 블록으로 바꿔 죽은 칸을 다시 씀 */
    if (!bits) { give_block(b); break; }      /* mark 된 칸이 없음 */
    m->state = OLD_CONS;
    m->slot_words = 2;
    __attribute__((fallthrough));
case OLD_SIZED:
case OLD_CONS: { ...  /* 죽은 칸을 free list 로, 살아 있는 칸이 없으면 블록을 돌려줌 */ }
```

fall-through로 크기별 블록의 코드를 같이 쓸 때는 앞의 case가 그 코드의 전제(칸 크기)를 모두 채웠는지 확인한다. 처음에는 mark bit가 없는 블록도 그대로 내려보냈다. 그 블록은 칸 크기가 0이라 sweep이 끝나지 않았다.

크기가 섞인 일반 블록은 이렇게 바꿀 수 없다. 객체가 모두 죽으면 돌려주는 것으로 둔다.

### Step 3: 남은 단편화의 원인을 가린다

RSS가 줄지 않았으므로 "빈 칸을 다시 쓰지 못해서"인지 "살아 있는 칸이 흩어져서"인지 가린다. cons를 복사할 때 free list를 bump보다 먼저 쓰게 바꿔 보았다. RSS도 시간도 그대로였으므로 원인은 흩어진 살아 있는 칸이다. 이것을 줄이려면 major GC가 드문드문한 블록의 객체를 옮겨야 한다(stack이 가리키지 않는 것만 옮기고 힙의 포인터를 고침). 기본 설정에서는 pin 하는 블록이 minor GC마다 2~5개이고 같은 자료 구조의 칸이라 드문드문하지 않으므로, 이 일은 뒤로 미룬다. 바꿔 본 것은 되돌리고 결과를 문서에 적는다.

### Step 4: nursery는 작게 시작해 키운다

```c
#define NURSERY_START_BLOCKS 32   /* 1MB */
#define NURSERY_FULL_BLOCKS 512   /* 16MB */
/* minor GC 마다 */
if (base_budget < full_budget) base_budget = base_budget * 2 < full_budget ? base_budget * 2 : full_budget;
```

할당이 많은 프로그램은 minor GC 다섯 번 만에 16MB가 되므로 시간은 그대로다. 크기를 정하면(`CALC_GC_NURSERY_KB`) 처음부터 그 크기로 한다. 테스트가 정한 크기로 GC 빈도를 맞추기 때문이다. 기본 설정도 테스트에 넣어 커지는 경로를 시험한다.

## Example

| 프로그램 | Boehm | gen 0.65.2 |
|---|---|---|
| `println 42` | 2MB | 1MB |
| `benchmarks/` fib, queens, sieve | 2~4MB | 1~3MB |
| msort 2백만 개 | 0.53초, 316MB | 0.37초, 272MB |
| `sprintf` 3백만 번 | 0.09초, 3MB | 0.06초, 17MB (nursery가 16MB까지 커짐) |

## 체크리스트

- [ ] pin 해서 넘긴 블록이 쌓이는 상황을 stress로 만들어 보았는가?
- [ ] GC의 통계와 RSS를 함께 재는가?
- [ ] old space와 모양이 같은 블록을 그 종류로 바꿔 다시 쓰는가?
- [ ] fall-through로 코드를 같이 쓸 때 앞 case가 전제를 채우는가?
- [ ] 바꿔 본 것이 효과가 없으면 되돌리고 결과를 적었는가?
- [ ] 할당이 적은 프로그램의 최대 메모리를 Boehm과 비교했는가?

## 관련 문서

- `91-build-a-mostly-copying-generational-gc.md` - pinning과 블록 구조
- `92-remember-each-slot-once-in-a-write-barrier.md` - 같은 collector의 write barrier
