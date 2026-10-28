#include "runtime.h"

#include <gc.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void calc_init(void) { GC_INIT(); }

void *calc_alloc(int64_t bytes) { return GC_MALLOC((size_t)bytes); }

void calc_error(const char *message) {
    fflush(stdout);
    fprintf(stderr, "Error: %s\n", message);
    exit(1);
}

/* 코드 포인트 하나를 UTF-8 로 */
static void put_utf8(FILE *out, uint32_t cp) {
    if (cp < 0x80) {
        fputc((int)cp, out);
    } else if (cp < 0x800) {
        fputc(0xC0 | (cp >> 6), out);
        fputc(0x80 | (cp & 0x3F), out);
    } else if (cp < 0x10000) {
        fputc(0xE0 | (cp >> 12), out);
        fputc(0x80 | ((cp >> 6) & 0x3F), out);
        fputc(0x80 | (cp & 0x3F), out);
    } else {
        fputc(0xF0 | (cp >> 18), out);
        fputc(0x80 | ((cp >> 12) & 0x3F), out);
        fputc(0x80 | ((cp >> 6) & 0x3F), out);
        fputc(0x80 | (cp & 0x3F), out);
    }
}

/* UTF-16 → UTF-8. 짝이 없는 대리 코드 단위는 U+FFFD (.NET 의 UTF-8 출력과 같음) */
static void put_utf16(FILE *out, const uint16_t *units, int64_t length) {
    for (int64_t i = 0; i < length; i++) {
        uint32_t u = units[i];
        if (u >= 0xD800 && u <= 0xDBFF && i + 1 < length && units[i + 1] >= 0xDC00 && units[i + 1] <= 0xDFFF) {
            put_utf8(out, 0x10000 + ((u - 0xD800) << 10) + (units[i + 1] - 0xDC00));
            i++;
        } else if (u >= 0xD800 && u <= 0xDFFF) {
            put_utf8(out, 0xFFFD);
        } else {
            put_utf8(out, u);
        }
    }
}

static const char *escape_of(uint16_t u) {
    switch (u) {
    case '\\': return "\\\\";
    case '"': return "\\\"";
    case '\n': return "\\n";
    case '\t': return "\\t";
    case '\r': return "\\r";
    default: return NULL;
    }
}

/* Eval.formatValue 의 quote 와 같음: \\ \" \n \t \r 만 이스케이프. 나머지는 구간째 UTF-8 로 (대리 쌍이 갈라지지 않게) */
static void put_quoted(FILE *out, calc_string *s) {
    fputc('"', out);
    int64_t start = 0;
    for (int64_t i = 0; i < s->length; i++) {
        const char *escape = escape_of(s->units[i]);
        if (escape != NULL) {
            put_utf16(out, &s->units[start], i - start);
            fputs(escape, out);
            start = i + 1;
        }
    }
    put_utf16(out, &s->units[start], s->length - start);
    fputc('"', out);
}

void calc_print_result(value v) {
    if (CALC_IS_INT(v)) {
        printf("%d\n", CALC_INT_VAL(v));
    } else if (CALC_IS_BOOL(v)) {
        printf("%s\n", CALC_BOOL_VAL(v) ? "true" : "false");
    } else if (CALC_IS_KIND(v, CALC_KIND_CLOSURE)) {
        printf("<fun>\n");
    } else if (CALC_IS_KIND(v, CALC_KIND_STRING)) {
        put_quoted(stdout, CALC_STRING(v));
        putchar('\n');
    } else {
        calc_error("calc_print_result: unsupported value");
    }
}

void calc_error_divide_by_zero(void) { calc_error("Attempted to divide by zero."); }

void calc_error_overflow(void) { calc_error("Arithmetic operation resulted in an overflow."); }

/* 32비트 덧셈이 한 바퀴 도는 것은 부호 없는 수로 더해서 표현 (부호 있는 오버플로는 정의되지 않은 동작) */
static value tag_int(int32_t n) { return (value)(((uint64_t)(int64_t)n << 3) | CALC_TAG_INT); }

/* 새 문자열 블록. 안에 포인터가 없으므로 GC 가 내용을 훑지 않는 ATOMIC 할당 */
static calc_string *alloc_string(int64_t length) {
    calc_string *s = GC_MALLOC_ATOMIC(sizeof(calc_string) + (size_t)length * sizeof(uint16_t));
    s->header = CALC_KIND_STRING | ((1 + (2 * length + 7) / 8) << 8);
    s->length = length;
    return s;
}

value calc_add(value a, value b) {
    if (CALC_IS_INT(a) && CALC_IS_INT(b)) {
        return tag_int((int32_t)((uint32_t)CALC_INT_VAL(a) + (uint32_t)CALC_INT_VAL(b)));
    }
    if (CALC_IS_KIND(a, CALC_KIND_STRING) && CALC_IS_KIND(b, CALC_KIND_STRING)) {
        calc_string *x = CALC_STRING(a), *y = CALC_STRING(b);
        calc_string *r = alloc_string(x->length + y->length);
        memcpy(r->units, x->units, (size_t)x->length * sizeof(uint16_t));
        memcpy(r->units + x->length, y->units, (size_t)y->length * sizeof(uint16_t));
        return (value)r;
    }
    calc_error("calc_add: unsupported value");
    return 0;
}

value calc_eq(value a, value b) {
    /* 함수끼리는 비교할 수 없음 (타입 검사는 통과하므로 실행 중 오류, 인터프리터와 같은 문장) */
    if (CALC_IS_PTR(a) && CALC_KIND(a) == CALC_KIND_CLOSURE) {
        calc_error("Type error: expected int, bool, string, list, tuple or data but got function");
    }
    /* int 와 bool 은 즉시값이라 단어가 같으면 같은 값 */
    if ((CALC_IS_INT(a) || CALC_IS_BOOL(a)) && (CALC_IS_INT(b) || CALC_IS_BOOL(b))) {
        return a == b ? CALC_TRUE : CALC_FALSE;
    }
    if (CALC_IS_KIND(a, CALC_KIND_STRING) && CALC_IS_KIND(b, CALC_KIND_STRING)) {
        calc_string *x = CALC_STRING(a), *y = CALC_STRING(b);
        int same = x->length == y->length && memcmp(x->units, y->units, (size_t)x->length * sizeof(uint16_t)) == 0;
        return same ? CALC_TRUE : CALC_FALSE;
    }
    calc_error("calc_eq: unsupported value");
    return 0;
}

int32_t calc_compare(value a, value b) {
    if (CALC_IS_INT(a) && CALC_IS_INT(b)) {
        int32_t x = CALC_INT_VAL(a), y = CALC_INT_VAL(b);
        return x < y ? -1 : (x > y ? 1 : 0);
    }
    /* String.CompareOrdinal 과 같음: UTF-16 코드 단위를 부호 없는 수로 차례로 비교, 같으면 짧은 쪽이 앞 */
    if (CALC_IS_KIND(a, CALC_KIND_STRING) && CALC_IS_KIND(b, CALC_KIND_STRING)) {
        calc_string *x = CALC_STRING(a), *y = CALC_STRING(b);
        int64_t n = x->length < y->length ? x->length : y->length;
        for (int64_t i = 0; i < n; i++) {
            if (x->units[i] != y->units[i]) {
                return x->units[i] < y->units[i] ? -1 : 1;
            }
        }
        return x->length < y->length ? -1 : (x->length > y->length ? 1 : 0);
    }
    calc_error("calc_compare: unsupported value");
    return 0;
}
