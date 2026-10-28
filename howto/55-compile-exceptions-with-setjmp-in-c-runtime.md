---
order: 55
created: 2026-10-28
commit: 583d1f6
version: 0.32.0
description: 네이티브 코드의 try는 setjmp를 부른 함수가 살아 있어야 longjmp할 수 있다. 생성한 코드에서 setjmp를 부르지 않고, try의 본문과 handler를 클로저로 만들어 C 런타임의 calc_try가 _setjmp한 뒤 부른다. handler를 부르기 전에 handler 목록에서 자신을 빼고, 맞는 분기가 없으면 다시 raise하는 분기를 Lower가 덧붙인다
---

# C 런타임의 setjmp로 예외를 컴파일하기

> 관련 커밋: [583d1f6](https://github.com/ohama/calc-mlir/commit/583d1f6) (v0.32.0)

인터프리터의 예외는 쉽다. F#의 예외로 던지고 `try … with`로 받으면 된다.

```fsharp
| Try (body, cases) ->
    try evalIn env body
    with CalcException exn -> (* 분기를 차례로 맞춰 보고, 맞는 분기가 없으면 raise (CalcException exn) *)
```

네이티브 코드에는 이런 것이 없다. C++ 예외처럼 unwinding 테이블(`invoke`, `landingpad`)을 쓰는 방법도 있지만, 런타임과 생성 코드 모두에 personality 함수와 테이블이 필요하다. 할당을 모두 GC에 맡기는 언어라면 정리할 자원이 없으므로, `setjmp`/`longjmp`로 "가장 안쪽 `try`로 바로 돌아가기"만 하면 된다.

## The Insight

**`setjmp`는 생성한 코드가 아니라 C 런타임에서 부른다. 그러려면 `try`의 본문을 클로저로 만들어 C 함수 안에서 부르면 된다.**

`longjmp`는 `setjmp`를 부른 함수가 **아직 돌아가지 않았을 때만** 그 지점으로 돌아갈 수 있다. 그래서 `calc_try(body, handler)` 같은 C 함수가 `setjmp`한 뒤 그대로 돌아가 버리면 쓸 수 없다. 본문 실행이 끝날 때까지 그 C 함수가 살아 있어야 한다. 본문을 클로저로 넘기면 C 함수가 본문을 직접 부르므로 이 조건이 저절로 맞는다.

```
try body with | p1 -> e1 | p2 -> e2
  ==> Lower: CTry (fun () -> body,
                   fun $exn -> match $exn with | p1 -> e1 | p2 -> e2 | _ -> raise $exn)
  ==> Codegen: llvm.call @calc_try(%body, %handler) : (i64, i64) -> i64
```

생성한 코드에서 `setjmp`를 직접 부르려면 할 일이 많다. 그 함수에 `returns_twice`를 붙여 LLVM이 레지스터에 값을 두지 않게 해야 하고, 플랫폼마다 다른 `jmp_buf` 크기만큼 공간을 잡아야 한다. C 쪽에 두면 컴파일러(clang)가 이것을 알아서 처리한다.

## Why This Matters

- **`setjmp`를 하는 C 함수가 먼저 돌아간 경우:** `longjmp`가 이미 없어진 스택 프레임으로 돌아간다. undefined behavior라서 가끔만 죽는다.
- **handler 목록에서 자신을 빼지 않고 handler를 부른 경우:** handler 안에서 다시 raise하면 같은 `try`로 돌아가 무한히 돈다.
- **맞는 분기가 없을 때 아무것도 안 한 경우:** OCaml에서 `try … with Not_found -> …`는 다른 예외를 그대로 바깥으로 보낸다. 다시 raise하는 분기가 없으면 `Match failure`가 나거나 예외가 사라진다.
- **`setjmp`를 쓴 경우:** macOS의 `setjmp`는 시그널 마스크를 저장하느라 시스템 호출을 한다. `try`가 반복문 안에 있으면 느려진다. `_setjmp`/`_longjmp`는 레지스터만 저장한다.

## Recognition Pattern

- GC를 쓰는 언어를 LLVM이나 MLIR로 컴파일하는데 예외나 non-local exit가 필요할 때
- 인터프리터의 예외 의미를 네이티브 코드에 그대로 옮겨야 할 때
- 런타임이 C이고 unwinding 테이블은 아직 다루고 싶지 않을 때

## The Approach

### Step 1: Lower에서 try를 클로저 두 개로 바꾼다

```fsharp
| Try (body, cases) ->
    let reraise = PWildcard, None, CBuiltin ("raise", [ CVar "$exn" ])
    let handler = CMatch (CVar "$exn", List.map lowerCase cases @ [ reraise ])
    CTry (state.AddFunction("$unit", None, lower state ctors scope body),
          state.AddFunction("$exn", None, handler))
```

- 본문 클로저는 `()`를 받는다. closure conversion이 본문의 free variable을 캡처한다.
- handler는 사용자 분기 뒤에 "나머지는 다시 raise" 분기를 덧붙인 `match`다. 사용자가 이름을 가린 `raise`가 아니라 내장 함수 호출(`CBuiltin`)을 바로 쓴다.
- Core에 `CTry` 하나만 더하면 되고, free variable 계산은 두 클로저의 합이다.

### Step 2: C 런타임에 handler 스택을 둔다

```c
typedef struct calc_handler {
    jmp_buf jump;
    struct calc_handler *outer;
} calc_handler;

static calc_handler *calc_handlers = NULL;   /* 안쪽 try 가 앞 */
static value calc_raised;                    /* _longjmp 한 뒤 calc_try 가 읽음 */

value calc_try(value body, value handler) {
    calc_handler h;                          /* calc_try 의 스택 프레임에 있음 */
    h.outer = calc_handlers;
    calc_handlers = &h;
    if (_setjmp(h.jump) == 0) {
        value result = calc_apply(body, CALC_UNIT);
        calc_handlers = h.outer;             /* 정상 종료: 이 try 를 뺌 */
        return result;
    }
    calc_handlers = h.outer;                 /* handler 안의 raise 는 바깥 try 로 */
    return calc_apply(handler, calc_raised);
}

void calc_raise(value exn) {
    if (calc_handlers != NULL) {
        calc_raised = exn;
        _longjmp(calc_handlers->jump, 1);
    }
    /* 받는 try 가 없음: 인터프리터와 같은 메시지로 종료 */
}
```

- `_setjmp` 뒤에 바꾸는 지역 변수가 없다. `longjmp` 뒤에 값이 어긋날 수 있는 변수(`volatile`이 필요한 변수)를 만들지 않는다.
- `calc_raise`와 `calc_try` 사이의 생성 코드 프레임은 그대로 버려진다. 할당은 모두 GC가 관리하므로 정리할 것이 없다. 버려진 스택에 남은 포인터도 conservative GC가 더는 보지 않는다.

### Step 3: 잡히지 않은 예외의 출력을 인터프리터와 맞춘다

differential testing이 비교하려면 출력이 같아야 한다.

| 예외 | 출력 (표준 에러, 종료 코드 1) |
|---|---|
| `Failure msg` | `Error: msg` (예전 내장 함수 오류와 같은 모양) |
| 그 밖 | `Error: exception <값>` (`Error: exception E (1, "x")`) |

인터프리터는 `CalcException.Message`가, C는 `calc_raise`가 같은 규칙으로 만든다. `Failure`를 메시지만 출력하게 했더니, `head`, `nth`를 prelude에서 `failwith`로 다시 써도 기존 오류 테스트가 그대로 통과했다.

### Step 4: 쌓이고 풀리는 경우를 테스트한다

```
exception E in let rec f n = if n = 0 then raise E else 1 + f (n - 1) in try f 100000 with E -> -1
                                                  → -1       깊은 재귀에서 _longjmp
exception E of int in let rec go n = try (if n = 0 then raise (E 0) else go (n - 1)) with E k -> raise (E (k + 1)) in
try go 1000 with E k -> k                         → 1001     try 1000개가 쌓였다가 하나씩 다시 raise
let f () = try raise Not_found with Not_found -> 1 in (f (), f (), try f () + raise Not_found with Not_found -> 7)
                                                  → (1, 1, 7)  끝난 try 는 목록에서 빠짐
try raise Not_found with Failure _ -> 1           → Error: exception Not_found   맞는 분기가 없으면 다시 raise
```

이 예제들은 인터프리터 예제 목록에도 넣는다. 차등 테스트가 두 구현을 비교한다.

## Example

```
$ calc --compile ./e -e 'try failwith "boom" with Failure m -> "caught " + m'
$ ./e
"caught boom"
```

생성 코드에는 `@lambda_0`(본문), `@lambda_1`(handler 안의 분기), `llvm.call @calc_try(%body, %handler)`, `llvm.call @calc_builtin_raise`가 보인다.

## 체크리스트

- [ ] `setjmp`를 하는 함수가 본문 실행이 끝날 때까지 살아 있는가?
- [ ] 정상 종료와 예외 두 경로 모두에서 handler 목록에서 자신을 빼는가? handler를 부르기 **전에** 빼는가?
- [ ] 맞는 분기가 없으면 다시 raise하는가?
- [ ] `_setjmp`/`_longjmp`(시그널 마스크 없음)를 쓰는가?
- [ ] 잡히지 않은 예외의 메시지와 종료 코드가 인터프리터와 같은가?
- [ ] 깊은 재귀, 쌓인 `try`, handler 안의 raise를 테스트하는가?

## 관련 문서

- `56-keep-exception-names-unique-for-name-based-identity.md` - 예외 이름과 soundness
- `30-implement-closure-conversion-to-top-level-functions.md` - 본문과 handler를 클로저로 만드는 closure conversion
- `52-write-standard-library-as-prelude-in-the-language.md` - `failwith`로 다시 쓴 `head`, `nth`
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 같은 출력
