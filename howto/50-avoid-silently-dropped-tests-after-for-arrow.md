---
order: 50
created: 2026-10-28
commit: 92e9d88
version: 0.28.0
description: F# 리스트 식에서 for ... -> 뒤에 쓴 항목은 리스트에 들어가지 않고 경고 FS3221만 남긴 채 버려진다. Expecto testList 안이라면 그 테스트는 실행되지 않고 실패도 하지 않는다. for ... do yield와 yield를 명시하고, 빌드 경고와 테스트 수를 확인한다
---

# `for … ->` 뒤의 테스트가 조용히 버려지는 문제 피하기

> 관련 커밋: [92e9d88](https://github.com/ohama/calc-mlir/commit/92e9d88) (v0.28.0)

Expecto 테스트 목록은 F# 리스트 식으로 만든다. 표에서 테스트를 만드는 `for … ->`와 손으로 쓴 `test`를 한 목록에 섞으면, **뒤의 테스트가 목록에 들어가지 않는다.** 실패도, 오류도 없다. 빌드 경고 하나만 남는다.

```fsharp
testList "쓰이지 않는 분기" [
    for input, expected in table ->
        test input { ... }
    test "가드가 있는 앞 분기는 덮는 데 세지 않음" { ... }      // ← 목록에 없음. 실행되지 않음
]
```

이 프로젝트에서도 이렇게 쓴 테스트 3개가 실행되지 않았다. 처음에는 "10개 추가, 총 809개"로 보고했는데, 고친 뒤에는 13개, 812개가 되었다. Release 빌드의 경고로 알게 되었다.

```
warning FS3221: This expression returns a value of type 'Test' but is implicitly discarded.
```

## The Insight

**`for … -> e`는 `for … do yield e`의 줄임말이다.** 리스트 식 안에 `yield`가 나오면, 그 뒤의 식은 자동으로 목록에 들어가지 않고 "결과를 버리는 문장"이 된다. F#의 암묵적 yield는 리스트 식 안에 `yield`가 없을 때만 동작하기 때문이다. 작은 스크립트로 재현했다.

```fsharp
[ for x in [ 1; 2 ] -> x * 10
  99 ]                                   // → [10; 20]       (99 는 버려짐, FS3221)

[ for x in [ 1; 2 ] do yield x * 10
  yield 99 ]                             // → [10; 20; 99]

[ 10
  20
  99 ]                                   // → [10; 20; 99]  (yield 가 없으면 암묵적 yield)
```

Expecto는 목록에 있는 테스트만 돌린다. 버려진 테스트는 "통과"도 "실패"도 아니고, 그냥 없다.

## Why This Matters

- **조용히 빠짐:** 깨진 코드를 지키려고 쓴 테스트가 실행되지 않습니다. 테스트 결과는 녹색이라 아무도 모릅니다.
- **잘못된 보고:** 테스트 수가 예상보다 적은데도 "모두 통과"로 보고하게 됩니다.
- **경고를 놓침:** Debug 빌드를 증분으로 하면 경고가 다시 출력되지 않을 수 있습니다. 이 프로젝트에서도 Release 빌드를 처음부터 다시 할 때 보였습니다.

## Recognition Pattern

- 표(데이터)로 테스트를 만들고 같은 목록에 손으로 쓴 테스트를 더할 때
- 빌드 출력에 `FS3221`이 있을 때
- 테스트를 더했는데 전체 테스트 수가 기대만큼 늘지 않을 때

## The Approach

### Step 1: 섞을 때는 `yield`를 모두 명시한다

```fsharp
testList "쓰이지 않는 분기" [
    for input, expected in table do
        yield test input { ... }
    yield test "가드가 있는 앞 분기는 덮는 데 세지 않음" { ... }
    yield test "빠진 분기와 쓰이지 않는 분기가 함께" { ... }
]
```

또는 표에서 만드는 테스트를 별도 `testList`로 감싸서 목록의 한 항목으로 만든다.

```fsharp
testList "쓰이지 않는 분기" [
    testList "표" [ for input, expected in table -> test input { ... } ]
    test "가드가 있는 앞 분기는 덮는 데 세지 않음" { ... }
]
```

### Step 2: 경고를 확인한다

테스트를 더한 뒤에는 처음부터 다시 빌드해서 경고를 본다.

```bash
dotnet build -c Release --no-incremental 2>&1 | grep -E "warning FS3221"
```

경고를 오류로 바꾸면 다시는 놓치지 않는다. 테스트 프로젝트의 fsproj에 다음을 넣으면 된다.

```xml
<WarningsAsErrors>FS3221</WarningsAsErrors>
```

### Step 3: 테스트 수를 센다

더한 테스트 수만큼 전체 수가 늘었는지 확인한다. 보고하기 전에 "몇 개 추가, 총 몇 개"를 실제 실행 결과에서 읽는다.

```
Passed!  - Failed: 0, Passed: 812, Skipped: 0, Total: 812
```

## Example

```fsharp
// ❌ 뒤의 test 두 개가 버려짐 (FS3221)
[ for input, expected in table ->
      test input { Expect.equal (warnings input) expected "" }
  test "가드" { ... }
  test "함께" { ... } ]

// ✅ yield 를 명시
[ for input, expected in table do
      yield test input { Expect.equal (warnings input) expected "" }
  yield test "가드" { ... }
  yield test "함께" { ... } ]
```

## 체크리스트

- [ ] 같은 목록에 `for … ->`와 다른 항목을 섞지 않았는가? 섞었다면 `yield`를 모두 명시했는가?
- [ ] `dotnet build --no-incremental` 출력에 `FS3221`이 없는가?
- [ ] 더한 테스트 수만큼 전체 테스트 수가 늘었는가?
- [ ] (선택) 테스트 프로젝트에서 `FS3221`을 오류로 다루는가?

## 관련 문서

- `04-setup-expecto-with-dotnet-test.md` - Expecto를 `dotnet test`로 돌리기 (테스트가 0개여도 성공하는 다른 함정)
- `37-batch-compiled-test-programs-into-one-executable.md` - 표(데이터)로 바꾼 실행 테스트
