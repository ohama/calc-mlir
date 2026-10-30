/* Calc 와 같은 값 표현의 C 모델. -DUSE_GCM 이면 새 collector, 아니면 지금 런타임과 같은 Boehm 설정 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
typedef int64_t value;
#define INT(n) ((((value)(n)) << 3) | 1)
#define VAL(v) ((v) >> 3)
#define NIL ((value)5)
#define HD(c) (((value *)((c) - 2))[0])
#define TL(c) (((value *)((c) - 2))[1])
#define FIELD(b, i) (((value *)(b))[i])
#ifdef USE_GCM
#include "gcm.h"
static void init(void) { gcm_init(); }
static inline void *alloc(int64_t bytes) { return gcm_alloc(bytes); }
static inline value cons(value h, value t) { return gcm_cons(h, t); }
static void done(void) { gcm_print_stats(); }
#else
#include <gc.h>
static void *fl[9];
static void init(void) { GC_set_all_interior_pointers(0); GC_INIT(); GC_register_displacement(2); GC_set_free_space_divisor(1); }
static inline void *alloc(int64_t bytes) { size_t w = ((size_t)bytes + 7) >> 3; if (w > 8) return GC_MALLOC((size_t)bytes); void *p = fl[w]; if (!p) p = GC_malloc_many(w << 3); fl[w] = GC_NEXT(p); GC_NEXT(p) = 0; return p; }
static inline value cons(value h, value t) { value *c = alloc(16); c[0] = h; c[1] = t; return (value)c | 2; }
static void done(void) {}
#endif
/* data 블록 [헤더 5 | n << 8 | 번호 << 32, 인자...] 와 튜플 [헤더 4 | n << 8, 원소...] */
static inline value data2(int64_t ctor, value a, value b) { value *p = alloc(24); p[0] = 5 | (2 << 8) | (ctor << 32); p[1] = a; p[2] = b; return (value)p; }
static inline value data1(int64_t ctor, value a) { value *p = alloc(16); p[0] = 5 | (1 << 8) | (ctor << 32); p[1] = a; return (value)p; }
static inline value data3(int64_t ctor, value a, value b, value c) { value *p = alloc(32); p[0] = 5 | (3 << 8) | (ctor << 32); p[1] = a; p[2] = b; p[3] = c; return (value)p; }
static inline value tuple2(value a, value b) { value *p = alloc(24); p[0] = 4 | (2 << 8); p[1] = a; p[2] = b; return (value)p; }
