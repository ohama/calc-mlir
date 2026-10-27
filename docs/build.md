# 컴파일 방법

## 요구 사항

- .NET SDK 10.0 이상 (`dotnet --version`으로 확인)
- FsLexYacc 11.3.0. `Calc.fsproj`의 `PackageReference`에 들어 있어서 빌드할 때 자동으로 복원됩니다.

## 빌드

저장소 루트에서 실행합니다.

```sh
dotnet build             # 솔루션(Calc.slnx) 전체: Calc + Calc.Tests
dotnet build src/Calc    # Calc 프로젝트만
```

성공하면 다음과 같이 출력됩니다.

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

결과물은 `src/Calc/bin/Debug/net10.0/Calc.dll`에 생성됩니다.

릴리스 빌드는 다음 명령을 씁니다.

```sh
dotnet build src/Calc -c Release
```

## 실행

```sh
# --expr 로 식 전달
dotnet run --project src/Calc -- --expr "2 + 3 * (4 - 1)"

# 표준 입력으로 식 전달 (--expr 생략)
echo "1 + 2 * 3" | dotnet run --project src/Calc
```

`--` 뒤에 오는 값은 `dotnet run`이 아니라 Calc 프로그램에 전달됩니다.

이미 빌드했다면 `dotnet run` 없이 바로 실행할 수도 있습니다. 이때는 `--`가 필요 없습니다.

```sh
dotnet src/Calc/bin/Debug/net10.0/Calc.dll --expr "2 + 3"
```

### 명령줄 옵션

옵션 처리는 [Argu](https://fsprojects.github.io/Argu/)로 구현했습니다.

| 옵션 | 짧은 형식 | 설명 |
|---|---|---|
| `--expr <expr>` | `-e <expr>` | 주어진 식을 계산합니다. `--expr=<expr>` 형식도 됩니다. 생략하면 표준 입력에서 읽습니다. |
| `--version` | `-v` | 버전을 출력하고 종료합니다 (예: `calc 0.6.0`). |
| `--help` | `-h` | 사용법을 출력하고 종료합니다. |

`--version`이 출력하는 버전은 `Calc.fsproj`의 `<Version>` 값을 실행 중에 어셈블리에서 읽어 옵니다. 그래서 버전 번호는 fsproj 한 곳만 고치면 됩니다.

알 수 없는 옵션을 주거나 `--expr` 뒤에 값이 없으면 오류 메시지와 사용법을 표준 에러로 출력하고 종료 코드 1을 반환합니다.

## 빌드 과정

`dotnet build`를 실행하면 F# 컴파일 전에 다음 두 단계가 먼저 수행됩니다.

| 입력 | 도구 | 생성 파일 |
|---|---|---|
| `Parser.fsy` | fsyacc (`--module Parser`) | `Parser.fs`, `Parser.fsi` |
| `Lexer.fsl` | fslex (`--module Lexer --unicode`) | `Lexer.fs`, `Lexer.fsi` |

생성 파일은 `src/Calc/` 안에 만들어지고, 빌드할 때마다 다시 생성되므로 `.gitignore`로 제외되어 있습니다. **직접 수정하지 말고** `.fsy`/`.fsl` 파일을 고치세요.

그다음 F# 컴파일러가 `Calc.fsproj`에 적힌 순서대로 컴파일합니다.

```
Ast.fs → Eval.fs → Parser.fsi → Parser.fs → Lexer.fs → Program.fs
```

F#은 앞에 나온 파일만 참조할 수 있습니다. 그래서 순서가 중요합니다.

- `Eval`은 `Ast`의 `Expr`를 평가합니다.
- `Parser`는 `Ast`의 `Expr` 타입을 사용합니다.
- `Lexer`는 `Parser`가 정의한 토큰(`NUMBER`, `PLUS` 등)을 사용합니다.
- `Program`은 `Lexer`와 `Parser`를 모두 사용합니다.

새 `.fs` 파일을 추가할 때도 의존 관계에 맞는 위치에 `<Compile Include=... />`를 넣어야 합니다.

## 문법 충돌 확인

fsyacc는 shift/reduce, reduce/reduce 충돌이 있어도 빌드를 멈추지 않고 경고만 냅니다. 문법을 고친 다음에는 빌드 출력에 `conflict`가 있는지 확인하세요.

```sh
dotnet build src/Calc | grep -i conflict
```

상세 출력에서 상태 테이블을 보고 싶다면 `Calc.fsproj`의 FsYacc `OtherFlags`에 `-v`를 추가하세요. `Parser.fsyacc.output` 파일이 생성됩니다.

## 정리

```sh
dotnet clean src/Calc
```

생성된 Lexer/Parser 파일까지 지우려면 다음 명령을 실행합니다.

```sh
rm -f src/Calc/{Lexer,Parser}.fs{,i}
```
