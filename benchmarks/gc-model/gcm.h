/* 세대 구분 GC 모델 (docs/generational-gc-design.md). 값은 Calc 와 같은 tagged 64비트:
   태그 0 = [헤더, ...] 블록의 주소, 태그 2 = 헤더 없는 16바이트 cons 의 주소 + 2, 나머지 태그는 즉시값 */
#ifndef GCM_H
#define GCM_H
#include <stdint.h>

typedef int64_t value;

#define GCM_TAG(v) ((v) & 7)
#define GCM_IS_PTR(v) (GCM_TAG(v) == 0 || GCM_TAG(v) == 2)

void gcm_init(void);
/* 헤더가 있는 블록 (bytes 는 헤더 포함). 내용은 0 으로 채워져 있음. 부른 쪽이 바로 헤더를 씀 */
void *gcm_alloc(int64_t bytes);
/* 안에 포인터가 없는 블록 (문자열, bool 배열). 모델에서는 gcm_alloc 과 같은 공간 */
void *gcm_alloc_atomic(int64_t bytes);
/* 헤더 없는 cons 칸 [head, tail]. 결과는 주소 + 2 */
value gcm_cons(value head, value tail);
/* 오래된 객체의 칸에 값을 씀 (write barrier) */
void gcm_write(value *slot, value v);
/* 런타임 전역의 값 칸을 root 로 등록 */
void gcm_add_root(value *slot);
void gcm_collect_minor(void);
void gcm_collect_major(void);
void gcm_print_stats(void);
#endif
