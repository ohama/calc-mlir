/* 세대 구분 GC: mostly-copying nursery + 옮기지 않는 old space
   (docs/generational-gc-design.md 4장, 11장. benchmarks/gc-model/gcm.c 의 모델과 같은 구조).
   - 모든 블록(32KB)은 미리 잡아 둔 한 영역에서 나옴. 블록마다 상태를 표(meta)에 둠
   - nursery: 일반 블록과 cons 블록에 따로 bump. 다시 쓸 때 0 으로 채움
   - minor GC: stack 과 레지스터가 가리키는 nursery 블록은 pin 해서 그 자리에서 old 로 넘김. 나머지 살아 있는 것은 복사
   - old space: 크기별 블록(free list), 큰 객체(블록 여러 개), 넘겨받은 nursery 블록. major GC 는 mark-sweep */
#include "runtime.h"

#include <pthread.h>
#include <setjmp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>

#define CALC_GC_TAG(v) ((v) & 7)
#define CALC_GC_IS_PTR(v) (CALC_GC_TAG(v) == 0 || CALC_GC_TAG(v) == 2)

#define BLOCK_SHIFT 15
#define BLOCK_BYTES ((int64_t)1 << BLOCK_SHIFT)
#define BLOCK_WORDS (BLOCK_BYTES / 8)
#define REGION_BYTES ((int64_t)1 << 35)
#define REGION_BLOCKS (REGION_BYTES >> BLOCK_SHIFT)
#define NURSERY_MAX_BLOCKS 32768 /* 1GB */
#define LARGE_WORDS 256 /* 이보다 큰 객체는 큰 객체 공간에 바로 */
#define FORWARDED 0xF   /* 옮긴 객체의 첫 단어: 새 주소 | 0xF */
#define FREE_SLOT 0x7F  /* old space 의 빈 칸의 첫 단어 (값과 헤더에 나오지 않음). 다음 빈 칸은 둘째 단어 */
#define ROUND16(n) (((n) + 15) & ~(int64_t)15)

enum { FREE = 0, NURSERY, NURSERY_CONS, OLD_SIZED, OLD_CONS, OLD_PROMOTED, OLD_PROMOTED_CONS, LARGE, LARGE_CONT };

typedef struct {
    uint8_t state;
    uint8_t pinned;
    uint8_t stack_ref;   /* major GC: stack 이나 레지스터가 이 블록을 가리킴 (옮기지 않음) */
    uint8_t evacuate;    /* major GC: 객체를 옮겨 비우는 블록 */
    uint16_t slot_words; /* OLD_SIZED, OLD_CONS 의 칸 크기 */
    uint32_t nblocks;    /* LARGE: 블록 수 */
    char *top;           /* NURSERY*, OLD_PROMOTED*, OLD_SIZED: 쓴 곳의 끝 */
    uint64_t *marks;     /* old 블록: 단어마다 mark bit (객체 시작 단어) */
    uint64_t *remembered; /* old 블록: 단어마다 remembered set 에 들어 있는지 (같은 칸을 한 번만 기록) */
} block_meta;

static char *region;
static block_meta *meta;
static int64_t fresh_block;            /* 아직 한 번도 쓰지 않은 첫 블록 */
static int64_t *free_blocks;           /* 돌려받은 블록 (0 으로 채워져 있지 않음) */
static int64_t free_count, free_cap;

static inline int64_t block_of(const void *p) { return ((const char *)p - region) >> BLOCK_SHIFT; }
static inline char *block_start(int64_t b) { return region + (b << BLOCK_SHIFT); }
static inline int in_region(value v) { return (uint64_t)((char *)v - region) < (uint64_t)REGION_BYTES; }

/* ---- 통계 ---- */
static int64_t stat_minor, stat_major, stat_copied_bytes, stat_pinned_blocks, stat_old_bytes_peak;

/* ---- 블록 ---- */
static int64_t take_block(int zero) {
    if (free_count > 0) {
        int64_t b = free_blocks[--free_count];
        if (zero) memset(block_start(b), 0, BLOCK_BYTES);
        return b;
    }
    if (fresh_block >= REGION_BLOCKS) { calc_error("out of memory"); }
    return fresh_block++;
}

static void give_block(int64_t b) {
    if (meta[b].marks) { free(meta[b].marks); meta[b].marks = NULL; }
    if (meta[b].remembered) { free(meta[b].remembered); meta[b].remembered = NULL; }
    meta[b].state = FREE;
    meta[b].pinned = 0;
    meta[b].evacuate = 0;
    if (free_count == free_cap) {
        free_cap = free_cap ? free_cap * 2 : 1024;
        free_blocks = realloc(free_blocks, (size_t)free_cap * sizeof(int64_t));
    }
    free_blocks[free_count++] = b;
}

static uint64_t *mark_bits(int64_t b) {
    if (!meta[b].marks) meta[b].marks = calloc(BLOCK_WORDS / 64, sizeof(uint64_t));
    return meta[b].marks;
}

/* ---- 객체 크기 (runtime.h 의 헤더 규칙) ---- */
static inline int64_t header_words(value h) {
    int64_t kind = h & 0xff;
    int64_t n = (uint64_t)h >> 8;
    if (kind == 5 || kind == 1) n &= 0xffffff;  /* data 는 위쪽 32비트에 생성자 번호, 클로저는 인자 수 (direct 함수가 있는 클로저) */
    if (kind == 9) return 1 + (n + 7) / 8;      /* bool 배열: n 은 바이트 수 */
    return 1 + n;
}
static inline int atomic_kind(value h) { int64_t k = h & 0xff; return k == 2 || k == 9; }

/* ---- nursery ---- */
/* nursery 블록 수: 1MB 에서 시작해 minor GC 마다 두 배, 16MB 까지 (할당이 적은 프로그램은 메모리를 적게) */
#define NURSERY_START_BLOCKS 32
#define NURSERY_FULL_BLOCKS 512
static int64_t nursery_budget = NURSERY_START_BLOCKS, base_budget = NURSERY_START_BLOCKS, full_budget = NURSERY_FULL_BLOCKS;
static int64_t nursery_used;
static int64_t *nursery_blocks;
static int64_t nursery_count;
static char *gen_top, *gen_limit, *cons_top, *cons_limit;
static int64_t gen_block = -1, cons_block = -1;

static void close_block(int64_t b, char *top) { if (b >= 0) meta[b].top = top; }

static int64_t nursery_block(int state) {
    int64_t b = take_block(1);
    meta[b].state = state;
    meta[b].pinned = 0;
    meta[b].top = block_start(b);
    nursery_blocks[nursery_count++] = b;
    nursery_used++;
    return b;
}

/* ---- old space ---- */
static const int16_t class_words[] = { 2, 4, 6, 8, 10, 12, 16, 20, 24, 32, 48, 64, 96, 128, 192, 256 };
#define CLASSES (int)(sizeof class_words / sizeof class_words[0])
static value *free_lists[CLASSES + 1];   /* 마지막은 cons */
static char *bump_top[CLASSES + 1], *bump_limit[CLASSES + 1];
static int64_t bump_block[CLASSES + 1];
static int64_t old_bytes, major_threshold = (int64_t)64 << 20, major_factor = 3;

static int class_of(int64_t words) {
    for (int i = 0; i < CLASSES; i++) if (class_words[i] >= words) return i;
    return -1;
}

static void *old_alloc_class(int c) {
    value *p = free_lists[c];
    if (p) { free_lists[c] = (value *)p[1]; old_bytes += class_words[c == CLASSES ? 0 : c] * 8; return p; }
    int64_t slot = (c == CLASSES ? 2 : class_words[c]) * 8;
    if (bump_top[c] == NULL || bump_top[c] + slot > bump_limit[c]) {
        if (bump_block[c] >= 0) meta[bump_block[c]].top = bump_top[c];
        int64_t b = take_block(0);
        meta[b].state = c == CLASSES ? OLD_CONS : OLD_SIZED;
        meta[b].slot_words = (uint16_t)(slot / 8);
        bump_block[c] = b;
        bump_top[c] = block_start(b);
        bump_limit[c] = bump_top[c] + (BLOCK_BYTES / slot) * slot;
        meta[b].top = bump_top[c];
    }
    void *r = bump_top[c];
    bump_top[c] += slot;
    meta[bump_block[c]].top = bump_top[c];
    old_bytes += slot;
    return r;
}

/* old space 에 바로 만든 큰 객체 (안에 포인터가 있는 것): 부른 쪽이 barrier 없이 채우므로 다음 minor GC 가 한 번 훑음 */
static value **fresh_large;
static int64_t fresh_large_count, fresh_large_cap;

static void *large_alloc(int64_t bytes, int atomic) {
    int64_t n = (bytes + BLOCK_BYTES - 1) >> BLOCK_SHIFT;
    if (fresh_block + n > REGION_BLOCKS) { calc_error("out of memory"); }
    int64_t b = fresh_block;
    fresh_block += n;
    meta[b].state = LARGE;
    meta[b].nblocks = (uint32_t)n;
    for (int64_t i = 1; i < n; i++) meta[b + i].state = LARGE_CONT;
    old_bytes += n * BLOCK_BYTES;
    if (!atomic) {
        if (fresh_large_count == fresh_large_cap) {
            fresh_large_cap = fresh_large_cap ? fresh_large_cap * 2 : 64;
            fresh_large = realloc(fresh_large, (size_t)fresh_large_cap * sizeof *fresh_large);
        }
        fresh_large[fresh_large_count++] = (value *)block_start(b);
    }
    return block_start(b);
}

/* ---- root ---- */
static value **roots;
static int64_t root_count, root_cap;
void calc_gc_add_root(value *slot) {
    if (root_count == root_cap) { root_cap = root_cap ? root_cap * 2 : 16; roots = realloc(roots, (size_t)root_cap * sizeof *roots); }
    roots[root_count++] = slot;
}

/* remembered set: write barrier 가 기록한 old 객체의 칸 */
static value **remembered;
static int64_t remembered_count, remembered_cap = 1 << 16;

static void minor_collect(void);

static inline int is_young_block(int64_t b) { return meta[b].state == NURSERY || meta[b].state == NURSERY_CONS; }
static inline int is_young(value v) { return CALC_GC_IS_PTR(v) && in_region(v) && is_young_block(block_of((void *)(v & ~7))); }

/* write barrier: 생성 코드와 런타임이 값을 쓴 뒤 부름 (쓰는 값이 포인터일 때). 오래된 객체의 칸에 젊은 객체를 썼으면 칸을 기록.
   칸마다 bit 를 두어 다음 minor GC 까지 한 번만 기록 (반복 안에서 같은 칸들에 거듭 씀). GC 를 일으키지 않음 */
void calc_write_barrier(value *slot, value v) {
    if (!is_young(v) || !in_region((value)slot)) return;
    int64_t b = block_of(slot);
    if (is_young_block(b)) return;
    if (!meta[b].remembered) meta[b].remembered = calloc(BLOCK_WORDS / 64, sizeof(uint64_t));
    int64_t w = ((char *)slot - block_start(b)) >> 3;
    uint64_t bit = 1ull << (w & 63);
    if (meta[b].remembered[w >> 6] & bit) return;
    meta[b].remembered[w >> 6] |= bit;
    if (remembered_count == remembered_cap) {
        remembered_cap *= 2;
        remembered = realloc(remembered, (size_t)remembered_cap * sizeof *remembered);
        if (!remembered) calc_error("out of memory");
    }
    remembered[remembered_count++] = slot;
}

/* ---- minor GC ---- */
static value **gray;      /* 복사한 객체 (주소) */
static int64_t gray_count, gray_cap;
static inline void push_gray(value *p) {
    if (gray_count == gray_cap) { gray_cap = gray_cap ? gray_cap * 2 : 4096; gray = realloc(gray, (size_t)gray_cap * sizeof *gray); }
    gray[gray_count++] = p;
}

static inline void forward(value *slot) {
    value v = *slot;
    if (!CALC_GC_IS_PTR(v) || !in_region(v)) return;
    value *p = (value *)(v & ~7);
    int64_t b = block_of(p);
    if (!is_young_block(b)) return;
    value first = p[0];
    if ((first & 0xF) == FORWARDED) { *slot = (first & ~(value)0xF) | CALC_GC_TAG(v); return; }
    value *q;
    if (CALC_GC_TAG(v) == 2) {
        /* cons: free list 보다 bump 를 먼저 (복사한 리스트가 메모리에 차례로 놓임) */
        if (bump_top[CLASSES] && bump_top[CLASSES] + 16 <= bump_limit[CLASSES]) {
            q = (value *)bump_top[CLASSES];
            bump_top[CLASSES] += 16;
            meta[bump_block[CLASSES]].top = bump_top[CLASSES];
            old_bytes += 16;
        } else q = old_alloc_class(CLASSES);
        q[0] = p[0];
        q[1] = p[1];
        stat_copied_bytes += 16;
    } else {
        int64_t words = header_words(first);
        q = old_alloc_class(class_of(words)); /* nursery 의 객체는 LARGE_WORDS 이하 */
        memcpy(q, p, (size_t)words * 8);
        stat_copied_bytes += words * 8;
    }
    p[0] = (value)q | FORWARDED;
    *slot = (value)q | CALC_GC_TAG(v);
    push_gray((value *)((value)q | CALC_GC_TAG(v))); /* 태그로 cons 인지 기억 */
}

static void scan_object(value *tagged) {
    if (((value)tagged & 7) == 2) {
        value *c = (value *)((value)tagged & ~7);
        forward(&c[0]);
        forward(&c[1]);
        return;
    }
    value h = tagged[0];
    if (atomic_kind(h)) return;
    int64_t words = header_words(h);
    for (int64_t i = 1; i < words; i++) forward(&tagged[i]);
}

/* pin 한 nursery 블록: 안의 객체를 차례로 훑음 */
static void scan_promoted(int64_t b) {
    char *p = block_start(b), *top = meta[b].top;
    if (meta[b].state == OLD_PROMOTED_CONS) {
        for (; p < top; p += 16) { forward(&((value *)p)[0]); forward(&((value *)p)[1]); }
    } else {
        while (p < top) {
            value h = ((value *)p)[0];
            if (h == 0) break; /* 할당했지만 아직 헤더를 쓰지 않은 칸 (생기지 않음) */
            int64_t words = header_words(h);
            if (!atomic_kind(h)) for (int64_t i = 1; i < words; i++) forward(&((value *)p)[i]);
            p += ROUND16(words * 8);
        }
    }
}

static char *stack_base;

static void pin_range(value *lo, value *hi) {
    for (value *s = lo; s < hi; s++) {
        value v = *s;
        if (!in_region(v)) continue;
        int64_t b = block_of((void *)v);
        if (is_young_block(b)) meta[b].pinned = 1;
        /* 블록 끝 바로 다음을 가리키는 포인터 (반복의 끝 주소) */
        if ((((char *)v - region) & (BLOCK_BYTES - 1)) == 0 && b > 0 && is_young_block(b - 1)) meta[b - 1].pinned = 1;
    }
}

/* ---- heap verifier (CALC_GC_VERIFY=n, 테스트용: n 번째 GC 마다) ----
   root(stack, 레지스터, 런타임 전역)에서 닿는 객체를 모두 따라가며 확인 (docs/generational-gc-design.md 6장, 16장).
   - 힙 안의 포인터는 살아 있는 객체의 시작을 가리킴 (cons 는 + 2, 태그와 블록 종류가 맞음, 빈 블록이나 빈 칸이 아님)
   - forwarding 표시가 남은 객체가 없음. 헤더의 크기가 블록 안에 들어감
   - minor GC 전: old 객체의 칸이 nursery 객체를 가리키면 그 칸이 remembered set 에 있음 (write barrier)
   - minor GC 뒤: nursery 객체를 가리키는 것이 없음
   틀리면 무엇이 어디서 틀렸는지 출력하고 abort */
static value object_of(value v, int exact);
enum { VERIFY_BEFORE_MINOR, VERIFY_AFTER_MINOR, VERIFY_AFTER_MAJOR };
static const char *verify_phase_names[] = { "before minor GC", "after minor GC", "after major GC" };
static int64_t verify_every, verify_tick, stat_verified;
static int verify_phase;
static uint64_t **verify_seen; /* 블록마다 단어당 bit: 이미 따라간 객체 */
static int64_t verify_seen_blocks;
static value **verify_stack;
static int64_t verify_count, verify_cap;

static void __attribute__((noreturn)) verify_fail(const char *what, value *slot, value v) {
    int64_t sb = in_region((value)slot) ? block_of(slot) : -1, vb = in_region(v) ? block_of((void *)v) : -1;
    fprintf(stderr, "gc verify failed (%s, GC #%lld): %s\n  slot %p (block %lld, state %d), value 0x%llx (block %lld, state %d)\n",
            verify_phase_names[verify_phase], (long long)(stat_minor + stat_major), what, (void *)slot, (long long)sb,
            sb >= 0 ? meta[sb].state : -1, (unsigned long long)v, (long long)vb, vb >= 0 ? meta[vb].state : -1);
    abort();
}

/* 쓴 곳의 끝: 지금 bump 하는 nursery 블록은 top 을 아직 적지 않음 */
static char *verify_top(int64_t b) {
    if (b == gen_block) return gen_top;
    if (b == cons_block) return cons_top;
    return meta[b].top;
}

static int is_cons_block(int s) { return s == NURSERY_CONS || s == OLD_CONS || s == OLD_PROMOTED_CONS; }

/* v 를 담은 객체의 시작 (태그 포함 모양). 없으면 0. object_of 와 같고 nursery 블록도 봄 */
static value verify_object_of(value v) {
    if (!in_region(v)) return 0;
    int64_t b = block_of((void *)v);
    char *bs = block_start(b), *top = verify_top(b);
    switch (meta[b].state) {
    case NURSERY_CONS: {
        int64_t off = ((char *)v - bs) & ~(int64_t)15;
        return bs + off < top ? (value)(bs + off) | 2 : 0;
    }
    case NURSERY: {
        for (char *p = bs; p < top;) {
            value h = ((value *)p)[0];
            if (h == 0) return 0;
            int64_t words = header_words(h);
            if ((char *)v < p + words * 8) return (value)p;
            p += ROUND16(words * 8);
        }
        return 0;
    }
    default: return object_of(v, 0);
    }
}

static int verify_mark(value o) {
    value *p = (value *)(o & ~7);
    int64_t b = block_of(p);
    if (!verify_seen[b]) verify_seen[b] = calloc(BLOCK_WORDS / 64, sizeof(uint64_t));
    int64_t w = ((char *)p - block_start(b)) >> 3;
    if (verify_seen[b][w >> 6] & (1ull << (w & 63))) return 0;
    verify_seen[b][w >> 6] |= 1ull << (w & 63);
    if (verify_count == verify_cap) {
        verify_cap = verify_cap ? verify_cap * 2 : 4096;
        verify_stack = realloc(verify_stack, (size_t)verify_cap * sizeof *verify_stack);
    }
    verify_stack[verify_count++] = (value *)o;
    return 1;
}

static int in_fresh_large(value *o) {
    for (int64_t i = 0; i < fresh_large_count; i++) if (fresh_large[i] == o) return 1;
    return 0;
}

/* 힙 객체 owner 의 칸 slot 에 든 값 (owner 가 NULL 이면 런타임 전역) */
static void verify_pointer(value *owner, value *slot) {
    value v = *slot;
    if (!CALC_GC_IS_PTR(v) || !in_region(v)) return; /* 즉시값, 0 (아직 채우지 않은 칸), 힙 밖 (코드 포인터, 필드 이름) */
    int64_t b = block_of((void *)v);
    int s = meta[b].state;
    if (s == FREE) verify_fail("points to a free block", slot, v);
    if (s == LARGE_CONT) verify_fail("points into the middle of a large object", slot, v);
    if ((CALC_GC_TAG(v) == 2) != is_cons_block(s)) verify_fail("pointer tag does not match the block kind", slot, v);
    if (verify_object_of(v) != v) verify_fail("does not point to the start of an object", slot, v);
    value first = ((value *)(v & ~7))[0];
    if ((first & 0xF) == FORWARDED) verify_fail("points to a forwarded object", slot, v);
    if (first == FREE_SLOT && (s == OLD_SIZED || s == OLD_CONS)) verify_fail("points to a freed slot", slot, v);
    int young = s == NURSERY || s == NURSERY_CONS;
    if (young && verify_phase != VERIFY_BEFORE_MINOR) verify_fail("points into the nursery after GC", slot, v);
    if (young && owner && !is_young_block(block_of(owner)) && !in_fresh_large((value *)((value)owner & ~7))) {
        int64_t ob = block_of(slot);
        int64_t w = ((char *)slot - block_start(ob)) >> 3;
        if (!meta[ob].remembered || !(meta[ob].remembered[w >> 6] & (1ull << (w & 63))))
            verify_fail("old object points to a young object without a write barrier", slot, v);
    }
    verify_mark(v);
}

static void verify_object(value *tagged) {
    if (((value)tagged & 7) == 2) {
        value *c = (value *)((value)tagged & ~7);
        verify_pointer(c, &c[0]);
        verify_pointer(c, &c[1]);
        return;
    }
    value h = tagged[0];
    int64_t kind = h & 0xff;
    if (kind < 1 || kind > 9 || kind == 3) verify_fail("bad header", tagged, h);
    int64_t words = header_words(h), b = block_of(tagged);
    char *limit = meta[b].state == OLD_SIZED ? (char *)tagged + meta[b].slot_words * 8
                : meta[b].state == LARGE ? block_start(b) + (int64_t)meta[b].nblocks * BLOCK_BYTES
                : block_start(b) + BLOCK_BYTES;
    if ((char *)tagged + words * 8 > limit) verify_fail("object size runs past its slot or block", tagged, h);
    if (atomic_kind(h)) return;
    for (int64_t i = 1; i < words; i++) verify_pointer(tagged, &tagged[i]);
}

/* stack 과 레지스터의 단어는 conservative: 객체 안 어디를 가리켜도 그 객체를 따라감 */
static void verify_range(value *lo, value *hi) {
    for (value *s = lo; s < hi; s++) {
        value o = verify_object_of(*s);
        if (o) verify_mark(o);
    }
}

static void verify_heap(int phase, value *stack_lo, jmp_buf regs) {
    if (!verify_every || ++verify_tick < verify_every) return;
    verify_tick = 0;
    verify_phase = phase;
    stat_verified++;
    verify_seen_blocks = fresh_block;
    verify_seen = calloc((size_t)fresh_block + 1, sizeof *verify_seen);
    /* minor GC 뒤에는 nursery 블록이 하나도 없음 (모두 돌려주거나 old 로 넘김) */
    if (phase != VERIFY_BEFORE_MINOR)
        for (int64_t b = 0; b < fresh_block; b++)
            if (is_young_block(b)) verify_fail("nursery block was neither freed nor promoted", (value *)block_start(b), 0);
    verify_range(stack_lo, (value *)stack_base);
    verify_range((value *)regs, (value *)((char *)regs + sizeof(jmp_buf)));
    for (int64_t i = 0; i < root_count; i++) {
        value v = *roots[i];
        if (CALC_GC_IS_PTR(v) && in_region(v)) verify_pointer(NULL, roots[i]);
    }
    /* 이번 minor GC 가 훑을 큰 객체와 remembered set 의 칸도 확인 */
    if (phase == VERIFY_BEFORE_MINOR)
        for (int64_t i = 0; i < fresh_large_count; i++) verify_mark((value)fresh_large[i]);
    while (verify_count > 0) verify_object(verify_stack[--verify_count]);
    for (int64_t b = 0; b < verify_seen_blocks; b++) free(verify_seen[b]);
    free(verify_seen);
    verify_seen = NULL;
}

static void major_collect(void);

static void __attribute__((noinline)) minor_collect(void) {
    stat_minor++;
    close_block(gen_block, gen_top);
    close_block(cons_block, cons_top);
    /* 1. stack 과 레지스터 (setjmp 로 레지스터를 이 프레임에 내림) */
    jmp_buf regs;
    setjmp(regs);
    value here;
    value *stack_lo = (value *)((uintptr_t)&here & ~(uintptr_t)7);
    verify_heap(VERIFY_BEFORE_MINOR, stack_lo, regs);
    pin_range(stack_lo, (value *)stack_base);
    /* 훑은 stack 이 크면 다음 nursery 를 키움: minor GC 마다 stack 을 다시 훑으므로, 할당량을 stack 크기의 두 배 이상으로 */
    int64_t stack_blocks = ((stack_base - (char *)&here) * 2) >> BLOCK_SHIFT;
    if (base_budget < full_budget) base_budget = base_budget * 2 < full_budget ? base_budget * 2 : full_budget;
    nursery_budget = stack_blocks > base_budget ? stack_blocks : base_budget;
    if (nursery_budget > NURSERY_MAX_BLOCKS) nursery_budget = NURSERY_MAX_BLOCKS;
    pin_range((value *)regs, (value *)((char *)regs + sizeof regs));
    /* 2. pin 한 블록은 old 로 넘김 (상태를 먼저 바꿔서 forward 가 건드리지 않게) */
    int64_t kept = 0;
    for (int64_t i = 0; i < nursery_count; i++) {
        int64_t b = nursery_blocks[i];
        if (!meta[b].pinned) continue;
        meta[b].state = meta[b].state == NURSERY_CONS ? OLD_PROMOTED_CONS : OLD_PROMOTED;
        meta[b].pinned = 0;
        stat_pinned_blocks++;
        old_bytes += BLOCK_BYTES;
        nursery_blocks[i] = nursery_blocks[kept]; /* 앞쪽에 모음 (자리를 바꿔서, 5 에서 나머지 블록을 모두 돌려줌) */
        nursery_blocks[kept++] = b;
    }
    int64_t promoted = kept;
    /* 3. root: 런타임 전역, remembered set, 넘긴 블록 */
    for (int64_t i = 0; i < root_count; i++) forward(roots[i]);
    for (int64_t i = 0; i < remembered_count; i++) {
        value *slot = remembered[i];
        int64_t b = block_of(slot);
        int64_t w = ((char *)slot - block_start(b)) >> 3;
        if (meta[b].remembered) meta[b].remembered[w >> 6] &= ~(1ull << (w & 63));
        forward(slot);
    }
    remembered_count = 0;
    for (int64_t i = 0; i < promoted; i++) scan_promoted(nursery_blocks[i]);
    for (int64_t i = 0; i < fresh_large_count; i++) scan_object(fresh_large[i]);
    fresh_large_count = 0;
    /* 4. 복사한 객체를 훑음 */
    while (gray_count > 0) scan_object(gray[--gray_count]);
    /* 5. 남은 nursery 블록은 돌려줌 */
    for (int64_t i = 0; i < nursery_count; i++) {
        int64_t b = nursery_blocks[i];
        if (is_young_block(b)) give_block(b);
    }
    nursery_count = 0;
    nursery_used = 0;
    gen_block = cons_block = -1;
    gen_top = gen_limit = cons_top = cons_limit = NULL;
    if (old_bytes > stat_old_bytes_peak) stat_old_bytes_peak = old_bytes;
    verify_heap(VERIFY_AFTER_MINOR, stack_lo, regs);
    if (old_bytes > major_threshold) major_collect();
}



/* ---- 할당 ---- */
/* GC stress 모드 (CALC_GC_STRESS=n, 테스트용): 할당 n 번마다 minor GC. 빠른 길의 limit 을 비워 두어 할당이 모두 느린 길로 오고,
   블록의 실제 끝은 gen_end, cons_end 에 둠. 블록마다 객체를 STRESS_PER_BLOCK 개만 넣어서, stack 이 가리키는 최근 블록만
   pin 되고 나머지 객체는 복사됨 (한 블록에 다 넣으면 그 블록이 통째로 pin 되어 복사를 시험하지 못함) */
#define STRESS_PER_BLOCK 4
static int64_t stress_every, stress_count, gen_in_block, cons_in_block;
static char *gen_end, *cons_end;

static int stress_tick(void) {
    if (++stress_count < stress_every) return 0;
    stress_count = 0;
    minor_collect();
    return 1;
}

static void *__attribute__((noinline)) gen_slow(int64_t bytes, int atomic) {
    if (bytes > LARGE_WORDS * 8) return large_alloc(bytes, atomic);
    if (stress_every) {
        stress_tick();
        if (gen_block >= 0 && gen_top + bytes <= gen_end && gen_in_block < STRESS_PER_BLOCK) {
            gen_in_block++;
            void *p = gen_top;
            gen_top += bytes;
            return p;
        }
        gen_in_block = 1;
    }
    close_block(gen_block, gen_top);
    if (nursery_used >= nursery_budget) minor_collect();
    gen_block = nursery_block(NURSERY);
    gen_top = block_start(gen_block);
    gen_end = gen_top + BLOCK_BYTES;
    gen_limit = stress_every ? NULL : gen_end;
    void *p = gen_top;
    gen_top += bytes;
    return p;
}

/* 헤더가 있는 블록 (bytes 는 헤더 포함). 내용은 0. 부른 쪽이 다음 할당 전에 헤더를 씀 */
void *calc_alloc(int64_t bytes) {
    bytes = (bytes + 15) & ~(int64_t)15;
    if (gen_top + bytes <= gen_limit) { void *p = gen_top; gen_top += bytes; return p; }
    return gen_slow(bytes, 0);
}

/* 안에 포인터가 없는 블록 (문자열, bool 배열). nursery 에서는 calc_alloc 과 같은 곳, 큰 객체는 훑지 않음 */
void *calc_alloc_atomic(int64_t bytes) {
    bytes = (bytes + 15) & ~(int64_t)15;
    if (gen_top + bytes <= gen_limit) { void *p = gen_top; gen_top += bytes; return p; }
    return gen_slow(bytes, 1);
}

static void *__attribute__((noinline)) cons_slow(void) {
    if (stress_every) {
        stress_tick();
        if (cons_block >= 0 && cons_top + 16 <= cons_end && cons_in_block < STRESS_PER_BLOCK) {
            cons_in_block++;
            void *c = cons_top;
            cons_top += 16;
            return c;
        }
        cons_in_block = 1;
    }
    close_block(cons_block, cons_top);
    if (nursery_used >= nursery_budget) minor_collect();
    cons_block = nursery_block(NURSERY_CONS);
    cons_top = block_start(cons_block);
    cons_end = cons_top + BLOCK_BYTES;
    cons_limit = stress_every ? NULL : cons_end;
    void *c = cons_top;
    cons_top += 16;
    return c;
}

/* cons 칸 [head, tail] 16바이트 (cons 만 담는 블록). 부른 쪽이 다음 할당 전에 채움 */
void *calc_alloc_cons(void) {
    if (cons_top + 16 <= cons_limit) { void *c = cons_top; cons_top += 16; return c; }
    return cons_slow();
}

/* ---- major GC ---- */
static value **mark_stack;
static int64_t mark_count, mark_cap;
static inline void push_mark(value *p) {
    if (mark_count == mark_cap) { mark_cap = mark_cap ? mark_cap * 2 : 4096; mark_stack = realloc(mark_stack, (size_t)mark_cap * sizeof *mark_stack); }
    mark_stack[mark_count++] = p;
}

/* 객체 시작 (태그 포함 모양: cons 는 + 2). 없으면 0. exact 는 힙 안의 정확한 포인터 */
static value object_of(value v, int exact) {
    if (!in_region(v)) return 0;
    int64_t b = block_of((void *)v);
    char *bs = block_start(b);
    switch (meta[b].state) {
    case OLD_CONS: {
        if (exact) return v;
        int64_t off = ((char *)v - bs) & ~(int64_t)15;
        if (bs + off >= meta[b].top || ((value *)(bs + off))[0] == FREE_SLOT) return 0;
        return (value)(bs + off) | 2;
    }
    case OLD_SIZED: {
        if (exact) return v;
        int64_t slot = meta[b].slot_words * 8;
        int64_t off = (((char *)v - bs) / slot) * slot;
        if (bs + off >= meta[b].top || ((value *)(bs + off))[0] == FREE_SLOT) return 0;
        return (value)(bs + off);
    }
    case OLD_PROMOTED_CONS: {
        if (exact) return v;
        int64_t off = ((char *)v - bs) & ~(int64_t)15;
        if (bs + off >= meta[b].top) return 0;
        return (value)(bs + off) | 2;
    }
    case OLD_PROMOTED: {
        if (exact) return v;
        char *p = bs;
        while (p < meta[b].top) {
            value h = ((value *)p)[0];
            if (h == 0) return 0;
            int64_t words = header_words(h);
            if ((char *)v < p + words * 8) return (value)p;
            p += ROUND16(words * 8);
        }
        return 0;
    }
    case LARGE: return (value)bs;
    case LARGE_CONT: {
        while (meta[b].state == LARGE_CONT) b--;
        return (value)block_start(b);
    }
    default: return 0;
    }
}

static inline void mark_value(value v, int exact) {
    value o = object_of(v, exact);
    if (!o) return;
    value *p = (value *)(o & ~7);
    int64_t b = block_of(p);
    uint64_t *bits = mark_bits(b);
    int64_t w = ((char *)p - block_start(b)) >> 3;
    if (bits[w >> 6] & (1ull << (w & 63))) return;
    bits[w >> 6] |= 1ull << (w & 63);
    push_mark((value *)o);
}

/* mark stack 이 빌 때까지: cons 는 head 와 tail, 블록은 헤더 뒤의 값 (문자열, bool 배열은 안을 보지 않음) */
static void drain_marks(void) {
    while (mark_count > 0) {
        value *o = mark_stack[--mark_count];
        if (((value)o & 7) == 2) {
            value *c = (value *)((value)o & ~7);
            if (CALC_GC_IS_PTR(c[0])) mark_value(c[0], 1);
            if (CALC_GC_IS_PTR(c[1])) mark_value(c[1], 1);
            continue;
        }
        value h = o[0];
        if (atomic_kind(h)) continue;
        int64_t words = header_words(h);
        for (int64_t i = 1; i < words; i++) if (CALC_GC_IS_PTR(o[i])) mark_value(o[i], 1);
    }
}

/* stack 과 레지스터: conservative 하게 mark 하고, 가리키는 블록은 옮기지 않게 표시 (객체 끝 바로 다음을 가리키는 포인터도 있으므로 앞 블록도) */
static void mark_range(value *lo, value *hi) {
    for (value *s = lo; s < hi; s++) {
        value v = *s;
        if (in_region(v)) meta[block_of((void *)v)].stack_ref = 1;
        if (in_region(v - 1)) meta[block_of((void *)(v - 1))].stack_ref = 1;
        mark_value(v, 0);
    }
}

/* ---- defragmentation (major GC 의 evacuation) ----
   pin 이 잦으면 old space 의 크기별 블록과 넘겨받은 블록마다 산 객체가 몇 개씩 흩어져 남아 블록을 돌려주지 못함 (14.1, 17장).
   mark 한 뒤 산 객체가 적은 블록(크기별 블록, cons 블록, 넘겨받은 일반 블록)을 골라 객체를 다른 블록으로 옮기고, 산 객체와 root 의 포인터를 고친 다음, sweep 이 빈 블록을 돌려줌.
   stack 과 레지스터는 고칠 수 없으므로 그것이 가리키는 블록은 옮기지 않음. major GC 는 minor GC 바로 뒤라 nursery 와
   remembered set 은 비어 있음 */
static int64_t defrag_live_percent = 25; /* 산 칸이 이 비율 이하인 블록을 옮김 (CALC_GC_DEFRAG, 0 이면 하지 않음) */
static int64_t defrag_min_blocks = 32;   /* 고른 블록이 이보다 적으면 하지 않음 (CALC_GC_DEFRAG_MIN) */
static int64_t stat_evacuated_blocks, stat_evacuated_bytes;

static int64_t live_slots(int64_t b) {
    uint64_t *bits = meta[b].marks;
    int64_t n = 0;
    if (bits) for (int64_t i = 0; i < BLOCK_WORDS / 64; i++) n += __builtin_popcountll(bits[i]);
    return n;
}

/* pin 해서 넘겨받은 일반 블록에서 mark 된 객체가 차지하는 바이트 */
static int64_t promoted_live_bytes(int64_t b) {
    char *bs = block_start(b);
    int64_t n = 0;
    for (char *p = bs; p < meta[b].top;) {
        value h = ((value *)p)[0];
        if (h == 0) break;
        int64_t bytes = ROUND16(header_words(h) * 8), w = (p - bs) >> 3;
        if (meta[b].marks[w >> 6] & (1ull << (w & 63))) n += bytes;
        p += bytes;
    }
    return n;
}

static int evacuation_candidate(int64_t b) {
    block_meta *m = &meta[b];
    if (m->stack_ref || !m->marks) return 0;
    if (m->state == OLD_PROMOTED) return promoted_live_bytes(b) * 100 <= BLOCK_BYTES * defrag_live_percent;
    if (m->state != OLD_SIZED && m->state != OLD_CONS) return 0;
    int c = m->state == OLD_CONS ? CLASSES : class_of(m->slot_words);
    if (b == bump_block[c]) return 0;
    int64_t capacity = BLOCK_BYTES / (m->slot_words * 8);
    return live_slots(b) * 100 <= capacity * defrag_live_percent;
}

/* 산 객체의 칸이 옮긴 객체를 가리키면 새 주소로 */
static inline void fix_slot(value *slot) {
    value v = *slot;
    if (!CALC_GC_IS_PTR(v) || !in_region(v) || !meta[block_of((void *)v)].evacuate) return;
    value first = ((value *)(v & ~7))[0];
    *slot = (first & ~(value)0xF) | CALC_GC_TAG(v);
}

static void fix_object(value *p, int cons) {
    if (cons) { fix_slot(&p[0]); fix_slot(&p[1]); return; }
    value h = p[0];
    if (atomic_kind(h)) return;
    int64_t words = header_words(h);
    for (int64_t i = 1; i < words; i++) fix_slot(&p[i]);
}

static void evacuate(void) {
    if (!defrag_live_percent) return;
    int64_t count = 0;
    for (int64_t b = 0; b < fresh_block; b++) if (evacuation_candidate(b)) count++;
    if (count < defrag_min_blocks) return;
    for (int64_t b = 0; b < fresh_block; b++) if (evacuation_candidate(b)) meta[b].evacuate = 1;
    /* 옮길 곳은 bump 로만 (free list 의 빈 칸은 옮길 블록에도 있음. free list 는 sweep 이 다시 만듦) */
    for (int c = 0; c <= CLASSES; c++) free_lists[c] = NULL;
    for (int64_t b = 0; b < fresh_block; b++) {
        block_meta *m = &meta[b];
        if (!m->evacuate) continue;
        if (m->state == OLD_PROMOTED) {
            /* 크기가 섞인 블록: 헤더로 차례로 읽으며 mark 된 객체를 크기별 블록으로 */
            char *bs = block_start(b);
            for (char *p = bs; p < m->top;) {
                value h = ((value *)p)[0];
                if (h == 0) break;
                int64_t words = header_words(h), w = (p - bs) >> 3;
                if (m->marks[w >> 6] & (1ull << (w & 63))) {
                    value *q = old_alloc_class(class_of(words)); /* nursery 에서 온 객체는 LARGE_WORDS 이하 */
                    memcpy(q, p, (size_t)words * 8);
                    int64_t qb = block_of(q), qw = ((char *)q - block_start(qb)) >> 3;
                    mark_bits(qb)[qw >> 6] |= 1ull << (qw & 63);
                    ((value *)p)[0] = (value)q | FORWARDED;
                    stat_evacuated_bytes += words * 8;
                }
                p += ROUND16(words * 8);
            }
            memset(m->marks, 0, BLOCK_WORDS / 8);
            stat_evacuated_blocks++;
            continue;
        }
        int cons = m->state == OLD_CONS;
        int c = cons ? CLASSES : class_of(m->slot_words);
        int64_t slot = m->slot_words * 8;
        char *bs = block_start(b);
        for (int64_t i = 0; i < BLOCK_WORDS / 64; i++) {
            for (uint64_t x = m->marks[i]; x; x &= x - 1) {
                value *p = (value *)(bs + ((i * 64 + __builtin_ctzll(x)) << 3));
                value *q = old_alloc_class(c);
                memcpy(q, p, (size_t)slot);
                int64_t qb = block_of(q), qw = ((char *)q - block_start(qb)) >> 3;
                mark_bits(qb)[qw >> 6] |= 1ull << (qw & 63);
                p[0] = (value)q | FORWARDED;
                stat_evacuated_bytes += slot;
            }
        }
        memset(m->marks, 0, BLOCK_WORDS / 8); /* sweep 이 블록을 돌려줌 */
        stat_evacuated_blocks++;
    }
    /* 산 객체(mark bit 이 있는 것)와 root 의 포인터를 고침. 옮긴 객체는 새 블록에서 mark 되어 있어 같이 고쳐짐 */
    for (int64_t b = 0; b < fresh_block; b++) {
        block_meta *m = &meta[b];
        if (!m->marks) continue;
        char *bs = block_start(b);
        switch (m->state) {
        case OLD_SIZED: case OLD_CONS: case OLD_PROMOTED: case OLD_PROMOTED_CONS: {
            int cons = m->state == OLD_CONS || m->state == OLD_PROMOTED_CONS;
            for (int64_t i = 0; i < BLOCK_WORDS / 64; i++)
                for (uint64_t x = m->marks[i]; x; x &= x - 1)
                    fix_object((value *)(bs + ((i * 64 + __builtin_ctzll(x)) << 3)), cons);
            break;
        }
        case LARGE:
            if (m->marks[0] & 1) fix_object((value *)bs, 0);
            b += m->nblocks - 1;
            break;
        default: break;
        }
    }
    for (int64_t i = 0; i < root_count; i++) fix_slot(roots[i]);
}

static void sweep(void) {
    for (int c = 0; c <= CLASSES; c++) free_lists[c] = NULL;
    old_bytes = 0;
    for (int64_t b = 0; b < fresh_block; b++) {
        block_meta *m = &meta[b];
        uint64_t *bits = m->marks;
        m->stack_ref = 0;
        m->evacuate = 0;
        switch (m->state) {
        case OLD_PROMOTED_CONS:
            /* pin 해서 넘겨받은 cons 블록은 old cons 블록과 모양이 같음 (블록 시작부터 16바이트 칸). 살아 있는 칸이 있으면
               old cons 블록으로 바꿔서 죽은 칸을 free list 로 다시 씀 (긴 리스트의 칸 몇 개 때문에 블록이 통째로 남지 않게) */
            if (!bits) { give_block(b); break; }
            m->state = OLD_CONS;
            m->slot_words = 2;
            __attribute__((fallthrough));
        case OLD_SIZED:
        case OLD_CONS: {
            int c = m->state == OLD_CONS ? CLASSES : class_of(m->slot_words);
            int64_t slot = m->slot_words * 8, live = 0;
            char *bs = block_start(b);
            for (char *p = bs; p < m->top; p += slot) {
                int64_t w = (p - bs) >> 3;
                if (bits && (bits[w >> 6] & (1ull << (w & 63)))) live++;
            }
            if (live == 0 && b != bump_block[c]) { give_block(b); break; }
            for (char *p = bs; p < m->top; p += slot) {
                int64_t w = (p - bs) >> 3;
                if (bits && (bits[w >> 6] & (1ull << (w & 63)))) continue;
                ((value *)p)[0] = FREE_SLOT;
                ((value *)p)[1] = (value)free_lists[c];
                free_lists[c] = (value *)p;
            }
            old_bytes += live * slot;
            break;
        }
        case OLD_PROMOTED: {
            int any = 0;
            if (bits) for (int64_t i = 0; i < BLOCK_WORDS / 64; i++) if (bits[i]) { any = 1; break; }
            if (!any) { give_block(b); break; }
            old_bytes += BLOCK_BYTES;
            /* 남기는 블록의 죽은 객체는 이번에 돌려준 객체를 가리킬 수 있음. stack 의 오래된 단어가 나중에 그 객체를 가리키면
               major GC 가 돌려준 칸을 따라가므로, 죽은 객체의 칸을 0 으로 지움 (헤더는 남겨 블록을 차례로 읽을 수 있게) */
            char *bs = block_start(b);
            for (char *p = bs; p < m->top;) {
                value h = ((value *)p)[0];
                if (h == 0) break;
                int64_t words = header_words(h), w = (p - bs) >> 3;
                if (!atomic_kind(h) && !(bits[w >> 6] & (1ull << (w & 63)))) memset(p + 8, 0, (size_t)(words - 1) * 8);
                p += ROUND16(words * 8);
            }
            break;
        }
        case LARGE: {
            int64_t n = m->nblocks;
            if (bits && (bits[0] & 1)) {
                old_bytes += n * BLOCK_BYTES;
                memset(bits, 0, BLOCK_WORDS / 8);
            } else {
                for (int64_t i = 0; i < n; i++) { madvise(block_start(b + i), BLOCK_BYTES, MADV_FREE); give_block(b + i); }
            }
            b += n - 1;
            continue;
        }
        default: break;
        }
        if (meta[b].marks) memset(meta[b].marks, 0, BLOCK_WORDS / 8);
    }
}

static void __attribute__((noinline)) major_collect(void) {
    stat_major++;
    /* nursery 는 비어 있음 (minor GC 바로 뒤) */
    jmp_buf regs;
    setjmp(regs);
    value here;
    value *stack_lo = (value *)((uintptr_t)&here & ~(uintptr_t)7);
    mark_range(stack_lo, (value *)stack_base);
    mark_range((value *)regs, (value *)((char *)regs + sizeof regs));
    for (int64_t i = 0; i < root_count; i++) mark_value(*roots[i], 1);
    drain_marks();
    evacuate();
    sweep();
    verify_heap(VERIFY_AFTER_MAJOR, stack_lo, regs);
    major_threshold = old_bytes * major_factor > ((int64_t)64 << 20) ? old_bytes * major_factor : ((int64_t)64 << 20);
}

/* ---- 초기화 ---- */
void calc_gc_init(void) {
    region = mmap(NULL, (size_t)REGION_BYTES, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON | MAP_NORESERVE, -1, 0);
    if (region == MAP_FAILED) calc_error("out of memory");
    /* 블록 경계에 맞춤 (앞쪽 일부를 버림) */
    char *aligned = (char *)(((uintptr_t)region + BLOCK_BYTES - 1) & ~(uintptr_t)(BLOCK_BYTES - 1));
    region = aligned;
    meta = mmap(NULL, (size_t)REGION_BLOCKS * sizeof(block_meta), PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON, -1, 0);
    fresh_block = 0;
    const char *kb = getenv("CALC_GC_NURSERY_KB");
    if (kb) {
        /* 크기를 정하면 처음부터 그 크기 (키우지 않음) */
        nursery_budget = atoll(kb) * 1024 / BLOCK_BYTES;
        if (nursery_budget < 2) nursery_budget = 2;
        if (nursery_budget > NURSERY_MAX_BLOCKS) nursery_budget = NURSERY_MAX_BLOCKS;
        full_budget = nursery_budget;
    }
    base_budget = nursery_budget;
    const char *stress = getenv("CALC_GC_STRESS");
    if (stress) stress_every = atoll(stress);
    const char *mf = getenv("CALC_GC_MAJOR_FACTOR");
    if (mf) major_factor = atoll(mf);
    const char *defrag = getenv("CALC_GC_DEFRAG");
    if (defrag) defrag_live_percent = atoll(defrag);
    const char *defrag_min = getenv("CALC_GC_DEFRAG_MIN");
    if (defrag_min) defrag_min_blocks = atoll(defrag_min);
    const char *verify = getenv("CALC_GC_VERIFY");
    if (verify) verify_every = atoll(verify);
    nursery_blocks = malloc((size_t)(NURSERY_MAX_BLOCKS + 2) * sizeof(int64_t)); /* nursery 가 stack 에 맞춰 커질 수 있음 */
    remembered = malloc((size_t)remembered_cap * sizeof *remembered);
    for (int c = 0; c <= CLASSES; c++) bump_block[c] = -1;
    stack_base = pthread_get_stackaddr_np(pthread_self());
}

void calc_gc_print_stats(void) {
    if (!getenv("CALC_GC_STATS")) return;
    fprintf(stderr, "gc: minor %lld, major %lld, copied %lld MB, pinned blocks %lld, old peak %lld MB, blocks used %lld (%lld MB), evacuated %lld blocks (%lld MB), verified %lld\n",
            (long long)stat_minor, (long long)stat_major, (long long)(stat_copied_bytes >> 20), (long long)stat_pinned_blocks,
            (long long)(stat_old_bytes_peak >> 20), (long long)fresh_block, (long long)((fresh_block * BLOCK_BYTES) >> 20), (long long)stat_evacuated_blocks,
            (long long)(stat_evacuated_bytes >> 20), (long long)stat_verified);
}
