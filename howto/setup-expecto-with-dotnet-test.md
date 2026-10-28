---
created: 2026-10-27
description: Expecto 테스트 프로젝트를 dotnet test로도 돌리려면 GenerateProgramFile=false와 YoloDev 어댑터가 필요하다. 어댑터가 없으면 테스트 0개로 성공 처리된다
---

# Expecto 테스트 프로젝트를 `dotnet test`와 함께 쓰기

Expecto 테스트 프로젝트는 그 자체로 콘솔 앱이다. 여기에 `dotnet test` 지원을 붙이면 **entry point 충돌**과 **테스트를 하나도 찾지 못하는데도 성공하는 문제**가 생길 수 있다.

## The Insight

Expecto 프로젝트를 실행하는 방법은 두 가지이고, 쓰는 장치가 다르다.

| 실행 방법 | 동작 | 필요한 것 |
|---|---|---|
| `dotnet run --project tests/X` | 내가 쓴 `main`이 `runTestsInAssemblyWithCLIArgs`를 호출 | `Expecto` |
| `dotnet test` | VSTest가 testhost를 띄우고, **어댑터**가 테스트를 찾아 실행 | `Microsoft.NET.Test.Sdk` + `YoloDev.Expecto.TestSdk` |

두 방법을 함께 쓰려면 두 장치가 충돌하지 않게 맞춰야 한다.

1. `Microsoft.NET.Test.Sdk`는 테스트 프로젝트에 **entry point(main)를 자동으로 생성**한다. Expecto의 `main`과 겹치므로 자동 생성을 꺼야 한다.
2. VSTest는 어댑터가 없으면 Expecto 테스트를 알아보지 못한다. 이때 **실패가 아니라 "테스트 없음"으로 끝나고 종료 코드는 0**이다.

## Why This Matters

**증상 1: 빌드 오류 (FS0433)**

`GenerateProgramFile=false`를 빼면 다음 오류가 난다.

```
Main.fs(6,5): error FS0433: A function labeled with the 'EntryPointAttribute' attribute
must be the last declaration in the last file in the compilation sequence.
```

메시지만 보면 파일 순서 문제처럼 보인다. 실제 원인은 SDK가 몰래 추가한 entry point 파일이 내 `Main.fs` **뒤에** 컴파일되는 것이다.

**증상 2: CI가 거짓으로 성공 (더 위험함)**

`YoloDev.Expecto.TestSdk`를 빼고 `dotnet test`를 실행한 결과다.

```
No test is available in .../Calc.Tests.dll. Make sure that test discoverer & executors
are registered and platform & framework version settings are appropriate and try again.
```

```bash
$ dotnet test; echo "exit=$?"
exit=0
```

테스트를 **하나도** 실행하지 않았는데 종료 코드가 0이다. CI는 초록불이 된다.

## Recognition Pattern

- Expecto 프로젝트에 `Microsoft.NET.Test.Sdk`를 추가한 직후 FS0433 오류가 난다
- `dotnet test` 출력에 `Passed!` 줄이 없고 `No test is available`이 나온다
- `dotnet run`으로는 테스트가 도는데 `dotnet test`로는 0개다

## The Approach

### Step 1: 패키지 4개를 추가한다

```bash
cd tests/Calc.Tests
dotnet add package Expecto                    # 테스트 프레임워크
dotnet add package Expecto.FsCheck            # (선택) 속성 기반 테스트
dotnet add package Microsoft.NET.Test.Sdk     # dotnet test 호스트
dotnet add package YoloDev.Expecto.TestSdk    # dotnet test ↔ Expecto 어댑터
```

### Step 2: entry point 자동 생성을 끈다

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <IsPackable>false</IsPackable>
  <GenerateProgramFile>false</GenerateProgramFile>
</PropertyGroup>
```

### Step 3: `Main.fs`를 컴파일 목록 **맨 마지막**에 둔다

```xml
<ItemGroup>
  <Compile Include="LexerTests.fs" />
  <Compile Include="ParserTests.fs" />
  <Compile Include="EvalTests.fs" />
  <Compile Include="Main.fs" />        <!-- 항상 마지막 -->
</ItemGroup>
```

```fsharp
module Main

open Expecto

[<EntryPoint>]
let main argv = runTestsInAssemblyWithCLIArgs [] argv
```

테스트 파일에서는 `[<Tests>]`를 붙인 값만 쓰면 된다. 두 실행 방법 모두 이 attribute로 테스트를 찾는다.

```fsharp
[<Tests>]
let tests =
    testList "Parser" [
        test "정수" { Expect.equal (parse "7") (Number 7) "" }
    ]
```

### Step 4: 두 방법으로 모두 실행해서 테스트 수가 같은지 확인한다

```bash
dotnet run --project tests/Calc.Tests      # EXPECTO! 125 tests run ...
dotnet test                                # Passed! ... Total: 125
```

### Step 5: CI에서는 "0개 실행"을 실패로 처리한다

종료 코드만 믿지 말고 출력에서 확인한다.

```bash
dotnet test | tee test.log
grep -q "No test is available" test.log && { echo "테스트가 발견되지 않음"; exit 1; }
```

## Example

`dotnet run`으로 실행할 때는 Expecto 옵션을 `--` 뒤에 준다.

```bash
dotnet run --project tests/Calc.Tests -- --summary         # 테스트별 결과
dotnet run --project tests/Calc.Tests -- --filter Parser   # "Parser" 목록과 그 아래만
dotnet run --project tests/Calc.Tests -- --list-tests      # 목록만
```

`--filter`는 "이름에 포함"이 아니다. **테스트 계층 경로의 앞부분**이 일치하는지 본다. `testList "Parser" [...]` 아래의 테스트 이름은 `Parser.정수`처럼 만들어지므로 `--filter Parser`로 그 목록 전체가 선택된다.

## 체크리스트

- [ ] `GenerateProgramFile=false`가 있다
- [ ] `Main.fs`가 `<Compile>` 목록 맨 마지막이다
- [ ] `YoloDev.Expecto.TestSdk`와 `Microsoft.NET.Test.Sdk`가 모두 있다
- [ ] `dotnet run`과 `dotnet test`의 테스트 수가 같다
- [ ] CI에서 "No test is available"을 실패로 처리한다

## 관련 문서

- `write-fscheck-properties-for-parser.md` - Expecto.FsCheck로 property-based testing 작성하기
