#define _GNU_SOURCE /* vasprintf (Linux). 첫 #include 보다 앞이어야 함 */
#include "runtime.h"

#include <gc.h>
#include <stdarg.h>
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

/* 메시지 길이에 제한이 없게 vasprintf (Match failure 는 큰 값을 그대로 출력함) */
void calc_errorf(const char *format, ...) {
    char *message = NULL;
    va_list args;
    va_start(args, format);
    if (vasprintf(&message, format, args) < 0) message = "calc_errorf: out of memory";
    va_end(args);
    calc_error(message);
}

/* ---- 값 만들기 ---- */

/* 32비트 덧셈이 한 바퀴 도는 것은 부호 없는 수로 더해서 표현 (부호 있는 오버플로는 정의되지 않은 동작) */
value calc_int(int32_t n) { return (value)(((uint64_t)(int64_t)n << 3) | CALC_TAG_INT); }

value calc_bool(int b) { return b ? CALC_TRUE : CALC_FALSE; }

/* 새 문자열 블록. 안에 포인터가 없으므로 GC 가 내용을 훑지 않는 ATOMIC 할당 */
calc_string *calc_new_string(int64_t length) {
    calc_string *s = GC_MALLOC_ATOMIC(sizeof(calc_string) + (size_t)length * sizeof(uint16_t));
    s->header = CALC_KIND_STRING | ((1 + (2 * length + 7) / 8) << 8);
    s->length = length;
    return s;
}

value calc_string_of(const uint16_t *units, int64_t length) {
    calc_string *s = calc_new_string(length);
    memcpy(s->units, units, (size_t)length * sizeof(uint16_t));
    return (value)s;
}

value calc_cons(value head, value tail) {
    value *block = GC_MALLOC(3 * sizeof(value));
    block[0] = CALC_KIND_CONS | (2 << 8);
    block[1] = head;
    block[2] = tail;
    return (value)block;
}

const char calc_name_some[] = "Some";
const char calc_name_none[] = "None";
const calc_unit calc_none __attribute__((aligned(8))) = {CALC_KIND_DATA | (1 << 8), calc_name_none};

value calc_some(value v) {
    value *block = GC_MALLOC(3 * sizeof(value));
    block[0] = CALC_KIND_DATA | (2 << 8);
    block[1] = (value)calc_name_some;
    block[2] = v;
    return (value)block;
}

int64_t calc_list_length(value v) {
    int64_t n = 0;
    for (; v != CALC_NIL; v = CALC_FIELD(v, 2)) n++;
    return n;
}

value calc_apply(value f, value arg) {
    value (*code)(value *, value) = (value(*)(value *, value))CALC_FIELD(f, 1);
    return code((value *)f, arg);
}

/* ---- UTF-16 버퍼와 formatValue ---- */

static void buffer_reserve(calc_buffer *b, int64_t extra) {
    if (b->length + extra <= b->capacity) return;
    int64_t capacity = b->capacity < 64 ? 64 : b->capacity;
    while (capacity < b->length + extra) capacity *= 2;
    b->units = realloc(b->units, (size_t)capacity * sizeof(uint16_t));
    b->capacity = capacity;
}

void calc_buffer_units(calc_buffer *b, const uint16_t *units, int64_t length) {
    buffer_reserve(b, length);
    memcpy(b->units + b->length, units, (size_t)length * sizeof(uint16_t));
    b->length += length;
}

/* ASCII 문자열 (숫자, 생성자 이름, 구분 기호) */
static void buffer_ascii(calc_buffer *b, const char *text) {
    int64_t length = (int64_t)strlen(text);
    buffer_reserve(b, length);
    for (int64_t i = 0; i < length; i++) b->units[b->length + i] = (uint16_t)(unsigned char)text[i];
    b->length += length;
}

/* Eval.quote 와 같음: \\ \" \n \t \r 만 이스케이프 */
void calc_buffer_quoted(calc_buffer *b, const uint16_t *units, int64_t length) {
    buffer_ascii(b, "\"");
    for (int64_t i = 0; i < length; i++) {
        switch (units[i]) {
        case '\\': buffer_ascii(b, "\\\\"); break;
        case '"': buffer_ascii(b, "\\\""); break;
        case '\n': buffer_ascii(b, "\\n"); break;
        case '\t': buffer_ascii(b, "\\t"); break;
        case '\r': buffer_ascii(b, "\\r"); break;
        default: calc_buffer_units(b, &units[i], 1); break;
        }
    }
    buffer_ascii(b, "\"");
}

/* Eval.formatValue 와 같은 형식 */
void calc_buffer_value(calc_buffer *b, value v) {
    if (CALC_IS_INT(v)) {
        char text[16];
        snprintf(text, sizeof text, "%d", CALC_INT_VAL(v));
        buffer_ascii(b, text);
    } else if (CALC_IS_BOOL(v)) {
        buffer_ascii(b, CALC_BOOL_VAL(v) ? "true" : "false");
    } else if (v == CALC_NIL) {
        buffer_ascii(b, "[]");
    } else if (CALC_IS_KIND(v, CALC_KIND_CLOSURE)) {
        buffer_ascii(b, "<fun>");
    } else if (CALC_IS_KIND(v, CALC_KIND_STRING)) {
        calc_buffer_quoted(b, CALC_STRING(v)->units, CALC_STRING(v)->length);
    } else if (CALC_IS_KIND(v, CALC_KIND_CONS)) {
        buffer_ascii(b, "[");
        for (value p = v; p != CALC_NIL; p = CALC_FIELD(p, 2)) {
            if (p != v) buffer_ascii(b, "; ");
            calc_buffer_value(b, CALC_FIELD(p, 1));
        }
        buffer_ascii(b, "]");
    } else if (CALC_IS_KIND(v, CALC_KIND_DATA)) {
        buffer_ascii(b, (const char *)CALC_FIELD(v, 1));
        if (CALC_SIZE(v) == 2) {
            /* 다시 입력했을 때 같은 값이 되도록 괄호: Some (Some 1), Some (-1). 튜플은 이미 괄호가 있음 */
            value p = CALC_FIELD(v, 2);
            int paren = (CALC_IS_KIND(p, CALC_KIND_DATA) && CALC_SIZE(p) == 2) || (CALC_IS_INT(p) && CALC_INT_VAL(p) < 0);
            buffer_ascii(b, paren ? " (" : " ");
            calc_buffer_value(b, p);
            if (paren) buffer_ascii(b, ")");
        }
    } else if (CALC_IS_KIND(v, CALC_KIND_TUPLE)) {
        buffer_ascii(b, "(");
        for (int64_t i = 0; i < CALC_SIZE(v); i++) {
            if (i > 0) buffer_ascii(b, ", ");
            calc_buffer_value(b, CALC_FIELD(v, 1 + i));
        }
        buffer_ascii(b, ")");
    } else {
        calc_error("calc_buffer_value: unsupported value");
    }
}

/* 코드 포인트 하나를 UTF-8 로 */
static char *put_utf8(char *out, uint32_t cp) {
    if (cp < 0x80) {
        *out++ = (char)cp;
    } else if (cp < 0x800) {
        *out++ = (char)(0xC0 | (cp >> 6));
        *out++ = (char)(0x80 | (cp & 0x3F));
    } else if (cp < 0x10000) {
        *out++ = (char)(0xE0 | (cp >> 12));
        *out++ = (char)(0x80 | ((cp >> 6) & 0x3F));
        *out++ = (char)(0x80 | (cp & 0x3F));
    } else {
        *out++ = (char)(0xF0 | (cp >> 18));
        *out++ = (char)(0x80 | ((cp >> 12) & 0x3F));
        *out++ = (char)(0x80 | ((cp >> 6) & 0x3F));
        *out++ = (char)(0x80 | (cp & 0x3F));
    }
    return out;
}

/* UTF-16 → UTF-8. 짝이 없는 대리 코드 단위는 U+FFFD (.NET 의 UTF-8 출력과 같음) */
char *calc_buffer_utf8(calc_buffer *b) {
    char *text = malloc((size_t)b->length * 3 + 1); /* 코드 단위 하나가 UTF-8 로 3바이트를 넘지 않음 (대리 쌍은 둘이 4바이트) */
    char *out = text;
    for (int64_t i = 0; i < b->length; i++) {
        uint32_t u = b->units[i];
        if (u >= 0xD800 && u <= 0xDBFF && i + 1 < b->length && b->units[i + 1] >= 0xDC00 && b->units[i + 1] <= 0xDFFF) {
            out = put_utf8(out, 0x10000 + ((u - 0xD800) << 10) + (b->units[i + 1] - 0xDC00));
            i++;
        } else if (u >= 0xD800 && u <= 0xDFFF) {
            out = put_utf8(out, 0xFFFD);
        } else {
            out = put_utf8(out, u);
        }
    }
    *out = '\0';
    free(b->units);
    b->units = NULL;
    b->length = b->capacity = 0;
    return text;
}

static char *format_utf8(value v) {
    calc_buffer b = {0};
    calc_buffer_value(&b, v);
    return calc_buffer_utf8(&b);
}

void calc_print_result(value v) {
    char *text = format_utf8(v);
    fputs(text, stdout);
    putchar('\n');
    free(text);
}

void calc_match_failure(value v) { calc_errorf("Match failure: %s", format_utf8(v)); }

void calc_error_divide_by_zero(void) { calc_error("Attempted to divide by zero."); }

void calc_error_overflow(void) { calc_error("Arithmetic operation resulted in an overflow."); }

/* ---- 다형 연산 ---- */

value calc_add(value a, value b) {
    if (CALC_IS_INT(a) && CALC_IS_INT(b)) {
        return calc_int((int32_t)((uint32_t)CALC_INT_VAL(a) + (uint32_t)CALC_INT_VAL(b)));
    }
    if (CALC_IS_KIND(a, CALC_KIND_STRING) && CALC_IS_KIND(b, CALC_KIND_STRING)) {
        calc_string *x = CALC_STRING(a), *y = CALC_STRING(b);
        calc_string *r = calc_new_string(x->length + y->length);
        memcpy(r->units, x->units, (size_t)x->length * sizeof(uint16_t));
        memcpy(r->units + x->length, y->units, (size_t)y->length * sizeof(uint16_t));
        return (value)r;
    }
    calc_error("calc_add: unsupported value");
    return 0;
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
        if (calc_list_length(a) != calc_list_length(b)) return 0;
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

value calc_eq(value a, value b) { return calc_bool(equals(a, b)); }

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
