---
order: 92
created: 2026-10-30
commit: d6243f9
version: 0.65.1
description: write barrier의 비용은 가장 나쁜 쓰기 모양으로 잰다. 같은 칸들에 거듭 쓰면 remembered set이 금방 차서 minor GC가 잦아진다. old 블록마다 단어당 bit를 두어 칸을 한 번만 기록하고, remembered set은 GC를 일으키지 않고 늘린다. 효과가 없는 다듬기(펼쳐 넣기, int 쓰기의 barrier 빼기)는 재 보고 하지 않는다
---

# write barrier가 같은 칸을 한 번만 기록하게 하기

> 관련 커밋: [d6243f9](https://github.com/ohama/calc-mlir/commit/d6243f9) (v0.65.1)

0.65.0의 세대 구분 GC는 오래된 객체의 칸에 젊은 객체를 쓰면 칸 주소를 remembered set(64K 칸)에 넣었다. 차면 minor GC를 했다. 같은 칸이 연달아 들어오는 것만 걸렀다.

| 프로그램 (실행 파일) | Boehm | 0.65.0 | 0.65.1 |
|---|---|---|---|
| old 배열의 칸 1000개에 새 리스트 3천만 번 (`a.[i % 1000] <- [i]`) | 0.07초 | 0.12초 (minor GC 457번) | 0.07초 (28번) |
| ref 1000개에 int 3천만 번 | 0.03초 | 0.03초 | 0.03초 |
| 같은 ref에 새 리스트 2천만 번 | 0.11초 | 0.10초 | 0.10초 |

## The Insight

**remembered set의 크기는 쓰기 횟수가 아니라, minor GC 사이에 바뀐 칸의 수여야 한다.** minor GC는 칸마다 지금 값을 다시 읽으므로, 한 칸을 백 번 기록해도 한 번 기록한 것과 결과가 같다. 칸마다 "이미 기록함" bit를 두면 크기가 바뀐 칸 수로 줄어든다. 그러면 remembered set이 찰 일이 드물어서, 차면 GC를 하는 대신 늘려도 된다.

## Why This Matters

- **"바로 앞과 같으면 건너뜀"은 칸 하나일 때만 맞는다.** 반복 안에서 칸 여럿을 돌아가며 쓰면(배열, ref 여러 개) 하나도 걸러지지 않는다. 0.65.0은 같은 ref 하나에 쓰는 예제로 확인했기 때문에 이 경우를 놓쳤다.
- **barrier가 GC를 일으키면 순서 문제가 생긴다.** 값을 쓴 뒤 barrier에서 GC를 하면, 쓴 값이 stack에 살아 있지 않을 수 있다. 0.65.0은 GC 뒤에 값을 다시 검사해서 살려 두었다. barrier가 GC를 일으키지 않으면 이 조건이 아예 없어진다.

## Recognition Pattern

- profile이나 GC 통계에서, nursery가 차서 생기는 수보다 minor GC가 훨씬 많을 때 (3천만 번 쓰기에 457번, nursery로는 28번)
- write barrier를 같은 칸 하나에 쓰는 예제로만 잰 경우

## The Approach

### Step 1: 가장 나쁜 쓰기 모양을 만든다

| 모양 | 시험하는 것 |
|---|---|
| old 배열의 칸 여럿에 새 객체 (`a.[i % 1000] <- [i]`) | 칸마다 barrier의 느린 길, remembered set 크기 |
| ref 여럿에 int | 태그 검사의 빠른 길 |
| 같은 ref에 새 객체 | 같은 칸의 반복 |

`CALC_GC_STATS=1`로 minor GC 횟수를 함께 본다.

### Step 2: 블록마다 단어당 bit를 둔다

```c
void calc_write_barrier(value *slot, value v) {
    if (!is_young(v) || !in_region((value)slot)) return;
    int64_t b = block_of(slot);
    if (is_young_block(b)) return;
    if (!meta[b].remembered) meta[b].remembered = calloc(BLOCK_WORDS / 64, sizeof(uint64_t));  /* 블록당 512바이트, 처음 쓸 때 */
    int64_t w = ((char *)slot - block_start(b)) >> 3;
    uint64_t bit = 1ull << (w & 63);
    if (meta[b].remembered[w >> 6] & bit) return;           /* 이미 기록한 칸 */
    meta[b].remembered[w >> 6] |= bit;
    if (remembered_count == remembered_cap) {                /* GC 대신 늘림 */
        remembered_cap *= 2;
        remembered = realloc(remembered, (size_t)remembered_cap * sizeof *remembered);
    }
    remembered[remembered_count++] = slot;
}
```

minor GC는 칸을 처리할 때 bit를 지운다. 블록을 돌려줄 때 bit 배열도 지운다.

### Step 3: 다듬기 후보를 재 보고 고른다

| 후보 | 결과 | 한 것 |
|---|---|---|
| 같은 칸을 한 번만 기록 | 0.12초 → 0.07초 | 함 |
| 빠른 길(젊은지 검사)을 생성 코드에 펼쳐 넣기 | 가장 나쁜 경우도 Boehm과 같아져 남은 이득이 없음. 블록 표 주소를 생성 코드에 알려야 함 | 안 함 |
| int로 정해진 `:=`, 배열 쓰기에서 barrier 빼기 | 태그 검사 하나라 시간 차이 없음 | 안 함 |

설계 문서의 계획에 있던 일이라도, 재서 이득이 없으면 하지 않고 그 결과를 적는다.

### Step 4: 늘어나는 경로를 테스트한다

minor GC 사이에 서로 다른 칸 20만 개(remembered set의 처음 크기 64K보다 많음)에 쓰는 프로그램을 세대 구분 GC 묶음에 더한다. nursery 256KB와 할당 100번마다 minor GC에서 인터프리터와 결과를 비교한다.

## Example

```
$ CALC_GC_STATS=1 ./wb1      # a.[i % 1000] <- [i] 3천만 번
0.65.0: gc: minor 457, ...   0.12초
0.65.1: gc: minor 28, ...    0.07초
```

## 체크리스트

- [ ] barrier를 칸 여럿에 돌아가며 쓰는 모양으로 쟀는가?
- [ ] minor GC 횟수가 nursery 크기로 정해지는 수와 비슷한가?
- [ ] 같은 칸을 minor GC 사이에 한 번만 기록하는가?
- [ ] barrier가 GC를 일으키지 않는가?
- [ ] remembered set이 늘어나는 경로를 테스트했는가?

## 관련 문서

- `91-build-a-mostly-copying-generational-gc.md` - 이 barrier를 쓰는 collector
- `81-give-llvm-alias-info-with-tbaa-and-noreturn.md` - barrier 함수에 memory effect를 알리는 것과 같은 생각
