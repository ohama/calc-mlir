---
created: 2026-10-28
description: C 런타임의 대소문자 변환과 공백 판정을 .NET과 똑같이 맞추려면, libc에 기대지 말고 .NET에서 표를 생성해 C 헤더로 넣는다. 생성기는 모든 코드 포인트를 돌고, 테스트가 표와 지금 .NET을 다시 비교해서 .NET을 올렸을 때 어긋남을 잡는다
---

# .NET에서 Unicode 표를 생성해 C 런타임에 넣기

인터프리터의 `upper "é"`는 .NET의 `ToUpperInvariant`를 쓴다. 컴파일한 프로그램의 C 런타임도 같은 결과를 내야 한다. C의 `towupper`는 **로캘에 따라 결과가 다르고**, 기본 `"C"` 로캘에서는 ASCII 밖을 아예 바꾸지 않는다(직접 확인함).

| 코드 포인트 | `towupper` ("C" 로캘) | `towupper` (en_US.UTF-8) | .NET `ToUpperInvariant` |
|---|---|---|---|
| U+00E9 é | 그대로 | É | É |
| U+03C3 σ | 그대로 | Σ | Σ |
| U+01C5 ǅ | 그대로 | Ǆ | Ǆ |
| U+10428 𐐨 | 그대로 | 𐐀 | 𐐀 |

로캘을 설정하면 이 문자들은 맞는다. 하지만 사용자 기기의 로캘 설정, libc 종류(macOS, glibc, musl), Unicode 버전에 따라 결과가 달라진다. 기준이 .NET이라면 **표를 .NET에서 만들어 C에 박는 것**이 가장 확실하다.

## The Insight

**"같은 결과"가 기준이면, 기준 구현에서 표를 뽑아내고 그 표를 기준 구현과 다시 비교하는 테스트를 둔다.**

- **생성기:** `dotnet fsi`로 0부터 0x10FFFF까지 모든 코드 포인트를 `ToUpperInvariant`/`ToLowerInvariant`에 넣습니다. 결과가 달라지는 것만 `(원래, 바뀐)` 쌍으로 적습니다. 대문자 변환 1476개(BMP 밖 282개), 소문자 변환 1459개입니다.
- **가정 확인:** 생성기는 "변환해도 길이가 바뀌지 않는다"를 확인하고, 아니면 실패합니다. .NET invariant 변환은 한 코드 포인트를 한 코드 포인트로 바꿉니다. `ß`는 `SS`가 되지 않고 그대로이고, `İ`의 소문자도 그대로입니다.
- **테스트:** 헤더를 읽어 같은 계산을 다시 하고, 모든 코드 포인트에서 비교합니다. .NET을 올려서 Unicode 버전이 바뀌면 이 테스트가 실패합니다.

## Why This Matters

- **ASCII만 처리한 경우:** `upper "é"`가 인터프리터에서는 `"É"`, 컴파일한 실행 파일에서는 `"é"`가 됩니다. 테스트에 ASCII만 있으면 모릅니다.
- **`towupper`와 `setlocale`을 쓴 경우:** 개발 기기에서는 맞아도, CI나 사용자 기기에서 로캘이 없으면 조용히 ASCII 동작으로 돌아갑니다.
- **표를 손으로 만든 경우:** 빠지는 문자가 생기고, .NET 버전이 바뀌어도 알 수 없습니다.
- **추측으로 기대값을 적은 경우:** 이 프로젝트에서도 `lower "İ"`를 `"i̇"`로 짐작해 테스트에 적었다가 틀렸습니다. .NET invariant는 `İ`를 그대로 둡니다.

## Recognition Pattern

- 관리형 언어(.NET, JVM)의 문자열 함수와 같은 결과를 내는 C/네이티브 런타임을 만들 때
- `upper`, `lower`, `trim`, 문자 분류(`IsWhiteSpace`, `IsLetter`)처럼 Unicode 데이터가 필요한 함수
- 결과가 로캘, 플랫폼, 라이브러리 버전에 따라 달라질 수 있는 함수

## The Approach

### Step 1: 기준 구현의 동작부터 잰다

생성하기 전에 가정을 확인한다.

- **길이:** 변환 결과의 길이가 바뀌는 코드 포인트가 있는가? (0개였습니다)
- **문맥:** 문자열 단위 변환이 코드 포인트 단위 변환과 같은가? invariant에는 문맥 규칙이 없습니다.
- **짝 없는 대리 코드 단위:** 그대로 둡니다.
- **공백:** `Char.IsWhiteSpace`는 BMP 안의 25개 문자이고, `String.Trim()`이 이 목록을 씁니다.

### Step 2: 생성기는 저장소에, 결과 헤더도 저장소에

```fsharp
// src/Calc/runtime/gen-unicode.fsx
let mapping (convert: string -> string) =
    [ for cp in 0 .. 0x10FFFF do
          if cp < 0xD800 || cp > 0xDFFF then
              let s = Char.ConvertFromUtf32 cp
              let t = convert s
              if t.Length <> s.Length then failwithf "length changes: U+%04X" cp
              if t <> s then yield cp, Char.ConvertToUtf32(t, 0) ]
```

결과 `unicode.h`도 저장소에 넣는다. 빌드할 때마다 .NET을 돌리지 않아도 되고, 표가 바뀌면 diff로 보인다. 헤더 첫 줄에 "생성한 파일, 직접 고치지 말 것"과 생성한 .NET 버전을 적는다.

```c
/* gen-unicode.fsx 가 만든 파일. 직접 고치지 말 것 */
/* .NET 10.0.7: ToUpperInvariant, ToLowerInvariant (코드 포인트마다, 길이 불변), Char.IsWhiteSpace */
static const uint32_t calc_upper_map[1476][2] = {
    {0x0061, 0x0041}, {0x0062, 0x0042}, ...
```

### Step 3: 런타임은 정렬된 표를 이진 탐색한다

대리 쌍은 코드 포인트로 묶어서 찾고, 결과를 다시 대리 쌍으로 쓴다. 길이가 바뀌지 않으므로 결과 문자열을 미리 같은 길이로 잡을 수 있다.

```c
static uint32_t lookup(const uint32_t (*map)[2], int64_t count, uint32_t cp) {
    int64_t lo = 0, hi = count - 1;
    while (lo <= hi) {
        int64_t mid = (lo + hi) / 2;
        if (map[mid][0] == cp) return map[mid][1];
        if (map[mid][0] < cp) lo = mid + 1; else hi = mid - 1;
    }
    return cp;    /* 표에 없으면 그대로 */
}
```

### Step 4: 테스트가 표와 지금 .NET을 비교한다

```fsharp
test "Unicode 표 (runtime/unicode.h) 가 지금 .NET 과 같음" {
    // 다르면: dotnet fsi src/Calc/runtime/gen-unicode.fsx
    let upper, lower = table "calc_upper_map", table "calc_lower_map"
    for cp in 0 .. 0x10FFFF do
        if cp < 0xD800 || cp > 0xDFFF then
            ...   // 표의 결과 = s.ToUpperInvariant(), s.ToLowerInvariant()
}
```

코드 포인트 110만 개를 도는 데 약 0.3초 걸린다. 실패 메시지에는 다시 만드는 명령을 적는다.

### Step 5: 차등 테스트에 ASCII 밖 문자를 넣는다

표가 맞아도 런타임 코드(대리 쌍 처리 등)가 틀릴 수 있다. 인터프리터와 비교하는 예제에 `é`, `σ`, `ǅ`, `ß`, `İ`, `𐐨`, 그리고 U+00A0, U+3000 같은 공백을 넣는다.

## Example

```
upper "abc é ß ǅ 𐐨"   → "ABC É ß Ǆ 𐐀"      (ß 그대로, 𐐨 은 BMP 밖)
lower "ÀÉ Σ İ"        → "àé σ İ"           (İ 그대로)
trim "  \t x y 　" → "x y"
```

인터프리터와 컴파일한 실행 파일 모두 같은 결과다.

## 체크리스트

- [ ] 기준 구현의 가정(길이 불변, 문맥 없음, 대리 코드 단위)을 확인했는가?
- [ ] 생성기가 모든 코드 포인트를 도는가? (BMP만이 아니라)
- [ ] 생성한 헤더에 "직접 고치지 말 것"과 기준 버전을 적었는가?
- [ ] 테스트가 표와 지금 기준 구현을 비교하고, 실패하면 다시 만드는 방법을 알려 주는가?
- [ ] 차등 테스트에 ASCII 밖 문자와 BMP 밖 문자가 있는가?

## 관련 문서

- `store-strings-as-utf16-for-dotnet-compatibility.md` - 문자열을 UTF-16으로 저장하는 이유
- `use-ordinal-string-apis-for-language-builtins.md` - 인터프리터 쪽: invariant/ordinal API를 고른 이유
