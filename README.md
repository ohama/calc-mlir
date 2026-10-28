# Calc

F#과 [FsLexYacc](https://github.com/fsprojects/FsLexYacc)(fslex, fsyacc)로 만드는 작은 식(expression) 언어입니다. 렉서, 파서, AST, 평가기를 단계별로 확장하면서 언어 구현을 공부하기 위한 프로젝트입니다.

```
$ dotnet run --project src/Calc -- --expr "let x = 5 in if x > 3 && not (x = 4) then x * 2 else 0"
AST:    Let
  ("x", Number 5,
   If
     (And (Greater (Var "x", Number 3), Not (Equal (Var "x", Number 4))),
      Multiply (Var "x", Number 2), Number 0))
Result: 10
```

## 언어 기능

| 기능 | 예 |
|---|---|
| 정수 사칙연산 (32비트, 정수 나눗셈) | `2 + 3 * (4 - 1)`, `7 / 2`, `-5` |
| 불리언 | `true`, `false` |
| 비교 | `=` `<>` `<` `>` `<=` `>=` |
| 논리 연산 (단락 평가) | `&&`, `\|\|`, `not` |
| `let` 바인딩 | `let x = 1 + 2 in x * x` |
| 조건식 | `if x > 0 then x else -x` |
| 함수 정의와 호출 (커링, 클로저) | `let add x y = x + y in add 1 2`, `(fun x -> x * 2) 21` |
| 재귀 함수 | `let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 10` |
| 문자열 (`+`로 연결, 비교) | `"Hello, " + name`, `"a" < "b"` |
| 문자열 내장 함수 | `length`, `substr`, `upper`, `split`, `join`, `toInt`, `toString` 등 14개 |
| 리스트·튜플·option 내장 함수 | `map`, `filter`, `fold`, `range`, `sum`, `sort`, `head`, `nth`, `fst`, `snd`, `tryHead`, `defaultValue` 등 19개 |
| 리스트 | `[1; 2; 3]`, `0 :: xs` |
| 튜플 | `(1, "a")`, `let (a, b) = p in a + b` |
| 대수적 데이터 타입, `option` | `type shape = Circle of int \| Rect of int * int in ...`, `Some 1`, `None` |
| 패턴 매칭 (중첩 패턴, `when` 가드) | `match xs with [] -> 0 \| [x] -> x \| x :: _ when x > 0 -> x \| _ -> -1` |

- 우선순위 (낮은 것부터): `let`/`if`/`fun`/`match` < `||` < `&&` < 비교 < `::` < `+ -` < `* /` < 단항 `-`, `not` < 함수 호출
- 타입은 실행 중에 검사합니다. 예를 들어 `1 + true`는 `Type error: expected int but got bool`입니다.

자세한 문법과 의미는 [docs/grammar.md](docs/grammar.md)에 있습니다.

## 시작하기

**요구 사항:** .NET SDK 10.0 이상. FsLexYacc, Argu, Expecto 같은 NuGet 패키지는 빌드할 때 자동으로 복원됩니다.

```sh
dotnet build    # 솔루션 전체 빌드 (Calc + Calc.Tests)
dotnet test     # 테스트 실행
```

### 실행

```sh
# 식을 인자로 전달
dotnet run --project src/Calc -- --expr "2 + 3 * (4 - 1)"

# 표준 입력으로 전달
echo "1 < 2" | dotnet run --project src/Calc
```

`--` 뒤의 인자는 `dotnet run`이 아니라 Calc 프로그램에 전달됩니다.

| 옵션 | 설명 |
|---|---|
| `--expr <expr>`, `-e <expr>` | 식을 계산합니다. 생략하면 표준 입력에서 읽습니다. |
| `--version`, `-v` | 버전을 출력합니다. |
| `--help`, `-h` | 사용법을 출력합니다. |

성공하면 종료 코드 0, 렉서·파서·평가 오류가 나면 `Error: ...`를 표준 에러로 출력하고 종료 코드 1을 반환합니다.

렉서·파서 오류는 위치와 함께 오류가 난 줄을 보여 줍니다.

```
$ dotnet run --project src/Calc -- --expr "1 + + 2"
Error: parse error: unexpected '+' (line 1, column 5)
  1 | 1 + + 2
    |     ^
```

## 프로젝트 구조

```
.
├── Calc.slnx                 # 솔루션
├── src/Calc/
│   ├── Calc.fsproj           # <Version> 은 여기서 관리
│   ├── Ast.fs                # AST (Expr)
│   ├── Errors.fs             # 렉서·파서 오류 (SyntaxError, 위치 포함)
│   ├── Eval.fs               # 평가기 (Value, Env, eval)
│   ├── Parser.fsy            # fsyacc 문법 → Parser.fs (빌드 시 생성)
│   ├── Lexer.fsl             # fslex 규칙 → Lexer.fs (빌드 시 생성)
│   └── Program.fs            # 명령줄 처리 (Argu)
├── tests/Calc.Tests/         # Expecto + FsCheck 테스트
├── docs/                     # 빌드, 테스트, 문법 문서
├── howto/                    # 문제 해결 기록
└── CHANGELOG.md
```

처리 흐름:

```
문자열 → Lexer.tokenize → Parser.start → Ast.Expr → Eval.eval → Value
```

`Lexer.fs`와 `Parser.fs`는 빌드할 때 `.fsl`과 `.fsy`에서 생성되며 git에 포함되지 않습니다. 직접 수정하지 말고 `.fsl`, `.fsy` 파일을 고치세요.

## 문서

| 문서 | 내용 |
|---|---|
| [docs/build.md](docs/build.md) | 빌드, 실행, 빌드 과정과 파일 순서, 문법 충돌 확인 |
| [docs/testing.md](docs/testing.md) | 자동 테스트(Expecto) 실행과 추가 방법, 수동 테스트 표 |
| [docs/grammar.md](docs/grammar.md) | 토큰, EBNF, 우선순위, AST 대응, 평가 규칙, 오류 |
| [howto/](howto/README.md) | 개발하면서 부딪힌 문제와 해결 방법 |
| [CHANGELOG.md](CHANGELOG.md) | 버전별 변경 사항 |

## 버전

버전은 [Semantic Versioning](https://semver.org/lang/ko/)을 따르며, 다음 세 곳을 항상 같게 유지합니다.

1. `src/Calc/Calc.fsproj`의 `<Version>`. `calc --version`이 이 값을 출력합니다.
2. `CHANGELOG.md`의 버전 항목
3. git 태그 `vX.Y.Z`
