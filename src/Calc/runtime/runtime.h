/* Calc 컴파일러가 만든 코드와 C 런타임 사이의 약속. docs/compiler-design.md 의 D3, 6장 참고 */
#ifndef CALC_RUNTIME_H
#define CALC_RUNTIME_H

#include <stdint.h>

/* 모든 값은 64비트 단어 하나. 하위 3비트가 종류 */
typedef int64_t value;

#define CALC_TAG_MASK 7
#define CALC_TAG_INT 1  /* (n << 3) | 1, n 은 32비트 */
#define CALC_TAG_BOOL 3 /* (b << 3) | 3 */
#define CALC_NIL 5      /* [] */

#define CALC_IS_INT(v) (((v) & CALC_TAG_MASK) == CALC_TAG_INT)
#define CALC_IS_BOOL(v) (((v) & CALC_TAG_MASK) == CALC_TAG_BOOL)
#define CALC_INT_VAL(v) ((int32_t)((v) >> 3))
#define CALC_BOOL_VAL(v) ((int)((v) >> 3))
#define CALC_IS_PTR(v) (((v) & CALC_TAG_MASK) == 0)

/* 힙 블록: 첫 단어가 헤더 = 종류 | (뒤따르는 단어 수 << 8). Codegen.closureHeader 와 같음 */
#define CALC_KIND_CLOSURE 1 /* [헤더, 코드 포인터, 캡처한 값...] */
#define CALC_HEADER(v) (((int64_t *)(v))[0])
#define CALC_KIND(v) (CALC_HEADER(v) & 0xff)

#define CALC_TRUE ((value)11)
#define CALC_FALSE ((value)3)

void calc_init(void);
void *calc_alloc(int64_t bytes);
/* "Error: <message>" 를 표준 에러로 출력하고 종료 코드 1 로 끝냄 (인터프리터와 같은 형식) */
void calc_error(const char *message);
/* Eval.formatValue 와 같은 형식으로 결과를 출력 */
void calc_print_result(value v);

/* 인터프리터(.NET 예외 메시지)와 같은 문장으로 끝냄 */
void calc_error_divide_by_zero(void);
void calc_error_overflow(void);

/* 타입이 여럿인 연산: 태그를 보고 동작을 고름 (docs/compiler-design.md D6) */
value calc_add(value a, value b);       /* int 덧셈 (32비트에서 한 바퀴 돎) */
value calc_eq(value a, value b);        /* 구조적 비교, 결과는 bool 값 */
int32_t calc_compare(value a, value b); /* -1, 0, 1 */

#endif
