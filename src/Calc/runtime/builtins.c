/* 내장 함수 20개 (map, fold 같은 함수는 Calc 로 쓴 prelude.calc). 인자 순서, 결과, 오류 메시지는 Eval.callBuiltin 과 같음.
   문자열 위치와 길이는 UTF-16 코드 단위 (.NET string 과 같음). 타입 검사를 통과했으므로 인자의 종류는 검사하지 않음 */
#include "runtime.h"
#include "unicode.h"

#include <gc.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define STR(v) CALC_STRING(v)
#define INT(v) CALC_INT_VAL(v)
#define HEAD(v) CALC_FIELD(v, 1)
#define TAIL(v) CALC_FIELD(v, 2)

/* 리스트를 앞에서부터 만듦 (마지막 칸의 tail 을 고쳐 붙임) */
typedef struct {
    value head;
    value last;
} list_builder;

static void push(list_builder *b, value x) {
    value cell = calc_cons(x, CALC_NIL);
    if (b->last == CALC_NIL) b->head = cell;
    else TAIL(b->last) = cell;
    b->last = cell;
}

static value from_buffer(calc_buffer *b) {
    value s = calc_string_of(b->units, b->length);
    free(b->units);
    return s;
}

/* s 의 from 부터 sub 가 처음 나오는 위치 (ordinal). 없으면 -1 */
static int64_t find(calc_string *s, calc_string *sub, int64_t from) {
    for (int64_t i = from; i + sub->length <= s->length; i++) {
        if (memcmp(&s->units[i], sub->units, (size_t)sub->length * sizeof(uint16_t)) == 0) return i;
    }
    return -1;
}

/* ---- 문자열 ---- */

value calc_builtin_length(value v) {
    if (CALC_IS_KIND(v, CALC_KIND_STRING)) return calc_int((int32_t)STR(v)->length);
    return calc_int((int32_t)calc_list_length(v));
}

value calc_builtin_substr(value s, value start, value count) {
    int64_t n = STR(s)->length;
    int32_t i = INT(start), c = INT(count);
    if (i < 0 || c < 0 || i > n || c > n - i) {
        calc_errorf("substr: index out of range (length %d, start %d, count %d)", (int)n, i, c);
    }
    return calc_string_of(&STR(s)->units[i], c);
}

/* 코드 포인트 cp 의 변환 결과 (표에 없으면 그대로). 표는 원래 코드 포인트 순서로 정렬되어 있음 */
static uint32_t lookup(const uint32_t (*map)[2], int64_t count, uint32_t cp) {
    int64_t lo = 0, hi = count - 1;
    while (lo <= hi) {
        int64_t mid = (lo + hi) / 2;
        if (map[mid][0] == cp) return map[mid][1];
        if (map[mid][0] < cp) lo = mid + 1;
        else hi = mid - 1;
    }
    return cp;
}

/* ToUpperInvariant / ToLowerInvariant: 코드 포인트마다 변환 (길이는 바뀌지 않음). 짝 없는 대리 코드 단위는 그대로 */
static value convert_case(value s, const uint32_t (*map)[2], int64_t count) {
    calc_string *x = STR(s);
    calc_string *r = calc_new_string(x->length);
    for (int64_t i = 0; i < x->length; i++) {
        uint16_t u = x->units[i];
        if (u >= 0xD800 && u <= 0xDBFF && i + 1 < x->length && x->units[i + 1] >= 0xDC00 && x->units[i + 1] <= 0xDFFF) {
            uint32_t cp = lookup(map, count, 0x10000 + ((uint32_t)(u - 0xD800) << 10) + (x->units[i + 1] - 0xDC00));
            r->units[i] = (uint16_t)(0xD800 + ((cp - 0x10000) >> 10));
            r->units[i + 1] = (uint16_t)(0xDC00 + ((cp - 0x10000) & 0x3FF));
            i++;
        } else if (u >= 0xD800 && u <= 0xDFFF) {
            r->units[i] = u;
        } else {
            r->units[i] = (uint16_t)lookup(map, count, u);
        }
    }
    return (value)r;
}

value calc_builtin_upper(value s) { return convert_case(s, calc_upper_map, sizeof calc_upper_map / sizeof calc_upper_map[0]); }

value calc_builtin_lower(value s) { return convert_case(s, calc_lower_map, sizeof calc_lower_map / sizeof calc_lower_map[0]); }

/* String.Trim: 양 끝의 Char.IsWhiteSpace 문자 */
static int is_whitespace(uint16_t u) {
    for (size_t i = 0; i < sizeof calc_whitespace / sizeof calc_whitespace[0]; i++) {
        if (calc_whitespace[i] == u) return 1;
    }
    return 0;
}

value calc_builtin_trim(value s) {
    calc_string *x = STR(s);
    int64_t start = 0, end = x->length;
    while (start < end && is_whitespace(x->units[start])) start++;
    while (end > start && is_whitespace(x->units[end - 1])) end--;
    return calc_string_of(&x->units[start], end - start);
}

value calc_builtin_contains(value s, value sub) { return calc_bool(find(STR(s), STR(sub), 0) >= 0); }

value calc_builtin_startsWith(value s, value prefix) {
    calc_string *x = STR(s), *p = STR(prefix);
    return calc_bool(p->length <= x->length && memcmp(x->units, p->units, (size_t)p->length * sizeof(uint16_t)) == 0);
}

value calc_builtin_endsWith(value s, value suffix) {
    calc_string *x = STR(s), *p = STR(suffix);
    return calc_bool(p->length <= x->length &&
                     memcmp(&x->units[x->length - p->length], p->units, (size_t)p->length * sizeof(uint16_t)) == 0);
}

value calc_builtin_indexOf(value s, value sub) { return calc_int((int32_t)find(STR(s), STR(sub), 0)); }

/* String.Replace(old, new, Ordinal): 왼쪽부터 겹치지 않게 모두 */
value calc_builtin_replace(value s, value old_value, value new_value) {
    calc_string *x = STR(s), *o = STR(old_value), *n = STR(new_value);
    if (o->length == 0) calc_error("replace: empty search string");
    calc_buffer b = {0};
    int64_t from = 0, at;
    while ((at = find(x, o, from)) >= 0) {
        calc_buffer_units(&b, &x->units[from], at - from);
        calc_buffer_units(&b, n->units, n->length);
        from = at + o->length;
    }
    calc_buffer_units(&b, &x->units[from], x->length - from);
    return from_buffer(&b);
}

/* String.Split(sep): 빈 조각도 남김. 빈 구분자는 코드 단위 하나씩 (대리 쌍도 갈라짐, 인터프리터와 같음) */
value calc_builtin_split(value s, value sep) {
    calc_string *x = STR(s), *p = STR(sep);
    list_builder b = {CALC_NIL, CALC_NIL};
    if (p->length == 0) {
        for (int64_t i = 0; i < x->length; i++) push(&b, calc_string_of(&x->units[i], 1));
        return b.head;
    }
    int64_t from = 0, at;
    while ((at = find(x, p, from)) >= 0) {
        push(&b, calc_string_of(&x->units[from], at - from));
        from = at + p->length;
    }
    push(&b, calc_string_of(&x->units[from], x->length - from));
    return b.head;
}

value calc_builtin_join(value sep, value items) {
    calc_buffer b = {0};
    for (value p = items; p != CALC_NIL; p = TAIL(p)) {
        if (p != items) calc_buffer_units(&b, STR(sep)->units, STR(sep)->length);
        calc_buffer_units(&b, STR(HEAD(p))->units, STR(HEAD(p))->length);
    }
    return from_buffer(&b);
}

/* 문자열은 그대로 (따옴표 없음), 나머지는 formatValue */
value calc_builtin_toString(value v) {
    if (CALC_IS_KIND(v, CALC_KIND_STRING)) return v;
    calc_buffer b = {0};
    calc_buffer_value(&b, v);
    return from_buffer(&b);
}

/* Int32.TryParse(s, AllowLeadingSign, InvariantCulture): 부호 하나(+ 또는 -)와 ASCII 숫자만. 공백은 안 되고,
   끝의 U+0000 은 무시 (.NET 과 같음) */
value calc_builtin_toInt(value s) {
    calc_string *x = STR(s);
    int64_t end = x->length;
    while (end > 0 && x->units[end - 1] == 0) end--;
    int64_t i = 0;
    int negative = 0;
    if (i < end && (x->units[i] == '+' || x->units[i] == '-')) {
        negative = x->units[i] == '-';
        i++;
    }
    int64_t n = 0;
    int ok = i < end;
    for (; ok && i < end; i++) {
        uint16_t u = x->units[i];
        if (u < '0' || u > '9') ok = 0;
        else {
            n = n * 10 + (u - '0');
            if (n > 2147483648LL) ok = 0;
        }
    }
    if (ok && (negative || n <= 2147483647LL)) return calc_int((int32_t)(negative ? -n : n));
    calc_buffer b = {0};
    calc_buffer_quoted(&b, x->units, x->length);
    calc_errorf("toInt: invalid integer: %s", calc_buffer_utf8(&b));
    return 0;
}

/* ---- 리스트 ---- */

value calc_builtin_head(value xs) {
    if (xs == CALC_NIL) calc_error("head: empty list");
    return HEAD(xs);
}

value calc_builtin_tail(value xs) {
    if (xs == CALC_NIL) calc_error("tail: empty list");
    return TAIL(xs);
}

value calc_builtin_nth(value xs, value i) {
    int64_t n = calc_list_length(xs);
    int32_t k = INT(i);
    if (k < 0 || k >= n) calc_errorf("nth: index out of range (length %d, index %d)", (int)n, k);
    for (; k > 0; k--) xs = TAIL(xs);
    return HEAD(xs);
}

static int compare_for_sort(const void *a, const void *b) { return calc_compare(*(const value *)a, *(const value *)b); }

/* int 끼리 또는 string 끼리 (calc_compare). 같다고 비교되는 원소는 내용도 같으므로 안정 정렬이 아니어도 결과가 같음 */
value calc_builtin_sort(value xs) {
    int64_t n = calc_list_length(xs);
    value *items = GC_MALLOC((size_t)(n > 0 ? n : 1) * sizeof(value));
    for (int64_t i = 0; i < n; i++, xs = TAIL(xs)) items[i] = HEAD(xs);
    qsort(items, (size_t)n, sizeof(value), compare_for_sort);
    value r = CALC_NIL;
    for (int64_t i = n - 1; i >= 0; i--) r = calc_cons(items[i], r);
    return r;
}

/* ---- 출력 ---- */

/* print, println: 문자열은 따옴표 없이, 나머지는 formatValue (toString 과 같음). UTF-8 로 표준 출력 */
static void put_display(value v) {
    calc_buffer b = {0};
    if (CALC_IS_KIND(v, CALC_KIND_STRING)) calc_buffer_units(&b, STR(v)->units, STR(v)->length);
    else calc_buffer_value(&b, v);
    char *text = calc_buffer_utf8(&b);
    fputs(text, stdout);
    free(text);
}

value calc_builtin_print(value v) {
    put_display(v);
    return CALC_UNIT;
}

value calc_builtin_println(value v) {
    put_display(v);
    putchar('\n');
    return CALC_UNIT;
}
