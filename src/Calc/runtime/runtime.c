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

/* Eval.formatValue 와 같은 형식 */
static void format_value(FILE *out, value v) {
    if (CALC_IS_INT(v)) {
        fprintf(out, "%d", CALC_INT_VAL(v));
    } else if (CALC_IS_BOOL(v)) {
        fputs(CALC_BOOL_VAL(v) ? "true" : "false", out);
    } else if (v == CALC_NIL) {
        fputs("[]", out);
    } else if (CALC_IS_KIND(v, CALC_KIND_CLOSURE)) {
        fputs("<fun>", out);
    } else if (CALC_IS_KIND(v, CALC_KIND_STRING)) {
        put_quoted(out, CALC_STRING(v));
    } else if (CALC_IS_KIND(v, CALC_KIND_CONS)) {
        fputc('[', out);
        for (value p = v; p != CALC_NIL; p = CALC_FIELD(p, 2)) {
            if (p != v) fputs("; ", out);
            format_value(out, CALC_FIELD(p, 1));
        }
        fputc(']', out);
    } else if (CALC_IS_KIND(v, CALC_KIND_DATA)) {
        fputs((const char *)CALC_FIELD(v, 1), out);
        if (CALC_SIZE(v) == 2) {
            /* 다시 입력했을 때 같은 값이 되도록 괄호: Some (Some 1), Some (-1). 튜플은 이미 괄호가 있음 */
            value p = CALC_FIELD(v, 2);
            int paren = (CALC_IS_KIND(p, CALC_KIND_DATA) && CALC_SIZE(p) == 2) || (CALC_IS_INT(p) && CALC_INT_VAL(p) < 0);
            fputs(paren ? " (" : " ", out);
            format_value(out, p);
            if (paren) fputc(')', out);
        }
    } else if (CALC_IS_KIND(v, CALC_KIND_TUPLE)) {
        fputc('(', out);
        for (int64_t i = 0; i < CALC_SIZE(v); i++) {
            if (i > 0) fputs(", ", out);
            format_value(out, CALC_FIELD(v, 1 + i));
        }
        fputc(')', out);
    } else {
        calc_error("format_value: unsupported value");
    }
}

void calc_print_result(value v) {
    format_value(stdout, v);
    putchar('\n');
}

void calc_match_failure(value v) {
    fflush(stdout);
    fputs("Error: Match failure: ", stderr);
    format_value(stderr, v);
    fputc('\n', stderr);
    exit(1);
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

static int64_t list_length(value v) {
    int64_t n = 0;
    for (; v != CALC_NIL; v = CALC_FIELD(v, 2)) n++;
    return n;
}

/* Eval.valueEquals 와 같음: 리스트와 튜플은 길이를 먼저 보고, 원소를 앞에서부터 (처음 다른 곳에서 멈춤) */
static int equals(value a, value b) {
    /* 함수끼리는 비교할 수 없음 (타입 검사는 통과하므로 실행 중 오류, 인터프리터와 같은 문장) */
    if (CALC_IS_KIND(a, CALC_KIND_CLOSURE)) {
        calc_error("Type error: expected int, bool, string, list, tuple or data but got function");
    }
    if (CALC_IS_INT(a) || CALC_IS_BOOL(a)) {
        return a == b;
    }
    if (CALC_IS_KIND(a, CALC_KIND_STRING)) {
        calc_string *x = CALC_STRING(a), *y = CALC_STRING(b);
        return x->length == y->length && memcmp(x->units, y->units, (size_t)x->length * sizeof(uint16_t)) == 0;
    }
    if (a == CALC_NIL || CALC_IS_KIND(a, CALC_KIND_CONS)) {
        if (list_length(a) != list_length(b)) return 0;
        for (; a != CALC_NIL; a = CALC_FIELD(a, 2), b = CALC_FIELD(b, 2)) {
            if (!equals(CALC_FIELD(a, 1), CALC_FIELD(b, 1))) return 0;
        }
        return 1;
    }
    /* 타입 검사를 통과했으므로 같은 타입: 생성자가 다르면 false, 같으면 인자를 비교 */
    if (CALC_IS_KIND(a, CALC_KIND_DATA)) {
        if (strcmp((const char *)CALC_FIELD(a, 1), (const char *)CALC_FIELD(b, 1)) != 0) return 0;
        return CALC_SIZE(a) == 2 && CALC_SIZE(b) == 2 ? equals(CALC_FIELD(a, 2), CALC_FIELD(b, 2)) : 1;
    }
    if (CALC_IS_KIND(a, CALC_KIND_TUPLE)) {
        for (int64_t i = 0; i < CALC_SIZE(a); i++) {
            if (!equals(CALC_FIELD(a, 1 + i), CALC_FIELD(b, 1 + i))) return 0;
        }
        return 1;
    }
    calc_error("calc_eq: unsupported value");
    return 0;
}

value calc_eq(value a, value b) { return equals(a, b) ? CALC_TRUE : CALC_FALSE; }

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
