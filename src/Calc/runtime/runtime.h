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
#define CALC_UNIT 7     /* () */

#define CALC_IS_INT(v) (((v) & CALC_TAG_MASK) == CALC_TAG_INT)
#define CALC_IS_BOOL(v) (((v) & CALC_TAG_MASK) == CALC_TAG_BOOL)
#define CALC_INT_VAL(v) ((int32_t)((v) >> 3))
#define CALC_BOOL_VAL(v) ((int)((v) >> 3))
#define CALC_IS_PTR(v) (((v) & CALC_TAG_MASK) == 0)

/* 힙 블록: 첫 단어가 헤더 = 종류 | (뒤따르는 단어 수 << 8). Codegen.closureHeader 와 같음 */
#define CALC_KIND_CLOSURE 1 /* [헤더, 코드 포인터, 캡처한 값...] */
#define CALC_KIND_STRING 2  /* [헤더, 길이, UTF-16 코드 단위...] (.NET string 과 같은 단위) */
#define CALC_KIND_CONS 3    /* [헤더, head, tail]. tail 은 cons 또는 CALC_NIL */
#define CALC_KIND_TUPLE 4   /* [헤더, 원소...]. 원소 수 = 헤더의 단어 수 */
#define CALC_KIND_DATA 5    /* [헤더, 생성자 이름(const char *), 인자(단어 수가 2일 때)]. 인자 없는 생성자는 전역 상수 블록 */
#define CALC_HEADER(v) (((int64_t *)(v))[0])
#define CALC_KIND(v) (CALC_HEADER(v) & 0xff)
#define CALC_IS_KIND(v, kind) (CALC_IS_PTR(v) && CALC_KIND(v) == (kind))

typedef struct {
    int64_t header;
    int64_t length; /* UTF-16 코드 단위 수 */
    uint16_t units[];
} calc_string;

#define CALC_STRING(v) ((calc_string *)(v))
#define CALC_FIELD(v, i) (((value *)(v))[(i)])
#define CALC_SIZE(v) (CALC_HEADER(v) >> 8)

#define CALC_TRUE ((value)11)
#define CALC_FALSE ((value)3)

void calc_init(void);
void *calc_alloc(int64_t bytes);
/* "Error: <message>" 를 표준 에러로 출력하고 종료 코드 1 로 끝냄 (인터프리터와 같은 형식) */
void calc_error(const char *message);
/* printf 형식의 calc_error */
void calc_errorf(const char *format, ...);
/* Eval.formatValue 와 같은 형식으로 결과를 출력. () 면 아무것도 출력하지 않음 (calc 파일 실행과 같음) */
void calc_print_result(value v);

/* 인터프리터(.NET 예외 메시지)와 같은 문장으로 끝냄 */
void calc_error_divide_by_zero(void);
void calc_error_overflow(void);

/* 타입이 여럿인 연산: 태그를 보고 동작을 고름 (docs/compiler-design.md D6) */
value calc_add(value a, value b);       /* int 덧셈 (32비트에서 한 바퀴 돎) */
value calc_eq(value a, value b);        /* 구조적 비교, 결과는 bool 값 */
int32_t calc_compare(value a, value b); /* -1, 0, 1 */

/* "Error: Match failure: <값>" (Eval.formatValue 형식) 을 출력하고 종료 */
void calc_match_failure(value v);

/* 클로저 f 를 인자 하나로 호출. 생성한 코드의 호출 규약 (클로저, 인자) -> 결과 와 같음 */
value calc_apply(value f, value arg);

/* option: Some 과 None 의 이름과 None 값은 런타임에 하나. 생성한 코드도 이 전역을 가리킴 (패턴 검사가 이름 포인터를 비교) */
extern const char calc_name_some[];
extern const char calc_name_none[];
typedef struct {
    int64_t header;
    const char *name;
} calc_unit;
extern const calc_unit calc_none;
#define CALC_NONE ((value)&calc_none)

/* ---- 런타임 안에서 함께 쓰는 도우미 (runtime.c, builtins.c) ---- */

value calc_int(int32_t n);
value calc_bool(int b);
calc_string *calc_new_string(int64_t length); /* 내용은 부르는 쪽이 채움 */
value calc_string_of(const uint16_t *units, int64_t length);
value calc_cons(value head, value tail);
int64_t calc_list_length(value list);

/* UTF-16 버퍼 (malloc). formatValue 결과를 모으고, 출력할 때 UTF-8 로 바꿈 */
typedef struct {
    uint16_t *units;
    int64_t length;
    int64_t capacity;
} calc_buffer;
void calc_buffer_units(calc_buffer *b, const uint16_t *units, int64_t length);
void calc_buffer_value(calc_buffer *b, value v);                                  /* Eval.formatValue */
void calc_buffer_quoted(calc_buffer *b, const uint16_t *units, int64_t length); /* Eval.quote */
char *calc_buffer_utf8(calc_buffer *b);                                         /* 새 malloc 문자열, 버퍼는 비움 */

/* ---- 내장 함수 (builtins.c). 인자 순서와 의미는 Eval.callBuiltin 과 같음 ---- */

value calc_builtin_length(value v);
value calc_builtin_substr(value s, value start, value count);
value calc_builtin_upper(value s);
value calc_builtin_lower(value s);
value calc_builtin_trim(value s);
value calc_builtin_contains(value s, value sub);
value calc_builtin_startsWith(value s, value prefix);
value calc_builtin_endsWith(value s, value suffix);
value calc_builtin_indexOf(value s, value sub);
value calc_builtin_replace(value s, value old_value, value new_value);
value calc_builtin_split(value s, value sep);
value calc_builtin_join(value sep, value items);
value calc_builtin_toString(value v);
value calc_builtin_toInt(value s);
value calc_builtin_head(value xs);
value calc_builtin_tail(value xs);
value calc_builtin_nth(value xs, value i);
value calc_builtin_sort(value xs);
value calc_builtin_print(value v);
value calc_builtin_println(value v);

#endif
