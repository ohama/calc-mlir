# 테스트 방법

아직 자동화된 테스트 프로젝트는 없습니다. 지금은 프로그램을 직접 실행해서 **AST**와 **결과**를 확인하는 방식으로 테스트합니다.

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

## 오류 처리 확인

| 입력 | 단계 | 기대 출력 |
|---|---|---|
| `1 +` | 파서 | `Error: parse error` |
| `2 $ 3` | 렉서 | `Error: Unexpected character: $` |
| `1 / 0` | 평가 | AST 출력 후 `Error: Attempted to divide by zero.` |

세 경우 모두 종료 코드는 `1`입니다. 종료 코드는 `echo $?`로 확인할 수 있습니다.

## 명령줄 옵션 확인

| 명령 | 기대 출력 | 종료 코드 |
|---|---|---|
| `--help`, `-h` | `USAGE: calc [--help] [--expr=<expr>] [--version]`로 시작하는 사용법 | 0 |
| `--version`, `-v` | `calc 0.2.0` (`Calc.fsproj`의 `<Version>`) | 0 |
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
