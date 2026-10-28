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

void calc_init(void);
void *calc_alloc(int64_t bytes);
/* "Error: <message>" 를 표준 에러로 출력하고 종료 코드 1 로 끝냄 (인터프리터와 같은 형식) */
void calc_error(const char *message);
/* Eval.formatValue 와 같은 형식으로 결과를 출력 */
void calc_print_result(value v);

#endif
