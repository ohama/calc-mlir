/* 세대 구분 GC 모델: mostly-copying nursery + 옮기지 않는 old space (docs/generational-gc-design.md 4장).
   - 모든 블록(32KB)은 미리 잡아 둔 한 영역에서 나옴. 블록마다 상태를 표(meta)에 둠
   - nursery: 일반 블록과 cons 블록에 따로 bump. 다시 쓸 때 0 으로 채움
   - minor GC: stack 과 레지스터가 가리키는 nursery 블록은 pin 해서 그 자리에서 old 로 넘김. 나머지 살아 있는 것은 복사
   - old space: 크기별 블록(free list), 큰 객체(블록 여러 개), 넘겨받은 nursery 블록. major GC 는 mark-sweep */
#include "gcm.h"

#include <pthread.h>
#include <setjmp.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>

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
    uint16_t slot_words; /* OLD_SIZED, OLD_CONS 의 칸 크기 */
    uint32_t nblocks;    /* LARGE: 블록 수 */
    char *top;           /* NURSERY*, OLD_PROMOTED*, OLD_SIZED: 쓴 곳의 끝 */
    uint64_t *marks;     /* old 블록: 단어마다 mark bit (객체 시작 단어) */
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
    if (fresh_block >= REGION_BLOCKS) { fprintf(stderr, "gcm: out of memory\n"); exit(2); }
    return fresh_block++;
}

static void give_block(int64_t b) {
    if (meta[b].marks) { free(meta[b].marks); meta[b].marks = NULL; }
    meta[b].state = FREE;
    meta[b].pinned = 0;
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
static int64_t nursery_budget = 512, base_budget = 512; /* 블록 수 (16MB) */
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

static void *large_alloc(int64_t bytes) {
    int64_t n = (bytes + BLOCK_BYTES - 1) >> BLOCK_SHIFT;
    if (fresh_block + n > REGION_BLOCKS) { fprintf(stderr, "gcm: out of memory\n"); exit(2); }
    int64_t b = fresh_block;
    fresh_block += n;
    meta[b].state = LARGE;
    meta[b].nblocks = (uint32_t)n;
    for (int64_t i = 1; i < n; i++) meta[b + i].state = LARGE_CONT;
    old_bytes += n * BLOCK_BYTES;
    return block_start(b);
}

/* ---- root ---- */
static value **roots;
static int64_t root_count, root_cap;
void gcm_add_root(value *slot) {
    if (root_count == root_cap) { root_cap = root_cap ? root_cap * 2 : 16; roots = realloc(roots, (size_t)root_cap * sizeof *roots); }
    roots[root_count++] = slot;
}

/* remembered set: write barrier 가 기록한 old 객체의 칸 */
static value **remembered;
static int64_t remembered_count, remembered_cap = 1 << 16;

static inline int is_young_block(int64_t b) { return meta[b].state == NURSERY || meta[b].state == NURSERY_CONS; }
static inline int is_young(value v) { return GCM_IS_PTR(v) && in_region(v) && is_young_block(block_of((void *)(v & ~7))); }

/* 쓰기 전에 검사: remembered set 이 차서 minor GC 를 하면 v 는 이 프레임에서 살아 있어 pin 되고 old 가 됨 */
void gcm_write(value *slot, value v) {
    if (is_young(v) && !(in_region((value)slot) && is_young_block(block_of(slot)))) {
        if (remembered_count == remembered_cap) gcm_collect_minor();
        if (is_young(v)) remembered[remembered_count++] = slot;
    }
    *slot = v;
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
    if (!GCM_IS_PTR(v) || !in_region(v)) return;
    value *p = (value *)(v & ~7);
    int64_t b = block_of(p);
    if (!is_young_block(b)) return;
    value first = p[0];
    if ((first & 0xF) == FORWARDED) { *slot = (first & ~(value)0xF) | GCM_TAG(v); return; }
    value *q;
    if (GCM_TAG(v) == 2) {
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
    *slot = (value)q | GCM_TAG(v);
    push_gray((value *)((value)q | GCM_TAG(v))); /* 태그로 cons 인지 기억 */
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

static void major_collect(void);

static void __attribute__((noinline)) minor_collect(void) {
    stat_minor++;
    close_block(gen_block, gen_top);
    close_block(cons_block, cons_top);
    /* 1. stack 과 레지스터 (setjmp 로 레지스터를 이 프레임에 내림) */
    jmp_buf regs;
    setjmp(regs);
    value here;
    pin_range((value *)((uintptr_t)&here & ~(uintptr_t)7), (value *)stack_base);
    /* 훑은 stack 이 크면 다음 nursery 를 키움: minor GC 마다 stack 을 다시 훑으므로, 할당량을 stack 크기의 두 배 이상으로 */
    int64_t stack_blocks = ((stack_base - (char *)&here) * 2) >> BLOCK_SHIFT;
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
        nursery_blocks[i] = nursery_blocks[kept]; /* 앞쪽에 모음 (자리를 바꿔서, 나머지 블록을 모두 돌려줌) */
        nursery_blocks[kept++] = b;
    }
    int64_t promoted = kept;
    /* 3. root: 런타임 전역, remembered set, 넘긴 블록 */
    for (int64_t i = 0; i < root_count; i++) forward(roots[i]);
    for (int64_t i = 0; i < remembered_count; i++) forward(remembered[i]);
    remembered_count = 0;
    for (int64_t i = 0; i < promoted; i++) scan_promoted(nursery_blocks[i]);
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
    if (old_bytes > major_threshold) major_collect();
}

void gcm_collect_minor(void) { minor_collect(); }

/* ---- 할당 ---- */
static void *__attribute__((noinline)) gen_slow(int64_t bytes) {
    if (bytes > LARGE_WORDS * 8) return large_alloc(bytes);
    close_block(gen_block, gen_top);
    if (nursery_used >= nursery_budget) minor_collect();
    gen_block = nursery_block(NURSERY);
    gen_top = block_start(gen_block);
    gen_limit = gen_top + BLOCK_BYTES;
    void *p = gen_top;
    gen_top += bytes;
    return p;
}

void *gcm_alloc(int64_t bytes) {
    bytes = (bytes + 15) & ~(int64_t)15;
    if (gen_top + bytes <= gen_limit) { void *p = gen_top; gen_top += bytes; return p; }
    return gen_slow(bytes);
}

void *gcm_alloc_atomic(int64_t bytes) { return gcm_alloc(bytes); }

static value __attribute__((noinline)) cons_slow(value head, value tail) {
    close_block(cons_block, cons_top);
    if (nursery_used >= nursery_budget) {
        /* head 와 tail 은 이 프레임의 인자 (stack 이나 레지스터) 라서 pin 됨 */
        minor_collect();
    }
    cons_block = nursery_block(NURSERY_CONS);
    cons_top = block_start(cons_block);
    cons_limit = cons_top + BLOCK_BYTES;
    value *c = (value *)cons_top;
    cons_top += 16;
    c[0] = head;
    c[1] = tail;
    return (value)c | 2;
}

value gcm_cons(value head, value tail) {
    if (cons_top + 16 <= cons_limit) {
        value *c = (value *)cons_top;
        cons_top += 16;
        c[0] = head;
        c[1] = tail;
        return (value)c | 2;
    }
    return cons_slow(head, tail);
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

static void mark_range(value *lo, value *hi) { for (value *s = lo; s < hi; s++) mark_value(*s, 0); }

static void sweep(void) {
    for (int c = 0; c <= CLASSES; c++) free_lists[c] = NULL;
    old_bytes = 0;
    for (int64_t b = 0; b < fresh_block; b++) {
        block_meta *m = &meta[b];
        uint64_t *bits = m->marks;
        switch (m->state) {
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
        case OLD_PROMOTED:
        case OLD_PROMOTED_CONS: {
            int any = 0;
            if (bits) for (int64_t i = 0; i < BLOCK_WORDS / 64; i++) if (bits[i]) { any = 1; break; }
            if (!any) give_block(b);
            else old_bytes += BLOCK_BYTES;
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
    mark_range((value *)((uintptr_t)&here & ~(uintptr_t)7), (value *)stack_base);
    mark_range((value *)regs, (value *)((char *)regs + sizeof regs));
    for (int64_t i = 0; i < root_count; i++) mark_value(*roots[i], 1);
    while (mark_count > 0) {
        value *o = mark_stack[--mark_count];
        if (((value)o & 7) == 2) {
            value *c = (value *)((value)o & ~7);
            if (GCM_IS_PTR(c[0])) mark_value(c[0], 1);
            if (GCM_IS_PTR(c[1])) mark_value(c[1], 1);
            continue;
        }
        value h = o[0];
        if (atomic_kind(h)) continue;
        int64_t words = header_words(h);
        for (int64_t i = 1; i < words; i++) if (GCM_IS_PTR(o[i])) mark_value(o[i], 1);
    }
    sweep();
    major_threshold = old_bytes * major_factor > ((int64_t)64 << 20) ? old_bytes * major_factor : ((int64_t)64 << 20);
}

void gcm_collect_major(void) { minor_collect(); major_collect(); }

/* ---- 초기화 ---- */
void gcm_init(void) {
    region = mmap(NULL, (size_t)REGION_BYTES, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON | MAP_NORESERVE, -1, 0);
    if (region == MAP_FAILED) { perror("gcm: mmap"); exit(2); }
    /* 블록 경계에 맞춤 (앞쪽 일부를 버림) */
    char *aligned = (char *)(((uintptr_t)region + BLOCK_BYTES - 1) & ~(uintptr_t)(BLOCK_BYTES - 1));
    region = aligned;
    meta = mmap(NULL, (size_t)REGION_BLOCKS * sizeof(block_meta), PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON, -1, 0);
    fresh_block = 0;
    const char *kb = getenv("GCM_NURSERY_KB");
    if (kb) nursery_budget = atoll(kb) * 1024 / BLOCK_BYTES;
    if (nursery_budget < 2) nursery_budget = 2;
    if (nursery_budget > NURSERY_MAX_BLOCKS) nursery_budget = NURSERY_MAX_BLOCKS;
    base_budget = nursery_budget;
    const char *mf = getenv("GCM_MAJOR_FACTOR");
    if (mf) major_factor = atoll(mf);
    nursery_blocks = malloc((size_t)(NURSERY_MAX_BLOCKS + 2) * sizeof(int64_t)); /* nursery 가 stack 에 맞춰 커질 수 있음 */
    remembered = malloc((size_t)remembered_cap * sizeof *remembered);
    for (int c = 0; c <= CLASSES; c++) bump_block[c] = -1;
    stack_base = pthread_get_stackaddr_np(pthread_self());
}

void gcm_print_stats(void) {
    if (!getenv("GCM_STATS")) return;
    fprintf(stderr, "gcm: minor %lld, major %lld, copied %lld MB, pinned blocks %lld, old peak %lld MB, blocks used %lld (%lld MB)\n",
            (long long)stat_minor, (long long)stat_major, (long long)(stat_copied_bytes >> 20), (long long)stat_pinned_blocks,
            (long long)(stat_old_bytes_peak >> 20), (long long)fresh_block, (long long)((fresh_block * BLOCK_BYTES) >> 20));
}
