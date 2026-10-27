# 테스트 방법

## 자동 테스트 (Expecto)

`tests/Calc.Tests`는 [Expecto](https://github.com/haf/expecto)로 만든 테스트 프로젝트입니다.

### 실행

저장소 루트에서 실행합니다.

```sh
# 방법 1: dotnet test (솔루션 Calc.slnx 의 모든 테스트 프로젝트 실행, CI에 적합)
dotnet test

# 방법 2: 테스트 프로젝트를 콘솔 앱으로 실행 (Expecto 출력이 더 자세함)
dotnet run --project tests/Calc.Tests
```

방법 2에서는 `--` 뒤에 Expecto 옵션을 줄 수 있습니다.

```sh
dotnet run --project tests/Calc.Tests -- --summary          # 테스트별 결과 요약
dotnet run --project tests/Calc.Tests -- --filter Parser    # "Parser" 테스트 목록과 그 아래 테스트만
dotnet run --project tests/Calc.Tests -- --list-tests       # 테스트 목록만 출력
dotnet run --project tests/Calc.Tests -- --debug            # 각 테스트 시작과 끝을 출력
```

### 구성

| 파일 | 테스트 목록 | 내용 |
|---|---|---|
| `LexerTests.fs` | `Lexer` | 토큰 순서, 공백과 줄바꿈 무시, 키워드와 식별자, 빈 입력, 알 수 없는 문자 오류 |
| `ParserTests.fs` | `Parser` | 우선순위, 왼쪽 결합, 괄호, 단항 마이너스, `let`의 범위와 중첩, 파서 오류 |
| `EvalTests.fs` | `Eval` | 식 계산 표, `let`과 섀도잉, 정의되지 않은 변수, 0으로 나누기, FsCheck 속성 테스트 |
| `Main.fs` | — | 진입점 (`runTestsInAssemblyWithCLIArgs`) |

`[<Tests>]` 속성이 붙은 값은 Expecto가 자동으로 찾아서 실행합니다.

### 테스트 추가하기

**예시 기반 테스트**: 결과가 정해진 경우입니다. `EvalTests.fs`라면 `cases` 리스트에 한 줄을 추가하면 됩니다.

```fsharp
let cases =
    [ "1 + 2", 3
      ...
      "새 식", 기대값 ]
```

**속성 기반 테스트**: FsCheck가 무작위 입력을 만들어서 기본 100번 확인합니다.

```fsharp
testProperty "a - b" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
    calc (sprintf "%d - %d" a b) = a - b
```

`NonNegativeInt`를 쓴 이유가 있습니다. 렉서는 음이 아닌 정수만 읽기 때문에 `-2147483648`은 `-`와 `2147483648`로 나뉘고, `2147483648`은 `int` 범위를 넘습니다.

**새 테스트 파일**: 만든 파일을 `Calc.Tests.fsproj`의 `Main.fs` **앞에** `<Compile Include=... />`로 추가합니다.

### 테스트가 Calc 코드를 사용하는 방식

테스트 프로젝트는 `ProjectReference`로 `src/Calc`를 참조합니다. 그래서 다음을 직접 호출할 수 있습니다.
- `Program.parse`, `Program.eval`
- 생성된 `Lexer.tokenize`, `Parser.token`

`--version`과 `--help` 같은 명령줄 옵션은 아직 자동 테스트에 포함되지 않았습니다. 아래의 수동 테스트로 확인하세요.

## 수동 테스트

프로그램을 직접 실행해서 **AST**와 **결과**를 눈으로 확인하는 방법입니다.

## 출력 형식

```
$ dotnet run --project src/Calc -- --expr "2 + 3 * (4 - 1)"
AST:    Add (Number 2, Multiply (Number 3, Subtract (Number 4, Number 1)))
Result: 11
```

- `AST:` 줄은 파서가 만든 트리입니다. 우선순위와 결합 방향이 올바른지 여기서 확인합니다.
- `Result:` 줄은 평가 결과입니다.
- 오류가 나면 표준 에러로 `Error: ...`를 출력하고 종료 코드 `1`을 반환합니다. 성공하면 `0`입니다.

## 정상 동작 확인

| 입력 | 확인 포인트 | 기대 AST | 기대 결과 |
|---|---|---|---|
| `1 + 2` | 기본 동작 | `Add (Number 1, Number 2)` | `3` |
| `2 + 3 * 4` | `*`가 `+`보다 먼저 계산됨 | `Add (Number 2, Multiply (Number 3, Number 4))` | `14` |
| `(2 + 3) * 4` | 괄호 | `Multiply (Add (Number 2, Number 3), Number 4)` | `20` |
| `10 - 3 - 2` | 왼쪽 결합 | `Subtract (Subtract (Number 10, Number 3), Number 2)` | `5` |
| `-5 + 2` | 단항 마이너스 | `Add (Negate (Number 5), Number 2)` | `-3` |
| `7 / 2` | 정수 나눗셈 | `Divide (Number 7, Number 2)` | `3` |
| `let x = 5 in x * 2` | `let` 바인딩 | `Let ("x", Number 5, Multiply (Var "x", Number 2))` | `10` |
| `let x = 1 in x + 2` | 본문은 오른쪽 끝까지 이어짐 | `Let ("x", Number 1, Add (Var "x", Number 2))` | `3` |
| `(let x = 1 in x) + 10` | 괄호로 범위 제한 | `Add (Let ("x", Number 1, Var "x"), Number 10)` | `11` |
| `let x = 1 in let x = 2 in x` | 섀도잉 (안쪽 바인딩 우선) | `Let ("x", Number 1, Let ("x", Number 2, Var "x"))` | `2` |

## 오류 처리 확인

| 입력 | 단계 | 기대 출력 |
|---|---|---|
| `1 +` | 파서 | `Error: parse error` |
| `2 $ 3` | 렉서 | `Error: Unexpected character: $` |
| `1 / 0` | 평가 | AST 출력 후 `Error: Attempted to divide by zero.` |
| `y + 1` | 평가 | AST 출력 후 `Error: Unbound variable: y` |
| `let x = 1` | 파서 (`in` 없음) | `Error: parse error` |
| `let in = 1 in 2` | 파서 (키워드는 변수 이름이 될 수 없음) | `Error: parse error` |

모든 경우 종료 코드는 `1`입니다. 종료 코드는 `echo $?`로 확인할 수 있습니다.

## 명령줄 옵션 확인

| 명령 | 기대 출력 | 종료 코드 |
|---|---|---|
| `--help`, `-h` | `USAGE: calc [--help] [--expr=<expr>] [--version]`로 시작하는 사용법 | 0 |
| `--version`, `-v` | `calc 0.4.0` (`Calc.fsproj`의 `<Version>`) | 0 |
| `-e "(2+3)*4"` | `Result: 20` | 0 |
| `--expr=7/2` | `Result: 3` | 0 |
| `--expr "-5 + 2"` | `Result: -3` (`-`로 시작하는 식도 값으로 처리됨) | 0 |
| `--expr` (값 없음) | `ERROR: argument '--expr' must be followed by <expr>.` + 사용법 | 1 |
| `--bogus` | `ERROR: unrecognized argument: '--bogus'.` + 사용법 | 1 |

## 여러 식을 한 번에 실행

`dotnet run`은 매번 빌드 여부를 확인하기 때문에 느립니다. 한 번 빌드한 다음 DLL을 직접 실행하면 더 빠릅니다.

```sh
dotnet build src/Calc

for e in "1 + 2" "2 + 3 * 4" "(2 + 3) * 4" "10 - 3 - 2" "-5 + 2" "7 / 2" "1 +" "2 \$ 3" "1 / 0"; do
  echo "== $e"
  dotnet src/Calc/bin/Debug/net10.0/Calc.dll --expr "$e"
  echo "exit=$?"
done
```

## 표준 입력 테스트

```sh
echo "1 + 2 * 3" | dotnet run --project src/Calc
# AST:    Add (Number 1, Multiply (Number 2, Number 3))
# Result: 7
```

여러 줄 입력도 하나의 식으로 처리됩니다. 렉서가 줄바꿈을 공백처럼 건너뜁니다.

## 문법을 바꿨을 때 확인할 것

1. `dotnet build src/Calc | grep -i conflict` 결과가 비어 있는지 확인합니다. 충돌이 없어야 합니다.
2. 위 표의 식들이 여전히 같은 AST를 만드는지 확인합니다.
3. 새로 추가한 문법에 대해 정상 입력과 잘못된 입력을 각각 하나 이상 확인합니다.
