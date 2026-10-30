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

/* 리스트의 칸: 헤더 없는 [head, tail] 16바이트 블록. 값은 블록 주소 | 2 (tail 은 cons 또는 CALC_NIL).
   헤더를 두면 24바이트라 Boehm GC 에서 32바이트를 씀. 주소 + 2 를 가리키는 포인터는 calc_init 이
   GC_register_displacement 로 알림 */
#define CALC_TAG_CONS 2
#define CALC_IS_CONS(v) (((v) & CALC_TAG_MASK) == CALC_TAG_CONS)
#define CALC_HEAD(v) (((value *)((v) - CALC_TAG_CONS))[0])
#define CALC_TAIL(v) (((value *)((v) - CALC_TAG_CONS))[1])

/* 힙 블록: 첫 단어가 헤더 = 종류 | (뒤따르는 단어 수 << 8). Codegen.closureHeader 와 같음 */
#define CALC_KIND_CLOSURE 1 /* [헤더, 코드 포인터, 캡처한 값...] */
#define CALC_KIND_STRING 2  /* [헤더, 길이, UTF-16 코드 단위...] (.NET string 과 같은 단위) */
#define CALC_KIND_TUPLE 4   /* [헤더, 원소...]. 원소 수 = 헤더의 단어 수 */
#define CALC_KIND_DATA 5    /* [헤더, 인자...]. 헤더 = 종류 | (인자 단어 수 << 8) | (생성자 번호 << 32). 단어 수 1: 튜플이 아닌
                               인자 하나, 2 이상: 튜플 인자의 원소를 편 것 (Node (l, v, r) 은 블록 하나). 인자 없는 생성자는
                               즉시값 (번호 << 32) | 6 (CALC_TAG_CTOR) */
#define CALC_KIND_REF 6     /* [헤더, 내용]. r := v 가 내용을 바꿈 */
#define CALC_KIND_RECORD 7  /* [헤더, 필드 이름들("x;y", const char *), 값...]. 값은 선언한 순서 */
#define CALC_KIND_ARRAY 8   /* [헤더, 원소...]. 원소 수 = 헤더의 단어 수. a.[i] <- v 가 원소를 바꿈 */
#define CALC_KIND_BOOL_ARRAY 9 /* bool 배열: [헤더, 원소마다 바이트 하나(0 또는 1)...]. 원소 수 = 헤더의 단어 수 자리.
                                  포인터가 없어 GC 가 훑지 않음 (GC_MALLOC_ATOMIC). 원소가 bool 이면 Array.create,
                                  Array.ofList 가 이것을 만듦 (타입 검사를 통과했으므로 원소 하나가 bool 이면 모두 bool) */
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
#define CALC_IS_ARRAY(v) (CALC_IS_KIND(v, CALC_KIND_ARRAY) || CALC_IS_KIND(v, CALC_KIND_BOOL_ARRAY))
#define CALC_BOOL_BYTES(v) ((uint8_t *)&CALC_FIELD(v, 1))

/* 생성자 번호 = 생성자 이름(C 문자열) 전역의 주소 - calc_name_none 의 주소. 생성한 코드와 런타임이 한 실행 파일에
   링크되므로 32비트에 들어감. 이름은 번호로 찾고, 같은 생성자인지는 번호로 비교 (이름 전역은 프로그램마다 하나) */
#define CALC_TAG_CTOR 6
#define CALC_IS_CTOR0(v) (((v) & CALC_TAG_MASK) == CALC_TAG_CTOR)
#define CALC_CTOR_NUMBER(word) ((int64_t)(word) >> 32) /* 헤더나 인자 없는 생성자의 즉시값에서 */
/* 주소를 정수로 더함: calc_name_none + 번호 는 배열 밖을 가리키는 포인터 연산(undefined behavior)이라 clang 이
   strlen 을 잘못 최적화함 ("None" 의 길이 안에 있다고 봄) */
#define CALC_CTOR_NAME(word) ((const char *)((intptr_t)calc_name_none + CALC_CTOR_NUMBER(word)))
#define CALC_DATA_SIZE(v) ((CALC_HEADER(v) >> 8) & 0xffffff)

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
void calc_error_index(void);
void calc_error_overflow(void);

/* 타입이 여럿인 연산: 태그를 보고 동작을 고름 (docs/compiler-design.md D6) */
value calc_add(value a, value b);       /* int 덧셈 (32비트에서 한 바퀴 돎) */
value calc_eq(value a, value b);        /* 구조적 비교, 결과는 bool 값 */
int32_t calc_compare(value a, value b); /* -1, 0, 1 */

/* 문자열 조각 2 ~ 8 개를 한 번 할당해 이음 */
value calc_concat2(value a, value b);
value calc_concat3(value a, value b, value c);
value calc_concat4(value a, value b, value c, value d);
value calc_concat5(value a, value b, value c, value d, value e);
value calc_concat6(value a, value b, value c, value d, value e, value f);
value calc_concat7(value a, value b, value c, value d, value e, value f, value g);
value calc_concat8(value a, value b, value c, value d, value e, value f, value g, value h);

/* "Error: Match failure: <값>" (Eval.formatValue 형식) 을 출력하고 종료 */
void calc_match_failure(value v);

/* 클로저 f 를 인자 하나로 호출. 생성한 코드의 함수는 tailcc 라 C 에서 바로 부를 수 없으므로,
   생성한 모듈이 정의함 (Codegen 의 declarations) */
value calc_apply(value f, value arg);

/* 예외. try e with ... 는 calc_try(fun () -> e, fun $exn -> match $exn with ... | _ -> raise $exn) (Lower).
   calc_raise 는 가장 안쪽 calc_try 로 돌아감 (_longjmp). 받는 try 가 없으면 Eval.CalcException 의 Message 처럼 출력하고 종료 */
value calc_try(value body, value handler);
void calc_raise(value exn) __attribute__((noreturn));

/* option: Some 과 None 의 이름은 런타임에 하나. 생성한 코드도 이 전역을 가리킴. calc_name_none 은 생성자 번호의 기준이라
   None 의 번호는 0, 값은 즉시값 6 */
extern const char calc_name_some[];
extern const char calc_name_none[];
#define CALC_NONE ((value)CALC_TAG_CTOR)

/* ---- 런타임 안에서 함께 쓰는 도우미 (runtime.c, builtins.c) ---- */

value calc_int(int32_t n);
value calc_bool(int b);
calc_string *calc_new_string(int64_t length); /* 내용은 부르는 쪽이 채움 */
value calc_string_of(const uint16_t *units, int64_t length);
value calc_cons(value head, value tail);
value calc_make_data(value bits, value tuple);
/* 배열의 i 번째 원소 (0 부터, 범위는 부르는 쪽이 확인). 두 종류의 배열을 모두 다룸 */
value calc_array_get(value a, int64_t i);
void calc_array_set(value a, int64_t i, value v);
value calc_data_payload(value v);
int64_t calc_list_length(value list);
/* int 의 10 진수 글자를 end 바로 앞에서 끝나게 씀 (snprintf 없이). 첫 글자의 위치 */
char *calc_format_int(int32_t n, char *end);

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
value calc_builtin_sort(value xs);
value calc_builtin_print(value v);
value calc_builtin_println(value v);
value calc_builtin_raise(value exn);
/* 배열: [| … |] 와 Array.ofList, a.[i], a.[i] <- v, Array.length, Array.create, Array.toList */
value calc_builtin_Array_ofList(value xs);
value calc_builtin_Array_toList(value a);
value calc_builtin_Array_length(value a);
value calc_builtin_Array_create(value n, value v);
value calc_builtin_array_get(value a, value i);
value calc_builtin_array_set(value a, value i, value v);

#endif
