---
order: 85
created: 2026-10-30
commit: fb18784
version: 0.60.0
description: 타입 정보가 없는 IR의 컴파일러에서도 bool 배열을 원소마다 바이트 하나로 만들 수 있다. 배열을 만드는 런타임 함수가 원소 값을 보고 종류를 정하고, 헤더에 종류를 적는다. 생성 코드는 접근마다 헤더의 종류로 갈라진다. 포인터가 없는 블록은 GC_MALLOC_ATOMIC으로 할당한다. 빈 배열은 어느 종류여도 되므로 =는 종류가 아니라 길이와 원소로 비교한다
---

# bool 배열을 원소마다 바이트 하나로 저장하기

> 관련 커밋: [fb18784](https://github.com/ohama/calc-mlir/commit/fb18784) (v0.60.0)

0.59.0까지 컴파일러의 배열은 원소가 무엇이든 `[헤더, 원소...]`였고 원소마다 tagged 값 8바이트였다. bool은 `3`(false)이나 `11`(true)이므로 1비트 정보에 8바이트를 썼다. 에라토스테네스의 체처럼 큰 bool 배열을 쓰는 프로그램은 메모리 대역폭에 묶인다.

| 실행 파일 | 0.59.0 | 0.60.0 | 최대 메모리 | 같은 C 코드 (8바이트 / 1바이트 원소) |
|---|---|---|---|---|
| 체 1억 | 0.65초 | 0.41초 | 804MB → 103MB | 0.55초 / 0.23초 |
| int 배열 1천만 개를 20번 더함 | 0.83초 | 0.83초 | | |

## The Insight

**원소의 타입을 컴파일러가 몰라도, 배열을 만드는 순간의 원소 값은 안다.** 타입 검사를 통과한 프로그램에서 원소 하나가 bool이면 모든 원소가 bool이다. 그래서 배열을 만드는 런타임 함수가 첫 원소를 보고 종류를 정해 헤더에 적는다. 그 뒤에는 헤더가 표현을 알려 준다.

```
보통 배열 (종류 1)   [헤더 1 | n<<8][원소 0 (8B)][원소 1 (8B)]...
bool 배열 (종류 9)   [헤더 9 | n<<8][b0][b1][b2]...       b = 0 또는 1
```

타입 정보를 codegen까지 넘기는 방법(`80-pass-inferred-int-types-to-codegen.md`)도 있지만, 배열은 함수 인자로 넘어가고 다형적인 prelude 함수(`Array.map`, `Array.fold`)를 거친다. 접근하는 곳마다 타입을 아는 것은 어렵다. 반면 헤더는 이미 접근마다 읽고 있다(범위 검사의 길이). 같은 단어의 아래 8비트를 비교하는 것이 추가 비용의 전부다.

## Why This Matters

- **8배 작으면 캐시와 GC가 같이 좋아진다.** 1억 원소가 800MB에서 100MB가 된다. 포인터가 없으므로 `GC_MALLOC_ATOMIC`으로 할당하면 Boehm GC가 내용을 훑지 않는다.
- **같은 C 코드가 기준이다.** C로 체를 8바이트 원소로 쓰면 0.55초, 1바이트 원소로 쓰면 0.23초였다. 생성 코드의 문제가 아니라 원소 크기의 문제라는 것을 먼저 확인했다.
- **빈 배열은 종류를 정할 수 없다.** 원소가 없으면 볼 값이 없다. 빈 `bool array`가 종류 1이어도 원소를 읽을 일이 없으므로 문제없다. 그러나 `=`가 종류를 비교하면 `[||] = Array.create 0 true` 같은 비교가 틀린다.
- **배열을 만드는 경로가 하나라도 빠지면 틀린다.** 생성 코드가 직접 `[헤더 1, ...]`을 만드는 곳이 남아 있으면, 그 배열은 보통 배열이고 접근은 헤더를 보므로 결과는 맞다. 그러나 런타임 함수 중 하나가 종류를 보지 않고 `CALC_FIELD(a, 1 + i)`로 읽으면 바이트 배열의 엉뚱한 곳을 읽는다.

## Recognition Pattern

- 언어의 값이 모두 한 단어(tagged 값)이고 배열도 원소마다 한 단어일 때
- 큰 bool(또는 char, 작은 int) 배열을 쓰는 프로그램이 같은 C 코드보다 크게 느리고 메모리를 많이 쓸 때
- codegen에 원소의 타입이 없지만 블록에 헤더가 있을 때

## The Approach

### Step 1: 같은 C 코드의 원소 크기만 바꿔서 잰다

```c
#ifndef ELEM
#define ELEM int64_t      /* uint8_t 로 바꿔서 한 번 더 */
#endif
ELEM *composite = GC_MALLOC_ATOMIC(sizeof(ELEM) * (n + 1));
for (int64_t i = 2; i * i <= n; i++)
    if (!composite[i]) for (int64_t j = i * i; j <= n; j += i) composite[j] = 1;
```

```bash
clang -O2 sieve.c -lgc -o sieve8                      # 0.55초
clang -O2 -DELEM=uint8_t sieve.c -lgc -o sieve1       # 0.23초
```

차이가 크면 진행한다. int 배열은 원소가 값 그대로라 바꿀 것이 없다.

### Step 2: 런타임에 종류와 접근 함수를 둔다

```c
#define CALC_KIND_BOOL_ARRAY 9
#define CALC_IS_ARRAY(v) (CALC_IS_KIND(v, CALC_KIND_ARRAY) || CALC_IS_KIND(v, CALC_KIND_BOOL_ARRAY))
#define CALC_BOOL_BYTES(v) ((uint8_t *)&CALC_FIELD(v, 1))

value calc_array_get(value a, int64_t i) {
    if (CALC_KIND(a) == CALC_KIND_BOOL_ARRAY) return calc_bool(CALC_BOOL_BYTES(a)[i]);
    return CALC_FIELD(a, 1 + i);
}
```

- **만드는 곳은 하나로 모은다.** `new_array(length, example)`이 `example`이 bool이면 바이트 배열을 만든다. `Array.create`는 초기값을, `Array.ofList`는 첫 원소를 넘긴다. 배열 리터럴과 prelude의 `init`, `map`, `copy`는 모두 `ofList`를 거치므로 따로 고칠 것이 없다.
- **읽고 쓰는 곳도 모은다.** 출력, `=`, `toList`, `get`, `set`에서 `CALC_FIELD(a, 1 + i)`를 찾아 `calc_array_get`/`calc_array_set`으로 바꾼다. `grep CALC_KIND_ARRAY`로 종류를 직접 비교하는 곳도 `CALC_IS_ARRAY`로 바꾼다.
- **`=`는 길이와 원소로 비교한다.** 빈 배열은 종류가 다를 수 있다.

### Step 3: 생성 코드는 접근마다 종류로 갈라진다

범위 검사에서 이미 헤더를 읽으므로, 같은 값에서 종류를 꺼낸다.

```fsharp
let header = load block {tbaa = [#calc_tbaa_header]}
let size = header >>> 8                 // 범위 검사
let isBytes = (header &&& 0xff) = 9
```

```fsharp
emitIf fb isBytes
    (fun () -> // 읽기: 바이트 b 를 bool 값 (b << 3) | 3 으로
        ... llvm.load (block + 8 + index) : i8, arith.extui, shli 3, ori 3)
    (fun () -> ... llvm.load block[1 + index] : i64)
```

쓰기는 `(v >> 3) & 1`을 `trunci`해서 바이트로 저장한다. 반복 안에서 종류는 바뀌지 않으므로 분기는 항상 같은 쪽으로 가고 branch predictor가 맞춘다.

### Step 4: 헤더 읽기에 따로 TBAA 태그를 붙인다

원소 저장은 `#calc_tbaa_array`다. 헤더 읽기에 태그가 없으면 LLVM은 원소 저장이 헤더를 바꿀 수 있다고 본다. `#calc_tbaa_header`를 따로 두면 원소 저장 뒤에도 헤더를 다시 읽지 않아도 된다는 것을 안다.

```
#calc_tbaa_header_type = #llvm.tbaa_type_desc<id = "header", members = {<#calc_tbaa_root, 0>}>
#calc_tbaa_header = #llvm.tbaa_tag<base_type = #calc_tbaa_header_type, access_type = #calc_tbaa_header_type, offset = 0>
```

`!invariant.load`로 헤더 읽기를 반복 밖으로 빼는 것도 시도했다. 0.41초 그대로였다. 헤더는 L1 cache에 있으므로 읽기가 비싸지 않았다. 효과 없는 변경은 넣지 않는다.

### Step 5: 표현이 드러나는 경우를 차등 테스트한다

| 경우 | 확인하는 것 |
|---|---|
| `Array.create`, get, set, length, `toList` | 바이트 경로의 읽기와 쓰기 |
| 리터럴과 `Array.ofList`의 `=`, 빈 배열의 `=` | 종류가 달라도 같은 값 |
| prelude의 `init`, `map`, `copy`, `fold`, `iter` | 모든 생성 경로가 바이트 배열을 만듦 |
| 함수 인자로 받은 배열 | 접근하는 곳에서 종류를 모름 |
| 범위 밖 읽기, 음수 번호 쓰기 | 오류 문장과 종료 코드 |
| int, string, 튜플 원소 | 보통 배열 경로가 그대로 |
| 큰 bool 배열을 붙잡은 채 할당 | `GC_MALLOC_ATOMIC` 블록이 수거되지 않음 |

기대값은 인터프리터의 출력이다. 생성한 MLIR에 바이트 읽기와 쓰기(`-> i8`, `: i8, !llvm.ptr`), 종류 비교(`arith.constant 9`), 헤더 태그가 있는지도 확인한다.

## Example

C와 남은 차이(0.41초와 0.23초)는 원소 크기가 아니라 표시 반복의 tagged 덧셈이다.

```
let j = ref (!i * !i)
while !j <= n do
    isPrime.[!j] <- false
    j := !j + !i
```

`!j + !i`는 tagged int 덧셈이라 서로 기다리는 명령 세 개(더하기, 32비트에서 넘침을 맞추는 시프트 두 번)가 된다. C는 덧셈 하나다. 원소 표현을 바꾼 효과와 다른 원인을 섞어 기대하지 않는다.

## 체크리스트

- [ ] 같은 C 코드에서 원소 크기만 바꿔 효과를 먼저 쟀는가?
- [ ] 배열을 만드는 모든 경로가 한 함수(`new_array`)를 거치는가?
- [ ] 런타임에서 원소를 직접 읽고 쓰는 곳(`CALC_FIELD(a, 1 + i)`)을 모두 접근 함수로 바꿨는가?
- [ ] 종류를 직접 비교하는 곳을 `CALC_IS_ARRAY`로 바꿨는가?
- [ ] 빈 배열의 종류가 달라도 `=`가 같다고 하는가?
- [ ] 포인터가 없는 블록을 `GC_MALLOC_ATOMIC`으로 할당했는가?
- [ ] 생성 코드의 헤더 읽기에 원소와 다른 TBAA 태그를 붙였는가?
- [ ] int 배열처럼 바뀌지 않아야 할 경우가 느려지지 않았는지 쟀는가?

## 관련 문서

- `81-give-llvm-alias-info-with-tbaa-and-noreturn.md` - TBAA 태그와 noreturn으로 LLVM에 알리기
- `84-encode-constructor-identity-in-the-header.md` - 헤더의 위쪽 비트에 생성자 번호 넣기
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
