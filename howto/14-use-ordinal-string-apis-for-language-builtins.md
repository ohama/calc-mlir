---
order: 14
created: 2026-10-28
commit: ede58c2
version: 0.9.0
description: .NET 문자열 API 중 일부는 기본이 현재 문화권 비교다. 언어의 내장 함수는 실행 환경과 상관없이 같은 결과를 내야 하므로 Ordinal과 Invariant를 명시한다
---

# 언어 내장 함수에는 Ordinal 문자열 API를 쓰기

> 관련 커밋: [ede58c2](https://github.com/ohama/calc-mlir/commit/ede58c2) (v0.9.0)

`s.IndexOf(sub)`, `s.StartsWith(prefix)`, `s.ToUpper()`, `String.Compare(a, b)`는 **현재 문화권(CurrentCulture)** 규칙을 쓴다. 이것으로 언어의 내장 함수를 만들면 같은 프로그램이 컴퓨터의 언어 설정에 따라 다른 결과를 낸다. 언어 semantics는 실행 환경과 무관해야 하므로 `StringComparison.Ordinal`과 `ToUpperInvariant`를 명시한다.

## The Insight

.NET 문자열 API는 메서드마다 기본 비교 방식이 다르다. 이름만 보고는 알 수 없다.

| API | 기본 비교 |
|---|---|
| `Contains(string)`, `Replace(string, string)`, `Equals`, `=` | ordinal |
| `IndexOf(string)`, `StartsWith(string)`, `EndsWith(string)` | **현재 문화권** |
| `String.Compare(a, b)`, `CompareTo` | **현재 문화권** |
| `ToUpper()`, `ToLower()` | **현재 문화권** |

문화권 비교는 "사람이 보기에 같은 문자열인가"를 따진다. 언어 내장 함수가 보장해야 할 것은 "문자 코드가 같은가"다. 둘은 생각보다 자주 다르다.

## Why This Matters

이 프로젝트 환경(.NET 10, macOS, 문화권 `en-US`)에서 `dotnet fsi`로 실행한 결과다.

| 식 | 문화권 기본 | Ordinal/Invariant |
|---|---|---|
| `"abc".IndexOf("\u0000")` | `0` (무시 가능한 문자라 빈 문자열처럼 일치) | `-1` |
| `"é".StartsWith("é")` (합성 문자 vs 분해 문자) | `true` | `false` |
| `String.Compare("é", "é")` | `0` (같음) | — |
| `"é" = "é"` | `false` | `false` |
| `String.Compare("a", "B")` | `-1` (a가 앞) | `CompareOrdinal`: `31` (B가 앞) |
| 문화권 `tr-TR`에서 `"i".ToUpper()` | `"İ"` (점 있는 대문자 I) | `ToUpperInvariant`: `"I"` |

문제가 되는 상황은 이렇다.

- **`=`와 `<`가 서로 모순된다.** 문화권 비교로 `<`를 만들면 `"é" = "é"`는 `false`인데, 둘 다 `<`도 `>`도 아니게 된다(`Compare` 결과 0). 정렬과 중복 제거가 깨진다.
- **`startsWith`가 `true`인데 `substr`로 잘라 보면 다르다.** `startsWith s p`가 참이면 `substr s 0 (length p) = p`여야 한다는 기대가 깨진다.
- **같은 테스트가 CI 서버(다른 문화권)에서만 실패한다.** `tr-TR`의 `ToUpper`가 대표적이다.

## Recognition Pattern

- 인터프리터나 컴파일러에 문자열 내장 함수를 추가할 때
- `IndexOf`, `StartsWith`, `EndsWith`, `Compare`, `ToUpper`, `ToLower`를 인자 없이 호출하는 코드를 쓸 때
- 정렬, 비교, 검색 결과가 "기계마다 다르다"는 보고를 받았을 때

## The Approach

**언어 semantics = 문자 코드.** 사람용 정렬이나 대소문자 무시가 필요하면 그건 별도 함수로, 이름에 드러나게 추가한다.

### Step 1: 비교 방식을 받는 오버로드를 항상 쓴다

```fsharp
let ordinal = System.StringComparison.Ordinal

| "contains", [ s; sub ] -> VBool ((str s).Contains(str sub, ordinal))
| "startsWith", [ s; prefix ] -> VBool ((str s).StartsWith(str prefix, ordinal))
| "endsWith", [ s; suffix ] -> VBool ((str s).EndsWith(str suffix, ordinal))
| "indexOf", [ s; sub ] -> VInt ((str s).IndexOf(str sub, ordinal))
| "replace", [ s; o; n ] -> VString ((str s).Replace(str o, str n, ordinal))
```

`Contains`와 `Replace`는 기본이 이미 ordinal이다. 그래도 명시한다. 읽는 사람이 메서드별 기본값을 외울 필요가 없다.

### Step 2: 대소문자 변환은 Invariant를 쓴다

```fsharp
| "upper", [ s ] -> VString ((str s).ToUpperInvariant())
| "lower", [ s ] -> VString ((str s).ToLowerInvariant())
```

### Step 3: 순서 비교 연산자도 ordinal로 맞춘다

```fsharp
/// < > <= >= 는 int 끼리 또는 string 끼리 (문자 코드 순서) 비교
| VString x -> System.String.CompareOrdinal(x, evalString env b)
```

`=`(ordinal)와 `<`(ordinal)가 같은 기준을 써야 "같지도 않고 크지도 작지도 않은" 쌍이 생기지 않는다. 결과로 `"B" < "a"`가 `true`가 된다. 이것을 문서에 적는다.

### Step 4: 숫자 변환도 문화권을 고정한다

```fsharp
System.Int32.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
```

`NumberStyles`를 명시하면 공백, 천 단위 구분자 허용 여부도 함께 정해진다.

### Step 5: 문서에 의미를 적는다

- 문자열 위치와 길이는 UTF-16 code unit이다 (`length "😀"`는 2)
- 비교는 문자 코드 순서다 (`"B" < "a"`)
- 합성/분해 문자는 다른 문자열이다

## Example

```fsharp
// ❌ 문화권에 따라 결과가 바뀜
| "indexOf", [ s; sub ] -> VInt ((str s).IndexOf(str sub))
| "upper", [ s ] -> VString ((str s).ToUpper())

// ✅ 실행 환경과 상관없이 같은 결과
| "indexOf", [ s; sub ] -> VInt ((str s).IndexOf(str sub, System.StringComparison.Ordinal))
| "upper", [ s ] -> VString ((str s).ToUpperInvariant())
```

언어에서 확인한 결과다.

```
"é" = "é"            (첫째는 U+00E9, 둘째는 e + U+0301)   → false
startsWith "é" "é"   (같은 조합)                          → false
"B" < "a"                                               → true
```

## 체크리스트

- [ ] `IndexOf`/`StartsWith`/`EndsWith`에 `StringComparison.Ordinal`을 넘긴다
- [ ] `ToUpper`/`ToLower` 대신 `ToUpperInvariant`/`ToLowerInvariant`를 쓴다
- [ ] `<` 등 순서 비교는 `String.CompareOrdinal`로, `=`와 같은 기준이다
- [ ] 숫자 파싱에 `CultureInfo.InvariantCulture`와 `NumberStyles`를 명시한다
- [ ] 문자 코드 기준 비교와 UTF-16 길이를 언어 문서에 적었다

## 관련 문서

- `13-implement-builtins-as-data-values.md` - 내장 함수를 값으로 표현하는 구조
- `11-lex-string-literals-with-fslex.md` - 문자열 리터럴 렉싱과 출력 형태
