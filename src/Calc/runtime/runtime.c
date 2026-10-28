#include "runtime.h"

#include <gc.h>
#include <stdio.h>
#include <stdlib.h>

void calc_init(void) { GC_INIT(); }

void *calc_alloc(int64_t bytes) { return GC_MALLOC((size_t)bytes); }

void calc_error(const char *message) {
    fflush(stdout);
    fprintf(stderr, "Error: %s\n", message);
    exit(1);
}

void calc_print_result(value v) {
    if (CALC_IS_INT(v)) {
        printf("%d\n", CALC_INT_VAL(v));
    } else if (CALC_IS_BOOL(v)) {
        printf("%s\n", CALC_BOOL_VAL(v) ? "true" : "false");
    } else {
        calc_error("calc_print_result: unsupported value");
    }
}

void calc_error_divide_by_zero(void) { calc_error("Attempted to divide by zero."); }

void calc_error_overflow(void) { calc_error("Arithmetic operation resulted in an overflow."); }

/* 32비트 덧셈이 한 바퀴 도는 것은 부호 없는 수로 더해서 표현 (부호 있는 오버플로는 정의되지 않은 동작) */
static value tag_int(int32_t n) { return (value)(((uint64_t)(int64_t)n << 3) | CALC_TAG_INT); }

value calc_add(value a, value b) {
    if (CALC_IS_INT(a) && CALC_IS_INT(b)) {
        return tag_int((int32_t)((uint32_t)CALC_INT_VAL(a) + (uint32_t)CALC_INT_VAL(b)));
    }
    calc_error("calc_add: unsupported value");
    return 0;
}

value calc_eq(value a, value b) {
    /* int 와 bool 은 즉시값이라 단어가 같으면 같은 값 */
    if ((CALC_IS_INT(a) || CALC_IS_BOOL(a)) && (CALC_IS_INT(b) || CALC_IS_BOOL(b))) {
        return a == b ? CALC_TRUE : CALC_FALSE;
    }
    calc_error("calc_eq: unsupported value");
    return 0;
}

int32_t calc_compare(value a, value b) {
    if (CALC_IS_INT(a) && CALC_IS_INT(b)) {
        int32_t x = CALC_INT_VAL(a), y = CALC_INT_VAL(b);
        return x < y ? -1 : (x > y ? 1 : 0);
    }
    calc_error("calc_compare: unsupported value");
    return 0;
}
