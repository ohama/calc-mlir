---
order: 5
created: 2026-10-27
commit: a0ebeba
version: 0.4.0
description: 버전은 fsproj <Version> 한 곳에서 관리하고 실행 중에 어셈블리에서 읽는다. +git 해시 접미사와 GetEntryAssembly 함정에 주의한다
---

# fsproj `<Version>` 하나로 버전 관리하기

> 관련 커밋: [a0ebeba](https://github.com/ohama/calc-mlir/commit/a0ebeba) (v0.4.0)

버전 번호를 코드, 문서, 태그에 따로 적으면 반드시 어긋난다. `.fsproj`의 `<Version>`을 **유일한 원본**으로 두고, 프로그램은 실행 중에 assembly attribute에서 버전을 읽는다.

## The Insight

`<Version>0.4.0</Version>` 한 줄을 쓰면 SDK가 빌드할 때 assembly attribute 세 개를 만든다(`obj/.../Calc.AssemblyInfo.fs`).

```fsharp
[<assembly: AssemblyVersionAttribute("0.4.0.0")>]
[<assembly: AssemblyFileVersionAttribute("0.4.0.0")>]
[<assembly: AssemblyInformationalVersionAttribute("0.4.0+12ed14b2438ebdf7362dce6d19a8ea493488578c")>]
```

- `AssemblyVersion`, `FileVersion`은 항상 네 자리(`0.4.0.0`)라서 SemVer 문자열과 모양이 다르다.
- `InformationalVersion`은 `<Version>` 문자열 그대로다. 단, **git 저장소 안에서 빌드하면 `+<커밋 해시>`가 붙는다**. .NET 8부터 SDK 기본 동작이다.

그래서 `InformationalVersion`을 읽되 `+` 뒤를 잘라내거나, 해시가 붙지 않게 설정한다.

## Why This Matters

**함정 1: `+해시` 접미사.** 그대로 출력하면 `calc 0.4.0+12ed14b...`가 나온다. 같은 코드도 git 밖(예: 소스 압축 파일)에서 빌드하면 해시가 없다. 그래서 환경마다 출력이 달라진다.

**함정 2: `Assembly.GetEntryAssembly()`.** "실행 중인 프로그램의 어셈블리"를 돌려준다. CLI로 실행할 때는 Calc가 맞다. 하지만 **같은 함수를 테스트에서 호출하면 다른 어셈블리를 가리킨다**. 이 프로젝트에서 테스트 안에서 `Program.version()`을 호출한 결과는 다음과 같다.

| 실행 방법 | `GetEntryAssembly()` | `version()` 결과 |
|---|---|---|
| `calc --version` | `Calc` | `0.4.0` ✅ |
| `dotnet run --project tests/Calc.Tests` | `Calc.Tests` | `1.0.0` ❌ (테스트 프로젝트에 Version이 없어 기본값) |
| `dotnet test` | `testhost` | `18.11.0` ❌ (Microsoft.NET.Test.Sdk 버전) |
| (비교) `typeof<Ast.Expr>.Assembly` | `Calc` | `0.4.0` ✅ (어디서 호출해도 같음) |

## Recognition Pattern

- `--version` 출력에 `+` 뒤로 긴 해시가 붙는다
- 버전을 확인하는 테스트가 `1.0.0`이나 테스트 SDK 버전 같은 엉뚱한 값을 받는다
- CHANGELOG, 태그, `--version` 출력이 서로 다르다

## The Approach

**"버전 정보가 어디에서 시작되는가?"** 를 한 곳으로 정한다. 나머지는 모두 그 값을 읽거나, 릴리스 절차에서 함께 맞춘다.

### Step 1: fsproj에 버전을 둔다

```xml
<PropertyGroup>
  <Version>0.4.0</Version>
  <!-- (선택) +git 해시 접미사를 붙이지 않음 -->
  <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
</PropertyGroup>
```

`IncludeSourceRevisionInInformationalVersion=false`를 쓰면 `InformationalVersion`이 정확히 `0.4.0`이 된다(확인함). 해시 정보가 디버깅에 쓸모 있다면 이 설정 대신 코드에서 잘라낸다.

### Step 2: 실행 중에 읽는다

어셈블리를 고를 때는 `GetEntryAssembly()` 대신 **버전을 가진 프로젝트 안의 타입**을 기준으로 삼는다.

```fsharp
open System.Reflection

/// Calc.fsproj 의 <Version> 값 (빌드 메타데이터 "+<git hash>" 는 제거)
let version () =
    let attr =
        typeof<Ast.Expr>.Assembly   // GetEntryAssembly() 는 테스트에서 testhost 를 가리킴
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    attr.InformationalVersion.Split('+').[0]
```

> 현재 이 프로젝트의 `Program.version()`은 `GetEntryAssembly()`를 쓴다. CLI에서는 문제없지만, `version()`을 테스트하려면 위처럼 바꿔야 한다.

### Step 3: 릴리스 절차로 CHANGELOG와 git 태그를 맞춘다

이 프로젝트는 세 곳을 같은 버전으로 유지한다.

```bash
# 1. fsproj 버전 올리기
sed -i '' 's|<Version>0.4.0</Version>|<Version>0.5.0</Version>|' src/Calc/Calc.fsproj

# 2. CHANGELOG: "## [Unreleased]" → "## [0.5.0] - YYYY-MM-DD"

# 3. 다시 빌드해서 실제 출력 확인
dotnet build && dotnet src/Calc/bin/Debug/net10.0/Calc.dll --version   # calc 0.5.0
dotnet test

# 4. 커밋 + annotated 태그
git add -A && git commit -m "Release 0.5.0: ..."
git tag -a v0.5.0 -m "Release 0.5.0"
```

태그는 반드시 **버전을 올린 커밋**에 붙인다. 태그를 먼저 붙이고 나중에 CHANGELOG를 고치면, 태그가 가리키는 코드와 문서가 어긋난다.

## Example

```
$ dotnet build src/Calc && grep Informational src/Calc/obj/Debug/net10.0/Calc.AssemblyInfo.fs
[<assembly: ...AssemblyInformationalVersionAttribute("0.4.0+12ed14b2438ebdf7...")>]

$ dotnet build src/Calc -p:IncludeSourceRevisionInInformationalVersion=false
$ grep Informational src/Calc/obj/Debug/net10.0/Calc.AssemblyInfo.fs
[<assembly: ...AssemblyInformationalVersionAttribute("0.4.0")>]
```

## 체크리스트

- [ ] 코드에 버전 문자열을 직접 적지 않았다 (`printfn "0.4.0"` 금지)
- [ ] `+해시`를 잘라내거나 `IncludeSourceRevisionInInformationalVersion=false`로 껐다
- [ ] 어셈블리를 `typeof<프로젝트 안의 타입>.Assembly`로 골랐다 (테스트할 계획이 있다면)
- [ ] 릴리스할 때 fsproj, CHANGELOG, git 태그를 한 커밋에서 맞췄다
- [ ] 다시 빌드한 뒤 `--version` 출력을 직접 확인했다

## 관련 문서

- `04-setup-expecto-with-dotnet-test.md` - `dotnet test`에서 진입 어셈블리가 testhost가 되는 이유
