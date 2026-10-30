---
order: 96
created: 2026-10-30
commit: 693cf67
version: 0.66.2
description: mostly-copying GC에서 pin이 잦으면 old 블록마다 산 객체가 흩어져 남아 메모리가 블록 수만큼 는다. major GC의 mark 뒤에 산 객체가 적은 블록을 골라 객체를 옮기고, 산 객체와 root의 포인터를 고친 다음 블록을 돌려준다. stack은 고칠 수 없으므로 stack이 가리키는 블록은 옮기지 않는다. 크기가 섞인 블록도 옮겨야 효과가 난다. verifier와 "늘 옮기는" stress 설정으로 시험한다
---

# conservative GC에서 드문드문한 블록 비우기

> 관련 커밋: [693cf67](https://github.com/ohama/calc-mlir/commit/693cf67) (v0.66.2 다음)

세대 구분 GC(`91-build-a-mostly-copying-generational-gc.md`)는 minor GC 때 stack이 가리키는 nursery 블록을 그 자리에서 old space로 넘긴다(pinning). 0.65.2에서 넘겨받은 cons 블록의 빈 칸을 다시 쓰게 했지만(`93-recycle-pinned-blocks-and-grow-the-nursery.md`), pin이 아주 잦으면 메모리가 여전히 컸다. 블록마다 산 객체가 몇 개씩 흩어져 남았기 때문이다.

| `CALC_GC_STRESS=3` (할당 3번마다 minor GC) | 전 | 후 |
|---|---|---|
| `benchmarks/msort.calc` | 블록 26만 개, 최대 메모리 6.4GB, 25.7초 | 블록 4095개, 131MB, 7.1초 |
| `benchmarks/bst.calc` | 블록 15만 개, 4.6GB | 블록 2156개, 69MB |
| 전체 테스트 | 긴 리스트를 만드는 테스트가 메모리 부족 | 통과 |

기본 설정에서는 시간이 같고, 영속 트리 백만 개의 최대 메모리가 95MB에서 89MB가 되었다.

## The Insight

**힙이 정확하면 conservative GC도 객체를 옮길 수 있다. 고칠 수 없는 곳(stack)이 가리키는 블록만 빼면 된다.** 옮긴 객체를 가리키는 것은 힙의 칸, 런타임 전역, stack과 레지스터다. 힙은 tagged 값이라 정확히 고칠 수 있고, 전역은 등록된 것뿐이다. stack이 가리키는 블록은 통째로 제외하면 고칠 필요가 없다.

**고르는 시점은 mark 뒤가 좋다.** mark가 끝나면 블록마다 산 객체를 정확히 안다. 그때 산 것이 적은 블록만 고르면 옮기는 양이 작고, 고른 블록은 모두 비워서 돌려줄 수 있다.

## Why This Matters

- **non-moving old space는 단편화를 스스로 풀지 못한다.** 빈 칸을 free list로 다시 써도, 산 칸이 블록마다 하나씩 있으면 블록 수가 줄지 않는다. 0.65.2에서 "빈 칸을 먼저 쓰기"를 시험했지만 RSS가 그대로였다.
- **pin은 conservative GC에서 피할 수 없다.** 기본 설정에서는 드물어도, 깊은 재귀나 큰 stack에서는 minor GC마다 pin이 늘 수 있다. 최악의 경우가 수 GB면 실제 프로그램에서도 언젠가 터진다.

## Recognition Pattern

- GC 통계의 "쓴 블록 수"나 RSS가 산 데이터보다 수십 배 클 때
- stress 모드(잦은 GC)에서만 메모리가 바닥날 때
- non-moving 영역이 있는 GC(mostly-copying, mark-sweep old space)

## The Approach

### Step 1: 힙 밖에서 힙을 가리키는 곳을 모두 센다

| 곳 | 옮길 때 |
|---|---|
| 힙 객체의 칸 | 고침 (tagged라서 포인터인지 앎) |
| 런타임 전역 (`calc_gc_add_root`로 등록한 것) | 고침 |
| stack, 레지스터 | 고칠 수 없음 → 그것이 가리키는 블록은 옮기지 않음 |
| 생성 코드의 전역 | 상수뿐이라 힙을 가리키지 않음 |
| `malloc` 버퍼 | 값을 두지 않음 (규칙) |
| nursery, remembered set | major GC는 minor GC 바로 뒤라 비어 있음 |

이 표가 완전해야 옮길 수 있다. 하나라도 빠지면 옮긴 뒤 옛 주소를 쓰게 된다.

### Step 2: stack이 가리키는 블록을 표시한다

```c
static void mark_range(value *lo, value *hi) {
    for (value *s = lo; s < hi; s++) {
        value v = *s;
        if (in_region(v)) meta[block_of((void *)v)].stack_ref = 1;
        if (in_region(v - 1)) meta[block_of((void *)(v - 1))].stack_ref = 1;  /* 객체 끝 바로 다음을 가리키는 포인터 */
        mark_value(v, 0);
    }
}
```

반복의 끝 주소처럼 객체 바로 뒤를 가리키는 포인터가 블록 경계에 걸릴 수 있다. 그래서 앞 블록도 표시한다. minor GC의 pinning에도 같은 규칙이 있다.

### Step 3: mark 뒤에 고르고, 옮기고, 고친다

1. **고르기:** 산 객체가 25% 이하인 블록을 고른다. stack이 가리키는 블록과 지금 bump 하는 블록은 뺀다. 고른 블록이 적으면(32개 미만) 하지 않는다.
2. **옮기기:** mark 된 객체를 크기별 블록에 bump로 복사하고, 새 자리에 mark bit를 세운다. 옛 자리에는 forwarding 표시를 남긴다. free list는 비운다. 빈 칸이 옮길 블록에도 있기 때문이다.
3. **고치기:** mark 된 객체를 모두 돌며, 칸이 옮긴 블록을 가리키면 forwarding 표시에서 새 주소를 읽어 바꾼다. 옮긴 객체도 새 자리에서 mark 되어 있으므로 같이 고쳐진다. root도 고친다.
4. **sweep:** 옮긴 블록의 mark bit를 지워 두었으므로 sweep이 블록을 돌려준다.

```c
static inline void fix_slot(value *slot) {
    value v = *slot;
    if (!CALC_GC_IS_PTR(v) || !in_region(v) || !meta[block_of((void *)v)].evacuate) return;
    value first = ((value *)(v & ~7))[0];
    *slot = (first & ~(value)0xF) | CALC_GC_TAG(v);   /* 태그(cons 의 2)는 그대로 */
}
```

### Step 4: 효과가 없는 경우를 블록 종류별로 본다

처음에는 크기별 블록과 cons 블록만 옮겼다. msort는 6.4GB에서 131MB가 되었지만 bst는 그대로였다. bst의 트리 노드는 pin 해서 넘겨받은 **크기가 섞인 블록**에 남는데, 그 블록은 옮기지 않았기 때문이다. 그런 블록은 헤더로 차례로 읽으며 mark 된 객체를 크기별 블록으로 옮기게 넓혔다. 그 뒤 bst도 69MB가 되었다.

### Step 5: "늘 옮기는" 설정으로 시험한다

기본 설정에서는 major GC가 드물고, 고른 블록이 32개 미만이면 옮기지 않는다. 그래서 테스트에는 옮길 수 있는 블록을 모두 옮기는 설정을 둔다.

```bash
CALC_GC_STRESS=100 CALC_GC_DEFRAG=100 CALC_GC_DEFRAG_MIN=1 CALC_GC_VERIFY=5 ./prog
```

heap verifier(`95-build-a-heap-verifier-for-a-gc.md`)가 옮긴 뒤 남은 옛 주소(빈 블록을 가리킴, forwarding 표시가 남음)를 잡는다.

## Example

테스트 환경 변수가 겹치는 함정도 있었다. GC 테스트 묶음이 heap verifier를 켜는데, `CALC_GC_STRESS=10 dotnet test`로 돌리면 실행 파일이 바깥의 stress까지 물려받는다. 그러면 할당 10번마다 GC하며 GC마다 힙 전체를 확인하게 되어, 프로그램 하나가 20분 넘게 돌았다. 그 묶음은 바깥의 `CALC_GC_*`를 지우고 자기 설정만 쓰게 했다.

```fsharp
for k in List.ofSeq psi.Environment.Keys do
    if k.StartsWith "CALC_GC_" then psi.Environment.Remove k |> ignore
```

## 체크리스트

- [ ] 힙 밖에서 힙을 가리키는 곳을 모두 적었는가?
- [ ] 고칠 수 없는 곳(stack, 레지스터)이 가리키는 블록을 제외하는가? 블록 경계의 포인터도?
- [ ] mark 뒤에 산 객체 비율로 고르는가?
- [ ] 옮길 곳을 고를 때 옮길 블록의 빈 칸을 쓰지 않는가?
- [ ] 블록 종류마다 효과를 쟀는가? (크기가 섞인 블록)
- [ ] 늘 옮기는 설정과 verifier로 시험하는가?
- [ ] 테스트가 바깥 환경 변수의 영향을 받지 않는가?

## 관련 문서

- `91-build-a-mostly-copying-generational-gc.md` - pinning과 블록 구조
- `93-recycle-pinned-blocks-and-grow-the-nursery.md` - 처음 본 단편화와 GC 통계, RSS
- `95-build-a-heap-verifier-for-a-gc.md` - 옮긴 뒤의 힙을 확인하는 verifier
