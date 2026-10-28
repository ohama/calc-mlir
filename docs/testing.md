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
| `LexerTests.fs` | `Lexer` | 토큰 순서, 공백과 줄바꿈 무시, 키워드와 식별자, 비교·논리 연산자, `->`, 리스트 토큰(`[` `]` `;` `::` `|`), 문자열과 이스케이프, 닫히지 않은 문자열, 빈 입력, 알 수 없는 문자 오류 |
| `ParserTests.fs` | `Parser` | 우선순위, 결합 방향, 괄호, 단항 마이너스, `let`/`if`/`fun`의 범위와 중첩, 함수 호출, 함수 정의 문법 설탕, 리스트와 `::`, 튜플, 패턴과 `match`(가드, 중첩), 튜플 분해 `let`, 생성자와 `type` 선언, 타입 식, 문자열 리터럴, 파서 오류 |
| `InferTests.fs` | `Infer` | 추론한 타입 표(다형성, 재귀, 패턴, 내장 함수, ADT), 타입 오류 메시지, 건전성(타입 검사를 통과한 `EvalTests` 예제는 실행 중 오류 없이 그 타입의 값이 됨), 타입 검사가 거부하는 동적 예제 목록, FsCheck 속성 |
| `CompileTests.fs` | `Compile` | 생성한 MLIR, 값 표현(태그), 컴파일한 실행 파일의 출력, **차등 테스트**(컴파일할 수 있는 `EvalTests` 예제를 인터프리터와 비교). LLVM/GC가 없으면 건너뜀(Skipped) |
| `EvalTests.fs` | `Eval` | 식 계산 표(정수, 불리언, 함수, 재귀, 리스트, 문자열, 내장 함수, 튜플, 패턴 매칭, option, 사용자 정의 타입), `let`과 섀도잉, 클로저, 단락 평가, 타입 오류, 리스트·문자열·내장 함수·패턴 매칭·ADT 오류와 출력, 정의되지 않은 변수, 0으로 나누기, FsCheck 속성 테스트 |
| `Main.fs` | — | 진입점 (`runTestsInAssemblyWithCLIArgs`) |

`[<Tests>]` 속성이 붙은 값은 Expecto가 자동으로 찾아서 실행합니다.

### 테스트 추가하기

**예시 기반 테스트**: 결과가 정해진 경우입니다. `EvalTests.fs`라면 `cases` 리스트에 한 줄을 추가하면 됩니다.

```fsharp
let cases =
    [ "1 + 2", VInt 3
      "1 < 2", VBool true
      ...
      "새 식", 기대값 ]
```

**속성 기반 테스트**: FsCheck가 무작위 입력을 만들어서 기본 100번 확인합니다.

```fsharp
testProperty "a - b" <| fun (NonNegativeInt a) (NonNegativeInt b) ->
    calc (sprintf "%d - %d" a b) = VInt (a - b)
```

**오류 테스트**: `expectError 입력 메시지`는 오류 메시지가 정확히 일치하는지 확인합니다.

```fsharp
expectError "1 + true" "Type error: expected int but got bool"
```

`NonNegativeInt`를 쓴 이유가 있습니다. 렉서는 음이 아닌 정수만 읽기 때문에 `-2147483648`은 `-`와 `2147483648`로 나뉘고, `2147483648`은 `int` 범위를 넘습니다.

**새 테스트 파일**: 만든 파일을 `Calc.Tests.fsproj`의 `Main.fs` **앞에** `<Compile Include=... />`로 추가합니다.

### 테스트가 Calc 코드를 사용하는 방식

테스트 프로젝트는 `ProjectReference`로 `src/Calc`를 참조합니다. 그래서 다음을 직접 호출할 수 있습니다.
- `Program.parse`
- `Eval.eval`, `Eval.evalIn`, `Eval.formatValue`, `Eval.Value`(`VInt`, `VBool`)
- 생성된 `Lexer.tokenize`, `Parser.token`

`--version`과 `--help` 같은 명령줄 옵션은 아직 자동 테스트에 포함되지 않았습니다. 아래의 수동 테스트로 확인하세요.

## 수동 테스트

프로그램을 직접 실행해서 **AST**와 **결과**를 눈으로 확인하는 방법입니다.

## 출력 형식

```
$ dotnet run --project src/Calc -- --expr "2 + 3 * (4 - 1)"
AST:    Add (Number 2, Multiply (Number 3, Subtract (Number 4, Number 1)))
Type:   int
Result: 11
```

- `AST:` 줄은 파서가 만든 트리입니다. 우선순위와 결합 방향이 올바른지 여기서 확인합니다.
- `Type:` 줄은 타입 추론 결과입니다. 타입이 맞지 않으면 이 줄 대신 `Error: Type mismatch: ...`가 나오고 평가하지 않습니다. `--no-typecheck`를 주면 이 줄이 없습니다.
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
| `1 + 2 < 4` | 산술이 비교보다 먼저 계산됨 | `Less (Add (Number 1, Number 2), Number 4)` | `true` |
| `true \|\| false && false` | `&&`가 `\|\|`보다 먼저 계산됨 | `Or (Bool true, And (Bool false, Bool false))` | `true` |
| `not true = false` | `not`이 비교보다 먼저 계산됨 | `Equal (Not (Bool true), Bool false)` | `true` |
| `if 1 < 2 then 10 else 20` | `if-then-else` | `If (Less (Number 1, Number 2), Number 10, Number 20)` | `10` |
| `if false then 1 else 2 + 3` | `else` 분기는 오른쪽 끝까지 이어짐 | `If (Bool false, Number 1, Add (Number 2, Number 3))` | `5` |
| `false && 1 / 0 = 0` | 단락 평가 (오른쪽은 평가하지 않음) | `And (Bool false, Equal (Divide (Number 1, Number 0), Number 0))` | `false` |
| `let f x = x + 1 in f 41` | 함수 정의와 호출 | `Let ("f", Lambda ("x", Add (Var "x", Number 1)), App (Var "f", Number 41))` | `42` |
| `(fun x -> x * 2) 21` | 람다 즉시 호출 | `App (Lambda ("x", Multiply (Var "x", Number 2)), Number 21)` | `42` |
| `let add x y = x + y in let inc = add 1 in inc 41` | 커링과 부분 적용 | | `42` |
| `let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 10` | 재귀 | | `3628800` |
| `let x = 1 in let f y = x + y in let x = 100 in f 1` | 클로저 (정적 범위) | | `2` |
| `fun x -> x` | 함수 값 출력 | `Lambda ("x", Var "x")` | `<fun>` |
| `[1; 2; 3]` | 리스트 리터럴 | `Cons (Number 1, Cons (Number 2, Cons (Number 3, Nil)))` | `[1; 2; 3]` |
| `1 + 2 :: [3]` | `::`는 `+`보다 약함 | `Cons (Add (Number 1, Number 2), Cons (Number 3, Nil))` | `[3; 3]` |
| `let rec len xs = match xs with [] -> 0 \| h :: t -> 1 + len t in len [1; 2; 3]` | `match`와 재귀 | | `3` |
| `"a" + "b"` | 문자열 연결 | `Add (Str "a", Str "b")` | `"ab"` |
| `"B" < "a"` | 문자열 비교 (문자 코드 순서) | `Less (Str "B", Str "a")` | `true` |
| `join "-" (split "2026-10-28" "-")` | 내장 함수 | | `"2026-10-28"` |
| `let first = substr "hello" 0 in first 2` | 내장 함수 부분 적용 | | `"he"` |
| `sum (map (fun x -> x * x) (range 1 10))` | 리스트 내장 함수 | | `385` |
| `fold (fun acc x -> x :: acc) [] [1; 2; 3]` | `fold` (왼쪽부터) | | `[3; 2; 1]` |
| `let (a, b) = (1, 2) in (b, a)` | 튜플과 튜플 분해 | | `(2, 1)` |
| `match [1; 2; 3] with [] -> 0 \| [x] -> x \| x :: y :: _ -> x + y` | 중첩 패턴, 위에서부터 첫 일치 | | `3` |
| `match 5 with n when n > 3 -> "big" \| _ -> "small"` | `when` 가드 | | `"big"` |
| `type shape = Circle of int \| Rect of int * int in map (fun s -> match s with Circle r -> r \| Rect (w, h) -> w * h) [Circle 2; Rect (2, 3)]` | 사용자 정의 타입 | | `[2; 6]` |
| `defaultValue 0 (tryFind (fun x -> x > 1) [1; 2; 3])` | option | | `2` |

## 컴파일러 테스트

`Compile` 테스트는 식을 실행 파일로 컴파일하고 실행합니다. 예제 하나에 외부 도구 3개와 실행 파일을 띄우므로 느립니다(예제당 약 0.2초). 컴파일러만 따로 돌리려면 다음과 같이 실행합니다.

```sh
dotnet run --project tests/Calc.Tests -- --filter "Compile"
```

- `brew install llvm bdw-gc`가 되어 있지 않으면 이 테스트들은 실패가 아니라 **Skipped**로 표시됩니다.
- 차등 테스트는 기대값을 따로 적지 않습니다. 같은 식을 `Eval.eval`로 실행한 결과(`Eval.formatValue`)와 실행 파일의 출력을 비교합니다. 컴파일러가 지원하는 기능이 늘어나면 비교하는 예제도 자동으로 늘어납니다.

## 오류 처리 확인

| 입력 | 단계 | 기대 출력 |
|---|---|---|
| `1 +` | 파서 | `Error: parse error: unexpected end of input (line 1, column 4)` 아래에 오류 줄과 `^` 표시 |
| `1 + + 2` | 파서 | `Error: parse error: unexpected '+' (line 1, column 5)` |
| `2 $ 3` | 렉서 | `Error: Unexpected character: $ (line 1, column 3)` |
| `let x = 1` | 파서 (`in` 없음) | `Error: parse error: unexpected end of input (line 1, column 10)` |
| `let in = 1 in 2` | 파서 (키워드는 변수 이름이 될 수 없음) | `Error: parse error: unexpected 'in' (line 1, column 5)` |
| `if true then 1` | 파서 (`else` 없음) | `Error: parse error: unexpected end of input (line 1, column 15)` |
| `let rec f = 1 in f` | 파서 (`let rec`에 인자 없음) | `Error: parse error: unexpected '=' (line 1, column 11)` |
| `match p with (a, a) -> a` | 파서 (패턴 안에 같은 이름) | `Error: Duplicate variable in pattern: a (line 1, column 14)` |
| `"abc` | 렉서 (닫히지 않은 문자열) | `Error: Unterminated string (line 1, column 1)` |
| `2147483648` | 렉서 (`int` 범위 밖) | `Error: Integer literal out of range: 2147483648 (line 1, column 1)` |
| `y + 1` | 타입 추론 | AST 출력 후 `Error: Unbound variable: y` |
| `let f x = f x in f 1` | 타입 추론 (`rec` 없이 재귀) | AST 출력 후 `Error: Unbound variable: f` |
| `1 + true` | 타입 추론 | AST 출력 후 `Error: Type mismatch: expected int but got bool` |
| `"a" + 1` | 타입 추론 | AST 출력 후 `Error: Type mismatch: expected string but got int` |
| `if 1 then 2 else 3` | 타입 추론 | AST 출력 후 `Error: Type mismatch: expected bool but got int` |
| `if true then 1 else false` | 타입 추론 (분기 타입이 다름) | AST 출력 후 `Error: Type mismatch: expected int but got bool` |
| `[1; true]` | 타입 추론 (원소 타입이 다름) | AST 출력 후 `Error: Type mismatch: expected int list but got bool list` |
| `1 < 2 < 3` | 타입 추론 (`(1 < 2) < 3`) | AST 출력 후 `Error: Type mismatch: expected int or string but got bool` |
| `1 2` | 타입 추론 (함수가 아닌 값 호출) | AST 출력 후 `Error: Type mismatch: expected function but got int` |
| `1 :: 2` | 타입 추론 (`::` 오른쪽이 리스트가 아님) | AST 출력 후 `Error: Type mismatch: expected int list but got int` |
| `fun x -> x x` | 타입 추론 (무한 타입) | AST 출력 후 `Error: Type mismatch: cannot construct infinite type 'a = 'a -> 'b` |
| `match Some 1 with Nome -> 0 \| _ -> 1` | 타입 추론 (모르는 생성자) | AST 출력 후 `Error: Unknown constructor: Nome` |
| `--no-typecheck -e "1 + true"` | 평가 (동적 타입 검사) | AST 출력 후 `Error: Type error: expected int but got bool` |
| `1 / 0` | 평가 | AST, `Type: int` 출력 후 `Error: Attempted to divide by zero.` |
| `toInt "abc"` | 평가 (내장 함수) | AST, `Type: int` 출력 후 `Error: toInt: invalid integer: "abc"` |
| `head []` | 평가 (내장 함수) | AST, `Type: 'a` 출력 후 `Error: head: empty list` |
| `match 1 with 2 -> 0` | 평가 (일치하는 패턴 없음) | AST, `Type: int` 출력 후 `Error: Match failure: 1` |
| `let rec loop n = if n = 0 then 0 else loop (n - 1) in loop 1000000` | 평가 (재귀가 너무 깊음) | `Stack overflow.` 후 프로세스 종료 (종료 코드 134) |

모든 경우 종료 코드는 `1`입니다. 종료 코드는 `echo $?`로 확인할 수 있습니다.

## 명령줄 옵션 확인

| 명령 | 기대 출력 | 종료 코드 |
|---|---|---|
| `--help`, `-h` | `USAGE: calc [--help] [--expr=<expr>] [--version] [--no-typecheck]`로 시작하는 사용법 | 0 |
| `--version`, `-v` | `calc 0.15.0` (`Calc.fsproj`의 `<Version>`) | 0 |
| `-e "(2+3)*4"` | `Result: 20` | 0 |
| `--expr=7/2` | `Result: 3` | 0 |
| `--expr "-5 + 2"` | `Result: -3` (`-`로 시작하는 식도 값으로 처리됨) | 0 |
| `--no-typecheck -e "[1; true]"` | `Type:` 줄 없이 `Result: [1; true]` (타입 추론 생략, 평가만) | 0 |
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
# Type:   int
# Result: 7
```

여러 줄 입력도 하나의 식으로 처리됩니다. 렉서가 줄바꿈을 공백처럼 건너뜁니다.

## 문법을 바꿨을 때 확인할 것

1. `dotnet build src/Calc | grep -i conflict` 결과가 비어 있는지 확인합니다. 충돌이 없어야 합니다.
2. 위 표의 식들이 여전히 같은 AST를 만드는지 확인합니다.
3. 새로 추가한 문법에 대해 정상 입력과 잘못된 입력을 각각 하나 이상 확인합니다.
