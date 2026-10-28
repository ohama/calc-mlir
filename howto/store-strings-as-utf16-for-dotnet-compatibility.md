---
created: 2026-10-28
description: .NET 인터프리터와 같은 결과를 내야 하는 네이티브 런타임은 문자열을 UTF-16 code unit으로 저장한다. ordinal 비교 순서가 UTF-8과 다르기 때문이다. 출력할 때만 UTF-8로 바꾸되 surrogate pair가 갈라지지 않게 구간째 바꾸고, lone surrogate는 .NET처럼 U+FFFD로 쓴다
---

# .NET과 같은 결과를 내도록 문자열을 UTF-16으로 저장하기

C 런타임에서는 문자열을 UTF-8 바이트로 다루는 것이 자연스럽다. 그런데 기준 구현이 .NET 인터프리터라면 **UTF-8로 저장하는 순간 결과가 달라지는 연산**이 생긴다. 그래서 저장은 .NET `string`과 같은 UTF-16 code unit으로 하고, 바깥으로 내보낼 때(출력)만 UTF-8로 바꾼다.

## The Insight

**문자열 비교 순서는 인코딩마다 다르다.** 인터프리터의 `<`는 `String.CompareOrdinal`이고, 이 함수는 UTF-16 code unit을 차례로 비교한다. UTF-8 바이트 순서(= code point 순서)와는 BMP 밖 문자에서 갈린다(직접 확인함).

| 비교 `"😀" < "￿"` | 결과 |
|---|---|
| UTF-16 code unit (`D83D DE00` vs `FFFF`) — .NET ordinal | `true` |
| UTF-8 바이트 (`F0 9F 98 80` vs `EF BF BF`) — C `memcmp` | `false` |
| code point (U+1F600 vs U+FFFF) | `false` |

길이도 마찬가지다. `.Length`는 code unit 수이므로 `"😀"`는 2다. UTF-8로 저장하면 길이와 인덱스(`substring`)마다 변환이 필요해진다.

## Why This Matters

- **UTF-8로 저장한 경우:** differential testing에서 이모지나 한자 확장 영역이 섞인 정렬이 인터프리터와 다르게 나옵니다. 평범한 ASCII 예제만 있으면 이 차이를 놓칩니다.
- **출력할 때 code unit을 하나씩 바꾼 경우:** surrogate pair의 두 반쪽이 따로 U+FFFD가 됩니다. `"😀"`가 `��`로 출력됩니다. 이 프로젝트에서도 처음에 이렇게 짰다가, 테스트하기 전에 발견해서 고쳤습니다.
- **lone surrogate를 그대로 출력한 경우:** 잘못된 UTF-8이 나옵니다. .NET은 이 경우 U+FFFD를 씁니다.

## Recognition Pattern

- 관리형 언어(.NET, JVM, JavaScript — 모두 UTF-16) 구현과 네이티브 구현이 같은 결과를 내야 할 때
- 문자열 비교, 길이, 인덱스가 언어 의미에 들어 있을 때
- C 런타임이 문자열을 출력할 때

## The Approach

### Step 1: 기준 구현의 단위를 따른다

```c
typedef struct {
    int64_t header;
    int64_t length;      /* UTF-16 코드 단위 수 = .NET string.Length */
    uint16_t units[];
} calc_string;
```

컴파일러는 F# `string`의 `ToCharArray()`를 그대로 내보낸다. 이미 UTF-16이라 변환할 것이 없다.

### Step 2: 비교는 code unit을 부호 없는 수로

```c
/* String.CompareOrdinal 과 같음 */
int64_t n = x->length < y->length ? x->length : y->length;
for (int64_t i = 0; i < n; i++)
    if (x->units[i] != y->units[i]) return x->units[i] < y->units[i] ? -1 : 1;
return x->length < y->length ? -1 : (x->length > y->length ? 1 : 0);
```

`uint16_t`라서 `0xFFFF`가 음수가 되지 않는다. 같음 비교는 길이와 `memcmp`로 충분하다.

### Step 3: 출력할 때 구간째 UTF-8로 바꾼다

이스케이프할 문자(`\\ \" \n \t \r`) 사이의 구간을 통째로 변환 함수에 넘긴다. 변환 함수는 surrogate pair를 한 code point로 묶는다.

```c
static void put_utf16(FILE *out, const uint16_t *units, int64_t length) {
    for (int64_t i = 0; i < length; i++) {
        uint32_t u = units[i];
        if (u >= 0xD800 && u <= 0xDBFF && i + 1 < length && units[i + 1] >= 0xDC00 && units[i + 1] <= 0xDFFF) {
            put_utf8(out, 0x10000 + ((u - 0xD800) << 10) + (units[i + 1] - 0xDC00));
            i++;
        } else if (u >= 0xD800 && u <= 0xDFFF) {
            put_utf8(out, 0xFFFD);           /* 짝 없는 대리 코드 단위 */
        } else {
            put_utf8(out, u);
        }
    }
}
```

### Step 4: .NET의 실제 출력을 확인한다

lone surrogate를 .NET이 어떻게 출력하는지는 추측하지 말고 잰다.

```fsharp
// lone.fsx
let s = "a" + string (char 0xD800) + "b"
stdout.Write(s)
```

```
$ dotnet fsi lone.fsx | xxd
00000000: 61ef bfbd 62     a...b       ← EF BF BD = U+FFFD
```

### Step 5: 내용에 포인터가 없는 블록은 ATOMIC으로 할당한다

`+`로 만든 문자열 블록에는 포인터가 없다. `GC_MALLOC_ATOMIC`으로 할당하면 Boehm GC가 블록 내용을 훑지 않는다. 그러면 표시 단계가 빨라지고, code unit 데이터가 우연히 주소처럼 보여서 다른 블록을 붙잡는 일(false retention)도 생기지 않는다. 대신 런타임이 헤더와 길이를 직접 채워야 한다. ATOMIC 메모리는 0으로 초기화되지 않는다.

```c
calc_string *s = GC_MALLOC_ATOMIC(sizeof(calc_string) + (size_t)length * sizeof(uint16_t));
s->header = CALC_KIND_STRING | ((1 + (2 * length + 7) / 8) << 8);
s->length = length;
```

## Example

```c
// ❌ BAD: 코드 단위를 하나씩 변환 → 대리 쌍이 각각 U+FFFD 가 됨
for (i = 0; i < s->length; i++) {
    if (is_escape(s->units[i])) put_escape(...);
    else put_utf16(out, &s->units[i], 1);
}

// ✅ GOOD: 이스케이프 사이 구간을 통째로
for (i = 0; i < s->length; i++) {
    if (escape_of(s->units[i])) { put_utf16(out, &s->units[start], i - start); fputs(...); start = i + 1; }
}
put_utf16(out, &s->units[start], s->length - start);
```

테스트에 BMP 밖 문자를 꼭 넣는다.

```fsharp
Expect.equal (compileAndRun "\"한글 😀\"") ("\"한글 😀\"\n", "", 0) ""
Expect.equal (compileAndRun "\"😀\" < \"￿\"") ("true\n", "", 0) "UTF-16 순서"
```

## 체크리스트

- [ ] 저장 단위가 기준 구현과 같은가? (.NET이면 UTF-16)
- [ ] 비교가 부호 없는 code unit 순서인가?
- [ ] 출력 변환이 surrogate pair를 묶는가? (하나씩 변환하지 않음)
- [ ] lone surrogate의 출력이 기준 구현과 같은가?
- [ ] 포인터 없는 블록을 ATOMIC으로 할당하고 헤더를 직접 채우는가?
- [ ] 테스트에 BMP 밖 문자가 있는가?

## 관련 문서

- `use-ordinal-string-apis-for-language-builtins.md` - 인터프리터 쪽: ordinal 비교를 명시한 이유
- `emit-string-constants-as-aligned-llvm-globals.md` - 문자열 상수 블록
