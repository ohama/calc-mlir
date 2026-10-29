---
order: 86
created: 2026-10-30
commit: 88fb581
version: 0.61.0
description: 생성 코드를 여러 번 고친 뒤에는 벤치마크 몇 개가 아니라 언어 기능마다 작은 프로그램을 만들어 잰다. 남은 비용은 생성 코드가 아니라 런타임과 표준 라이브러리에 있기 쉽다. int 하나에 snprintf와 malloc을 쓰는 toString, 비교마다 함수 포인터를 부르는 qsort, 리스트를 거치는 배열 함수였다. 가장 흔한 경우의 빠른 경로를 앞에 두고 나머지는 그대로 둔다
---

# 언어 기능마다 재서 런타임의 빠른 경로 찾기

> 관련 커밋: [88fb581](https://github.com/ohama/calc-mlir/commit/88fb581) (v0.61.0)

0.60.0까지 컴파일러의 속도 작업은 `benchmarks/`의 다섯 예제(fib, queens, sieve, msort, bst)와 그 큰 입력을 보고 생성 코드를 고친 것이었다. 다섯 예제는 C와 비슷한 수준까지 왔다. 그런데 이 예제들은 문자열, 출력, `sort`, 배열 라이브러리 함수를 거의 쓰지 않는다. 언어 기능마다 프로그램을 만들어 재 보니 큰 비용 세 개가 런타임과 prelude에 있었다.

| 프로그램 (실행 파일) | 0.60.0 | 0.61.0 | 원인 |
|---|---|---|---|
| 3백만 개 `Array.init` → `map` → `fold` | 0.14초, 238MB | 0.01초, 48MB | prelude가 리스트를 거침 |
| 같은 프로그램, 인터프리터 | 1.39초 | 0.72초 | prelude를 같이 씀 |
| `toString i` 2백만 번 | 0.09초 | 0.02초 | int 하나에도 `snprintf`, `malloc`/`free` |
| `println i` 3백만 번 | 0.25초 | 0.09초 | 같은 원인. C의 `printf`는 0.13초 |
| int 2백만 개 `sort` | 0.18초 | 0.12초 | `qsort`가 비교마다 `calc_compare`를 부름 |

컴파일러 코드는 한 줄도 바꾸지 않았다.

## The Insight

**벤치마크가 고르는 것만 빨라진다.** 같은 벤치마크로 여러 번 고치면, 그 벤치마크가 쓰지 않는 기능의 비용은 처음 그대로 남는다. 생성 코드는 여러 번 다듬었지만, 런타임의 C 함수는 "맞게 동작하는 첫 버전"이었다.

- `toString`은 모든 값을 같은 길로 처리했다. 버퍼를 `malloc`하고, 값의 종류로 갈라지고, int면 `snprintf`하고, 문자열로 복사한 뒤 `free`했다.
- `sort`는 `qsort(items, n, 8, compare_for_sort)`였다. 비교마다 함수 포인터 호출 두 번(`compare_for_sort` → `calc_compare`)이다.
- prelude의 `Array.map f a`는 `Array.ofList (map f (Array.toList a))`였다. 원소마다 리스트 칸 세 개를 만든다(toList, map, map 안의 reverse).

일반적인 길은 그대로 두고, **가장 흔한 경우 하나의 빠른 경로**를 앞에 두면 된다. 결과가 같아야 하므로 일반적인 길이 정답이고, 빠른 경로는 그 부분집합이다.

## Why This Matters

- **사용자 프로그램은 벤치마크와 다르게 생겼다.** 결과를 출력하고, 문자열을 만들고, 정렬하고, 라이브러리 함수를 쓴다. `println`이 C의 `printf`보다 두 배 느리면, 계산이 아무리 빨라도 출력이 많은 프로그램은 느리다.
- **"문제없음"도 결과다.** 같은 조사에서 `try`(5백만 번에 0.04초), 다형 함수 안의 비교, 문자열 `=`, option, 레코드 갱신은 병목이 아니었다. 재지 않았으면 이쪽을 먼저 고쳤을 수 있다.
- **짐작으로는 순서를 못 정한다.** 문자열 `+` 연쇄를 한 번에 할당하는 것은 컴파일러를 고쳐야 하는 큰 일인데 C 모델에서 22%였다. `toString`의 빠른 경로는 런타임 10줄인데 4.5배였다.

## Recognition Pattern

- 생성 코드를 고칠 때마다 같은 몇 개의 벤치마크만 잴 때
- 런타임 함수가 모든 타입을 하나의 길로 처리할 때 (`value` 하나를 받아 종류로 갈라지는 C 함수)
- 표준 라이브러리 함수가 다른 자료 구조를 거쳐서 구현되어 있을 때 (`ofList (map f (toList a))`)
- `sample`의 위쪽에 `__vfprintf`, `_xzm_free`, `_qsort` 같은 libc 함수가 보일 때

## The Approach

### Step 1: 언어 기능마다 0.1~0.3초짜리 프로그램을 만든다

기능 하나만 쓰고, 시작 비용보다 충분히 길게 만든다. 같은 일을 다른 방법으로 하는 기준 프로그램을 짝으로 둔다.

| 기능 | 프로그램 | 기준 |
|---|---|---|
| 배열 라이브러리 | `Array.init n (fun i -> i)` → `map` → `fold` | 같은 일을 `for`와 `a.[i]`로 |
| 문자열 만들기 | `sprintf "%d-%d:%s" i (i * 2) "x"` | `toString i`만 |
| 출력 | `println i` | 같은 C 프로그램의 `printf` |
| 정렬 | `sort` int 2백만 개 | 리스트를 만드는 데까지만 |
| 예외 | `try check x with Bad _ -> 0` (7번에 한 번 `raise`) | `try` 없이 |
| 다형 비교 | 표기 없는 insertion sort | `(x: int)`로 표기한 것 |
| 튜플 | `go (i - 1) (b, a + b)` | `go (i - 1) b (a + b)` |

짝과의 차이가 그 기능의 비용이다. 큰 것만 `sample`로 본다.

```bash
calc --compile=big_str big_str.calc
./big_str > /dev/null & pid=$!; sleep 0.5; sample $pid 2 -f prof.txt; wait $pid
awk '/Sort by top of stack/,0' prof.txt | sed -n 2,14p
```

```
__vfprintf  (in libsystem_c.dylib)        237
_platform_memmove  (in libsystem_platform.dylib)        233
_xzm_free  (in libsystem_malloc.dylib)        107
GC_malloc_kind  (in libgc.1.5.6.dylib)        102
```

### Step 2: 고치기 전에 시험 구현으로 효과를 잰다

시험 구현은 빨리 만들고, 재고, `git diff > prototype.patch`로 저장한 뒤 되돌린다. 효과가 큰 것만 다시 적용해서 테스트와 문서를 붙인다. 컴파일러를 고쳐야 하는 것(문자열 연쇄)은 C 모델로 잰다.

### Step 3: 가장 흔한 경우의 빠른 경로를 앞에 둔다

```c
/* 뒤에서부터 씀. Int32.MinValue 는 부호 없는 수로 바꿔서 */
char *calc_format_int(int32_t n, char *end) {
    uint32_t u = n < 0 ? 0u - (uint32_t)n : (uint32_t)n;
    char *p = end;
    do { *--p = (char)('0' + u % 10); u /= 10; } while (u != 0);
    if (n < 0) *--p = '-';
    return p;
}

value calc_builtin_toString(value v) {
    if (CALC_IS_KIND(v, CALC_KIND_STRING)) return v;
    if (CALC_IS_INT(v)) { /* 가장 흔한 경우: 버퍼 없이 바로 문자열로 */
        ...
    }
    calc_buffer b = {0};          /* 나머지는 전과 같은 길 */
    calc_buffer_value(&b, v);
    return from_buffer(&b);
}
```

- **같은 함수를 일반적인 길에서도 쓴다.** `calc_buffer_value`의 int 경우도 `calc_format_int`를 쓰면, 리스트 안의 int처럼 빠른 경로에 들지 않는 경우도 빨라진다. 출력 형식이 두 곳에서 갈라지지 않는다.
- **값 표현의 성질을 쓴다.** tagged int `(n << 3) | 1`은 태그가 모두 같으므로 값 그대로 비교해도 int의 순서와 같다. `sort`는 원소를 배열로 옮기면서 모두 int인지 보고, 그렇다면 `calc_compare` 없이 merge sort한다.

```c
for (int64_t i = 0; i < n; i++, xs = TAIL(xs)) { items[i] = HEAD(xs); all_int &= CALC_IS_INT(items[i]); }
if (all_int) sort_ints(items, n);
else qsort(items, (size_t)n, sizeof(value), compare_for_sort);
```

- **stable한 것을 고른다.** int는 같은 값끼리 구분되지 않아서 상관없지만, 일반적인 길과 같은 성질이면 나중에 경우를 넓힐 때 안전하다.

### Step 4: 라이브러리 함수는 자료 구조를 거치지 않고 쓴다

```
// 전: 원소마다 리스트 칸 세 개
let map f a = Array.ofList (map f (Array.toList a))

// 후: f 의 첫 결과로 만든 뒤 채움. f 를 부르는 순서(앞 원소부터)는 같음
let map f a =
    let n = Array.length a
    if n = 0 then Array.ofList []
    else
        let b = Array.create n (f a.[0])
        for i = 1 to n - 1 do
            b.[i] <- f a.[i]
        b
```

- **첫 결과로 만드는 이유:** `Array.create`가 원소 값을 보고 표현을 정한다(bool이면 바이트 배열). 빈 값을 미리 넣을 수 없는 언어에서도 쓸 수 있다.
- **바뀌면 안 되는 것:** `f`를 부르는 순서와 횟수, `f`가 `raise`할 때의 결과, 빈 배열과 음수 길이. 이것을 테스트한다.
- **prelude는 인터프리터도 쓴다.** 두 실행기가 같이 빨라졌다(인터프리터 1.39초 → 0.72초).

### Step 5: 잴 때 새 런타임이 들어갔는지 확인한다

이 프로젝트는 런타임 C 소스를 빌드 출력(`bin/Release/net10.0/runtime/`)에 복사하고, `calc --compile`이 그것을 컴파일한다. 소스만 고치고 Release를 다시 빌드하지 않으면 옛 런타임으로 링크된다. 한 번은 앞의 `cd`가 실패해 `&&`로 이은 빌드가 건너뛰어졌고, "바꾼 뒤"가 전과 같은 시간으로 나왔다.

```bash
nm f_arr | grep -c calc_format_int          # 0 이면 옛 런타임
grep -c calc_format_int src/Calc/bin/Release/net10.0/runtime/runtime.c
```

효과가 0으로 나오면 측정 대상부터 의심한다.

## Example

결과가 같아야 하므로 기대값은 바꾸기 전 인터프리터의 출력이다. 빠른 경로의 경계를 테스트한다.

| 경우 | 프로그램 |
|---|---|
| int의 끝값 | `toString (-2147483647 - 1)`, `print 2147483647`, `toString 0` |
| 빠른 경로에 들지 않는 값 | `toString [1; -2]`, `toString (Some (-3))`, `toString ()` |
| `sort` | `sort []`, `sort [5]`, 같은 값과 끝값, 문자열, 10만 개가 정렬되었는지 |
| `Array` 함수 | `Array.init 0 f`, `Array.init (-3) f`, `f`에서 `print`(순서), `f`에서 `failwith`, 30만 개를 수거한 뒤 |

## 체크리스트

- [ ] 벤치마크 몇 개가 아니라 언어 기능마다 짝이 있는 프로그램을 만들었는가?
- [ ] 병목이 아닌 기능도 기록했는가? (다음에 다시 재지 않도록)
- [ ] 시험 구현이나 C 모델로 효과를 재고 순서를 정했는가?
- [ ] 빠른 경로가 일반적인 길의 부분집합이고, 일반적인 길은 그대로인가?
- [ ] 라이브러리 함수를 바꿀 때 부르는 순서, 횟수, 예외, 빈 입력이 같은가?
- [ ] 잰 실행 파일에 새 런타임이 들어갔는지 확인했는가?

## 관련 문서

- `84-encode-constructor-identity-in-the-header.md` - 큰 표현 변경 전에 C 모델로 재기
- `85-store-bool-arrays-as-bytes.md` - 원소 값을 보고 배열의 표현 정하기
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
