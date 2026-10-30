/* 내장 함수 (map, fold, head 같은 함수는 Calc 로 쓴 prelude.calc). 인자 순서, 결과, 오류 메시지는 Eval.callBuiltin 과 같음.
   문자열 위치와 길이는 UTF-16 코드 단위 (.NET string 과 같음). 타입 검사를 통과했으므로 인자의 종류는 검사하지 않음 */
#include "runtime.h"
#include "unicode.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define STR(v) CALC_STRING(v)
#define INT(v) CALC_INT_VAL(v)
#define HEAD(v) CALC_HEAD(v)
#define TAIL(v) CALC_TAIL(v)

/* 리스트를 앞에서부터 만듦 (마지막 칸의 tail 을 고쳐 붙임) */
typedef struct {
    value head;
    value last;
} list_builder;

static void push(list_builder *b, value x) {
    value cell = calc_cons(x, CALC_NIL);
    if (b->last == CALC_NIL) b->head = cell;
    else {
        TAIL(b->last) = cell;
        calc_write_barrier(&TAIL(b->last), cell); /* 앞 칸이 그 사이의 수거로 old 가 되었을 수 있음 */
    }
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
    if (CALC_IS_INT(v)) { /* 가장 흔한 경우: 버퍼 없이 바로 문자열로 */
        char text[16];
        char *p = calc_format_int(INT(v), text + sizeof text);
        int64_t length = text + sizeof text - p;
        calc_string *s = calc_new_string(length);
        for (int64_t i = 0; i < length; i++) s->units[i] = (uint16_t)p[i];
        return (value)s;
    }
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

static int compare_for_sort(const void *a, const void *b) { return calc_compare(*(const value *)a, *(const value *)b); }

/* 정렬용 임시 단어 n 개. 블록에 헤더를 두어야 세대 구분 GC 가 블록을 차례로 읽을 수 있으므로 헤더 뒤의 주소를 돌려줌.
   atomic 이면 문자열과 같은 종류(안을 보지 않음), 아니면 배열 */
static value *scratch(int64_t n, int atomic) {
    if (n < 1) n = 1;
    value *block = atomic ? calc_alloc_atomic(8 * (1 + n)) : calc_alloc(8 * (1 + n));
    block[0] = (atomic ? CALC_KIND_STRING : CALC_KIND_ARRAY) | (n << 8);
    return block + 1;
}

/* 원소가 모두 int 일 때: tagged int 는 값 그대로 비교해도 순서가 같아 calc_compare 를 부르지 않음.
   qsort 는 비교마다 함수 포인터를 부르므로 아래에서 위로 merge sort (stable) */
static void sort_ints(value *items, int64_t n) {
    value *a = items, *t = scratch(n, 1);
    for (int64_t w = 1; w < n; w *= 2) {
        for (int64_t lo = 0; lo < n; lo += 2 * w) {
            int64_t mid = lo + w < n ? lo + w : n, hi = lo + 2 * w < n ? lo + 2 * w : n;
            int64_t i = lo, j = mid, k = lo;
            while (i < mid && j < hi) t[k++] = (int64_t)a[j] < (int64_t)a[i] ? a[j++] : a[i++];
            while (i < mid) t[k++] = a[i++];
            while (j < hi) t[k++] = a[j++];
        }
        value *x = a; a = t; t = x;
    }
    if (a != items) memcpy(items, a, (size_t)n * sizeof(value));
}

/* append (reverse xs) ys (Codegen 이 이 모양을 알아보고 부름): xs 의 원소를 하나씩 ys 앞에 붙임. 칸 n 개 */
value calc_rev_append(value xs, value ys) {
    for (; xs != CALC_NIL; xs = TAIL(xs)) ys = calc_cons(HEAD(xs), ys);
    return ys;
}

value calc_builtin_reverse(value xs) {
    value r = CALC_NIL;
    for (; xs != CALC_NIL; xs = TAIL(xs)) r = calc_cons(HEAD(xs), r);
    return r;
}

/* xs 의 원소를 차례로 새 칸에 담고 마지막 칸을 ys 에 이음 (ys 는 그대로 공유) */
value calc_builtin_append(value xs, value ys) {
    if (xs == CALC_NIL) return ys;
    value first = calc_cons(HEAD(xs), ys), last = first;
    for (xs = TAIL(xs); xs != CALC_NIL; xs = TAIL(xs)) {
        value cell = calc_cons(HEAD(xs), ys);
        TAIL(last) = cell;
        calc_write_barrier(&TAIL(last), cell);
        last = cell;
    }
    return first;
}

/* a 부터 b 까지. b < a 면 []. 끝값이 int 의 끝이어도 멈추도록 64비트로 셈 */
value calc_builtin_range(value a, value b) {
    value r = CALC_NIL;
    for (int64_t i = INT(b); i >= INT(a); i--) r = calc_cons(calc_int((int32_t)i), r);
    return r;
}

/* int 끼리 또는 string 끼리 (calc_compare). 같다고 비교되는 원소는 내용도 같으므로 안정 정렬이 아니어도 결과가 같음 */
value calc_builtin_sort(value xs) {
    int64_t n = calc_list_length(xs);
    value *items = scratch(n, 0);
    int all_int = 1;
    for (int64_t i = 0; i < n; i++, xs = TAIL(xs)) { items[i] = HEAD(xs); all_int &= CALC_IS_INT(items[i]); }
    if (all_int) sort_ints(items, n);
    else qsort(items, (size_t)n, sizeof(value), compare_for_sort);
    value r = CALC_NIL;
    for (int64_t i = n - 1; i >= 0; i--) r = calc_cons(items[i], r);
    return r;
}

/* ---- 출력 ---- */

/* print, println: 문자열은 따옴표 없이, 나머지는 formatValue (toString 과 같음). UTF-8 로 표준 출력 */
static void put_display(value v) {
    if (CALC_IS_INT(v)) { /* 버퍼와 UTF-8 변환 없이 */
        char text[16];
        text[15] = 0;
        fputs(calc_format_int(INT(v), text + 15), stdout);
        return;
    }
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

/* ---- 예외 ---- */

value calc_builtin_raise(value exn) { calc_raise(exn); }

/* ---- 배열: [헤더, 원소...] 또는 bool 배열 [헤더, 바이트...] (Eval.callBuiltin 과 같은 오류 문장) ---- */

/* 원소 example 이 bool 이면 bool 배열 (바이트 하나씩, 안에 포인터가 없어 GC 가 훑지 않음) */
static value new_array(int64_t length, value example) {
    if (length < 0) length = 0;
    if (length > 0 && CALC_IS_BOOL(example)) {
        int64_t *block = calc_alloc_atomic(8 + length);
        if (block == NULL) calc_error("out of memory");
        block[0] = CALC_KIND_BOOL_ARRAY | (length << 8);
        return (value)block;
    }
    int64_t *block = calc_alloc(8 * (1 + length));
    block[0] = CALC_KIND_ARRAY | (length << 8);
    return (value)block;
}

value calc_builtin_Array_ofList(value xs) {
    value a = new_array(calc_list_length(xs), xs == CALC_NIL ? CALC_UNIT : HEAD(xs));
    int64_t i = 0;
    for (value p = xs; p != CALC_NIL; p = TAIL(p)) calc_array_set(a, i++, HEAD(p));
    return a;
}

value calc_builtin_Array_toList(value a) {
    value list = CALC_NIL;
    for (int64_t i = CALC_SIZE(a) - 1; i >= 0; i--) list = calc_cons(calc_array_get(a, i), list);
    return list;
}

value calc_builtin_Array_length(value a) { return calc_int((int32_t)CALC_SIZE(a)); }

value calc_builtin_Array_create(value n, value v) {
    if (INT(n) < 0) calc_errorf("Array.create: the length must be non-negative (got %d)", INT(n));
    value a = new_array(INT(n), v);
    if (CALC_KIND(a) == CALC_KIND_BOOL_ARRAY) memset(CALC_BOOL_BYTES(a), CALC_BOOL_VAL(v), (size_t)INT(n));
    else for (int64_t i = 1; i <= INT(n); i++) CALC_FIELD(a, i) = v;
    return a;
}

/* 범위 밖은 .NET 의 IndexOutOfRangeException 과 같은 문장 */
static int64_t checked(value a, value i) {
    int32_t index = INT(i);
    if (index < 0 || index >= CALC_SIZE(a)) calc_error("Index was outside the bounds of the array.");
    return index;
}

value calc_builtin_array_get(value a, value i) { return calc_array_get(a, checked(a, i)); }

value calc_builtin_array_set(value a, value i, value v) {
    calc_array_set(a, checked(a, i), v);
    return CALC_UNIT;
}
