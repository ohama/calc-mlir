---
order: 84
created: 2026-10-29
commit: 124eb44
version: 0.59.0
description: 생성자 블록의 이름 포인터 칸은 헤더의 위쪽 비트로 옮길 수 있다. 이름 전역의 주소를 런타임의 기준 전역에서 뺀 값은 같은 실행 파일 안이라 32비트에 들어간다. 인자 없는 생성자는 즉시값으로 만든다. C 런타임에서 그 값으로 이름을 찾을 때 포인터 연산을 쓰면 undefined behavior라 clang이 strlen을 잘못 최적화하므로 정수로 더한다
---

# 생성자 번호를 헤더에 넣어 이름 칸 없애기

> 관련 커밋: [124eb44](https://github.com/ohama/calc-mlir/commit/124eb44) (v0.59.0)

0.58.0까지 컴파일러의 생성자 값은 `[헤더, 이름 포인터, 인자...]` 블록이었다. 이름 포인터는 두 가지에 쓰였다. 패턴 검사에서 같은 생성자인지 비교하고, 출력에서 이름을 찾았다. 그러나 칸 하나가 블록 크기를 Boehm의 다음 16바이트 단위로 올리고 있었다.

| 생성자 | 0.58.0 (Boehm 객체) | 0.59.0 |
|---|---|---|
| `Some x` | 24B (32B) | 16B |
| `Node (l, v, r)` | 40B (48B) | 32B |

| 실행 파일 | 0.58.0 | 0.59.0 | 최대 메모리 |
|---|---|---|---|
| bst 2백만 개 | 1.83초 | 1.53초 | 168MB → 113MB |
| option을 많이 만드는 프로그램 | 0.57초 | 0.52초 | 787MB → 659MB |

## The Insight

**포인터는 64비트지만, 같은 실행 파일 안의 두 주소의 차이는 32비트에 들어간다.** 생성자 이름은 생성한 코드의 전역이고, 런타임의 `calc_name_none`도 같은 실행 파일에 정적으로 링크된다. 그래서 다음과 같이 정한다.

```
생성자 번호 = 이름 전역의 주소 - calc_name_none 의 주소      (None 은 0)
헤더       = 종류 5 | (인자 단어 수 << 8) | (번호 << 32)
인자 없는 생성자 = (번호 << 32) | 6                          (블록이 아닌 즉시값)
```

이 번호는 이름 포인터가 하던 두 일을 모두 한다.

| 쓰는 곳 | 0.58.0 | 0.59.0 |
|---|---|---|
| 같은 생성자인가 | 이름 칸의 포인터 비교 | 헤더의 위쪽 32비트 비교 |
| 출력할 이름 | 이름 칸 | `calc_name_none + 번호` |

번호를 따로 정하는 표(생성자마다 0, 1, 2…)가 필요 없다. 프로그램 여럿을 한 실행 파일에 링크해도(테스트의 `buildMany`) 이름 전역의 주소가 서로 다르므로 겹치지 않는다.

## Why This Matters

- **블록 크기가 할당기 단위를 넘는지가 중요하다.** 단어 하나를 줄여도 할당기의 단위(16바이트)를 넘지 않으면 효과가 없다. 인자 단어 수가 홀수인 생성자에서만 작아진다(`Rect (w, h)`는 32바이트 그대로).
- **링크할 때 정해지는 값은 상수로 만들 수 없다.** 두 전역 주소의 차이를 `<< 32`한 값은 Mach-O 재배치로 표현할 수 없다. 그래서 전역 상수 블록에 넣을 수 없다. 인자 없는 생성자를 즉시값으로 만들면 이 문제도 같이 없어진다. 생성 코드가 주소 두 개를 빼서 계산하고, LLVM이 그 계산을 반복 밖으로 뺀다.
- **C 런타임의 포인터 연산이 잘못된 코드가 된다.** `calc_name_none + 번호`는 `"None"` 배열 밖을 가리키는 포인터 연산이다. 이것은 undefined behavior다. clang `-O2`는 결과가 그 배열 안이라고 보고 `strlen`을 최적화했다. 그 결과 인자 없는 생성자 뒤의 출력이 모두 사라졌다(`[A; B` 뒤의 `]`). 디버그 출력을 넣으면 최적화가 바뀌어 증상이 사라지므로 찾기 어렵다.

## Recognition Pattern

- tagged 값 표현에서 생성자(태그, 이름) 칸이 따로 있는 블록이 뜨거울 때
- 할당기가 16바이트 단위인데 많이 쓰는 블록이 단위보다 한 단어 클 때
- 런타임이 전역의 주소에 정수를 더해 다른 전역을 찾을 때

## The Approach

### Step 1: 같은 할당기를 쓰는 C 모델로 효과를 먼저 잰다

```c
#define W 5   /* 5: [hdr, name, l, v, r]   4: [hdr, l, v, r] */
static T node(T l, intptr_t v, T r) { T n = GC_MALLOC(W * 8); ... }
```

```bash
clang -O2 -DW=5 bstm.c -lgc -o bstm5    # 1.93초, GC_size = 48
clang -O2 -DW=4 bstm.c -lgc -o bstm4    # 1.60초, GC_size = 32
```

### Step 2: 값 표현을 정하고 런타임 매크로로 모은다

```c
#define CALC_TAG_CTOR 6
#define CALC_IS_CTOR0(v) (((v) & CALC_TAG_MASK) == CALC_TAG_CTOR)
#define CALC_CTOR_NUMBER(word) ((int64_t)(word) >> 32)       /* 헤더나 즉시값에서. 음수일 수 있어 산술 시프트 */
#define CALC_CTOR_NAME(word) ((const char *)((intptr_t)calc_name_none + CALC_CTOR_NUMBER(word)))
#define CALC_DATA_SIZE(v) ((CALC_HEADER(v) >> 8) & 0xffffff) /* 위쪽 비트에 번호가 있으므로 가림 */
#define CALC_NONE ((value)CALC_TAG_CTOR)                     /* 번호 0 */
```

- **정수로 더한다:** `calc_name_none + n`이 아니라 `(intptr_t)calc_name_none + n`이다. 정수에서 만든 포인터는 컴파일러가 어느 배열을 가리키는지 가정하지 않는다.
- **크기 필드를 가린다:** 생성자 블록의 단어 수는 `CALC_DATA_SIZE`로 읽는다. 배열 길이는 `CALC_SIZE`(위쪽 비트까지)를 그대로 쓴다. 배열은 1억 원소일 수 있어 24비트에 들어가지 않는다.
- **즉시값을 처리할 곳:** 출력과 `=`에 즉시값 경우를 더한다. `=`에서 `a`가 블록이어도 `b`는 같은 타입의 즉시값일 수 있으므로 `b`의 태그를 먼저 본다.

### Step 3: Codegen은 번호를 계산하는 함수 하나를 쓴다

```fsharp
/// 생성자 번호 << 32. None 은 0 이라 상수
let private emitCtorBits (fb: FunctionBuilder) (ctor: string) =
    if ctor = "None" then emitConst fb "i64" 0L
    else
        let name = emitGlobalAddress fb (fb.Strings.CtorName ctor)
        let offset = emitOp fb "arith.subi" name (emitGlobalAddress fb "calc_name_none") "i64"
        emitOp fb "arith.shli" offset (emitConst fb "i64" 32L) "i64"
```

- **블록 만들기:** 헤더는 `bits | dataHeader n`(SSA 값)이다.
- **인자 없는 생성자:** `bits | 6`이다.
- **실행 중에 펴는 튜플 인자:** `calc_make_data`에 이름 대신 `bits`를 넘긴다.

### Step 4: 패턴 검사에서 즉시값과 블록을 나눈다

```fsharp
| Ast.PCtor (ctor, None) -> equalsValue (emitCtorImmediate fb ctor)      // 값 비교 한 번, 메모리를 읽지 않음
| Ast.PCtor (ctor, argPattern) ->
    // 태그가 0 일 때만 헤더를 읽음: 같은 타입의 인자 없는 생성자는 즉시값이라 읽으면 안 됨
    isBlock && (header &&& (-1L <<< 32)) = emitCtorBits fb ctor
```

인자 칸 번호도 하나씩 당긴다. 튜플 인자의 i번째 원소는 칸 `2 + i`가 아니라 `1 + i`다. 인자 하나는 단어 수가 1인지로 알아본다.

### Step 5: 표현이 드러나는 경우를 모두 차등 테스트한다

| 경우 | 프로그램 |
|---|---|
| 즉시값과 블록의 `=` | `A = B 1`, `B 1 = A`, `C (1, 2) = A` |
| 실행 중에 편 튜플 인자 | `let f = Some in f (1, 2)`, `Some p` 패턴 |
| 출력의 괄호 | `Some None`, `[None; Some (Some 1)]`, `B (-1)` |
| 예외 | `raise E`, `F (n, s)` 패턴, 받지 않은 `Not_found`, `failwith` |
| 수거 | 30만 개 `Node` 트리를 붙잡은 채 30만 개 `Some (x, x)`를 만듦 |

기대값은 인터프리터의 출력이다. 출력이 잘리는 버그는 이 테스트 중 출력 테스트가 잡았다.

## Example

undefined behavior는 디버그 출력을 넣으면 사라졌다.

```c
/* 잘못된 출력: (A        정상: (A, 1)   ← 같은 프로그램, fprintf 를 넣은 런타임 */
buffer_ascii(b, calc_name_none + CALC_CTOR_NUMBER(v));                      /* UB */
buffer_ascii(b, (const char *)((intptr_t)calc_name_none + CALC_CTOR_NUMBER(v)));  /* 정수 */
```

증상이 최적화에 따라 바뀌면, 그 코드에 undefined behavior가 있는지 먼저 의심한다.

## 체크리스트

- [ ] 줄인 단어가 할당기의 단위(16바이트)를 넘는 블록에서 효과가 있는지 C 모델로 쟀는가?
- [ ] 번호가 32비트에 들어가는 조건(같은 실행 파일에 정적으로 링크)을 확인했는가?
- [ ] 링크할 때 정해지는 값을 전역 상수에 넣으려 하지 않는가? (즉시값이나 실행 중 계산)
- [ ] C 런타임에서 주소에 정수를 더할 때 포인터가 아니라 정수로 더하는가?
- [ ] 헤더의 크기 필드를 읽는 곳에서 위쪽 비트를 가렸는가? (배열 길이는 그대로)
- [ ] 패턴 검사에서 헤더를 읽기 전에 즉시값이 아닌지 확인하는가?
- [ ] `=`와 출력에 즉시값 경우를 더했는가?

## 관련 문서

- `79-drop-cons-headers-with-a-pointer-tag.md` - 포인터 태그로 cons 칸의 헤더 없애기
- `76-flatten-constructor-tuple-payloads-by-block-size.md` - 생성자의 튜플 인자를 블록에 펴기
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
