---
order: 95
created: 2026-10-30
commit: ecf5f4f
version: 0.66.2
description: GC 버그는 결과를 바꾸지 않고 메모리만 새거나, 한참 뒤에 엉뚱한 곳에서 죽는다. root에서 닿는 객체를 GC와 따로 따라가며 불변식(포인터가 객체의 시작, forwarding 표시 없음, barrier 기록, GC 뒤 nursery 블록 없음)을 확인하는 heap verifier를 만든다. 만들자마자 minor GC가 nursery 블록을 잃는 버그를 찾았다. 고치기 전 코드로 되돌려 verifier가 잡는지 확인하고, 비용 때문에 테스트는 확인 간격을 둔다
---

# GC에 heap verifier 붙이기

> 관련 커밋: [ecf5f4f](https://github.com/ohama/calc-mlir/commit/ecf5f4f) (v0.66.2)

0.66.0의 세대 구분 GC(`91-build-a-mostly-copying-generational-gc.md`)는 테스트를 모두 통과했다. 기본 설정, 작은 nursery, 할당 100번마다 GC하는 stress 모드에서도 통과했다. 0.66.2는 GC마다 힙이 맞는지 확인하는 heap verifier(`CALC_GC_VERIFY=n`)를 더했다. 만든 날 버그 두 개가 나왔다.

| 버그 | 찾은 방법 | 테스트로 드러났나 |
|---|---|---|
| minor GC가 pin 한 블록을 모으며 nursery 블록을 목록에서 잃음 | verifier (`bad header`) | 아니요. 결과는 맞고 메모리만 5% 샘 |
| major GC가 남긴 pin 블록 안의 죽은 객체가 돌려준 칸을 가리킴 | verifier를 만들며 코드를 읽다가 | 아니요. stack의 오래된 단어가 맞아떨어져야 생김 |

## The Insight

**GC 버그는 결과가 아니라 불변식으로 찾는다.** 결과를 비교하는 테스트는 산 객체를 잘못 거둘 때만 실패한다. 블록을 돌려주지 않는 버그는 메모리만 늘린다. 죽은 객체 안에 남은 나쁜 포인터는 그 객체를 아무도 다시 보지 않는 동안 문제가 없다. 이런 버그는 결과를 비교해서는 보이지 않는다. 대신 "GC가 끝난 힙은 이래야 한다"를 적어 두고 GC마다 확인한다.

**verifier는 GC와 독립적으로 따라간다.** GC의 mark bit나 목록을 쓰면 GC의 버그를 그대로 물려받는다. verifier는 자기 bit 표로 root에서 다시 따라가고, 객체를 찾는 코드도 블록을 처음부터 읽는 단순한 방법을 쓴다.

## Why This Matters

- **stress 모드만으로는 부족하다.** stress는 GC를 자주 일으켜 버그가 드러날 기회를 늘릴 뿐이다. 드러나는 것은 여전히 "결과가 달라짐"이나 "죽음"이다.
- **늦게 드러나는 버그는 원인과 증상이 멀다.** 잃은 nursery 블록 안의 옛 객체를 다른 경로가 건드리면, 그 일은 수천 번 뒤의 GC에서 엉뚱한 블록에서 일어난다. verifier는 블록을 잃은 바로 그 GC에서 멈춘다.
- **앞으로 GC를 고칠 때의 안전망이다.** defragmentation처럼 객체를 옮기는 일을 더하기 전에 있어야 한다.

## Recognition Pattern

- 직접 만든 GC나 할당기가 있고, 테스트가 결과만 비교할 때
- GC 통계에서 "쓴 블록 수"가 산 객체에 비해 큰데 이유를 모를 때
- 설계 문서에 "verifier"가 계획으로만 남아 있을 때

## The Approach

### Step 1: 불변식을 표로 적는다

| 확인 | 깨지면 생기는 일 |
|---|---|
| 힙의 포인터가 객체의 시작을 가리킴. 태그(cons는 2)와 블록 종류가 맞음 | 객체 안쪽을 헤더로 읽음 |
| 빈 블록, 큰 객체의 중간, 빈 칸을 가리키지 않음 | 산 객체를 거둠 |
| forwarding 표시가 남은 객체가 없음. 헤더의 종류와 크기가 맞음 | 옮긴 뒤 고치지 않은 포인터 |
| minor GC 전: old 객체가 nursery 객체를 가리키는 칸은 remembered set에 있음 | write barrier를 빠뜨림 |
| minor GC 뒤: nursery를 가리키는 것이 없고, nursery 상태의 블록이 없음 | 옮기지 않은 객체, 돌려주지 않은 블록 |

예외도 같이 적는다. 이 GC에서는 old space에 바로 만든 큰 객체를 barrier 없이 채우고, 다음 minor GC가 통째로 훑는다. 그래서 barrier 검사에서 뺀다.

### Step 2: root에서 따라가고, stack 범위는 collector와 같게 한다

```c
static void verify_heap(int phase, value *stack_lo, jmp_buf regs) {
    if (!verify_every || ++verify_tick < verify_every) return;
    ...
    verify_range(stack_lo, (value *)stack_base);           /* conservative: 객체 안 어디든 */
    verify_range((value *)regs, (value *)((char *)regs + sizeof(jmp_buf)));
    for (int64_t i = 0; i < root_count; i++) verify_pointer(NULL, roots[i]);   /* 정확하게 */
    while (verify_count > 0) verify_object(verify_stack[--verify_count]);
}
```

stack의 시작은 collector가 훑은 곳(`minor_collect`의 지역 변수)을 넘겨받는다. verifier의 프레임은 더 깊다. 그 아래까지 훑으면 GC가 보지 않은 오래된 단어를 root로 보아, 없는 버그를 보고한다.

dead 객체를 따라가지 않도록 힙을 차례로 훑지 않고 root에서 따라간다. old space에는 다음 major GC까지 죽은 객체가 남아 있고, 그 안의 포인터는 이미 의미가 없을 수 있다.

### Step 3: 틀린 곳을 좁힐 수 있게 출력한다

```
gc verify failed (before minor GC, GC #774): bad header
  slot 0x700baf8000 (block 5983, state 1), value 0x700ba221af (block 5982, state 3)
```

칸과 값 각각의 블록 번호와 상태를 출력한다. 처음에는 값의 블록만 출력해서, 문제가 old 블록에 있다고 잘못 읽었다. 칸의 블록을 같이 보니 **지금 쓰는 nursery 블록**(state 1)의 첫 객체에 forwarding 표시가 있었다. 그런 블록은 앞선 minor GC가 옮기고도 돌려주지 않은 블록뿐이다. "minor GC 뒤에 nursery 상태의 블록이 남았나"를 확인하자 첫 GC에서 바로 나왔다.

```c
/* 버그: pin 한 블록을 앞쪽에 모으며 그 자리의 블록 번호를 덮어씀 → 덮어쓴 블록은 5 에서 돌려주지 못함 */
nursery_blocks[kept++] = b;
/* 고침: 자리를 바꿈 */
nursery_blocks[i] = nursery_blocks[kept];
nursery_blocks[kept++] = b;
```

원인을 좁히려고 넣은 확인은 지우지 않고 verifier에 넣는다.

### Step 4: 고치기 전 코드로 verifier를 시험한다

고친 줄만 되돌리고 verifier를 켜서 실행한다. 기본 설정의 첫 GC에서 `nursery block was neither freed nor promoted`로 멈추는 것을 확인했다. verifier가 실제로 이 버그를 잡는다는 증거이고, 다음에 같은 실수를 하면 테스트가 실패한다는 뜻이다.

### Step 5: 비용을 재고 확인 간격을 정한다

verifier는 한 번에 산 힙 전체를 따라간다.

| 영속 트리 (산 객체 10만 개) | 시간 |
|---|---|
| nursery 256KB, verifier 없음 | 0.02초 |
| nursery 256KB, GC마다 | 10초 |
| 기본 nursery, GC마다 | 0.16초 |

테스트 묶음은 기본 설정에서 GC마다, nursery 256KB에서 10번째 GC마다(`CALC_GC_VERIFY=10`) 켠다. 전체 테스트를 GC마다 확인하는 것(`CALC_GC_VERIFY=1 dotnet test`, 3분)은 GC를 고친 뒤에 손으로 돌린다.

## Example

검사하는 코드를 쓰다 보면 GC의 가정을 하나씩 다시 묻게 된다. 두 번째 버그는 "conservative root가 죽은 객체에 닿으면 그 안의 포인터는 믿을 수 있나?"를 따지다가 나왔다.

- major GC는 pin 해서 넘겨받은 일반 블록에 산 객체가 하나라도 있으면 블록을 통째로 남긴다.
- 그 안의 죽은 객체는 이번 sweep에서 돌려준 객체를 가리킬 수 있다.
- 나중에 stack의 오래된 단어가 그 죽은 객체를 가리키면, major GC가 그 객체를 살리고 돌려준 칸을 따라간다.

그래서 sweep이 남기는 블록의 죽은 객체는 헤더만 두고 칸을 0으로 지운다.

```c
if (!atomic_kind(h) && !(bits[w >> 6] & (1ull << (w & 63)))) memset(p + 8, 0, (size_t)(words - 1) * 8);
```

## 체크리스트

- [ ] GC가 끝난 힙의 불변식을 표로 적었는가? 예외(barrier 없이 채우는 객체)도 적었는가?
- [ ] verifier가 GC의 mark bit나 목록을 쓰지 않고 따로 따라가는가?
- [ ] stack 범위를 collector가 훑은 범위와 같게 했는가?
- [ ] 실패 메시지에 칸과 값 각각의 위치와 상태가 있는가?
- [ ] 고치기 전 코드로 되돌려 verifier가 버그를 잡는지 확인했는가?
- [ ] 비용을 재고 테스트의 확인 간격을 정했는가?

## 관련 문서

- `91-build-a-mostly-copying-generational-gc.md` - 이 verifier가 확인하는 collector
- `93-recycle-pinned-blocks-and-grow-the-nursery.md` - GC 통계와 RSS를 따로 재기
- `26-write-differential-tests-for-compiler.md` - 결과를 비교하는 테스트
