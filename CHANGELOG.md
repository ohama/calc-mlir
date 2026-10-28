# Changelog

이 파일은 버전별 변경 사항을 기록합니다. 형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/)를,
버전 번호는 [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.
버전은 `src/Calc/Calc.fsproj`의 `<Version>`에서 관리합니다.

## [0.12.0] - 2026-10-28

### 추가
- 튜플 `(1, "a", true)`. 괄호 안에 원소 2개 이상을 씁니다. 같은 원소 수끼리 `=`로 비교할 수 있습니다.
- 일반 패턴 매칭
  - 분기 수 제한 없음. 위에서부터 첫 일치 분기를 고르고, 없으면 `Match failure: <값>`
  - 패턴: 변수, `_`, 정수·문자열·불리언 리터럴, `[]`, `p :: p`, `[p; p]`, `(p, p)`, 중첩
  - `when` 가드
- 튜플 분해 `let (a, b) = e in body`
- `_` 인자: `fun _ -> 1`, `let f _ x = x`
- 내장 함수 `fst`, `snd`
- 파서 오류 `Duplicate variable in pattern: <이름> (line L, column C)`
- 렉서: 키워드 `when`, 토큰 `,`, `_`
- AST: `Pattern`(`PVar`, `PWildcard`, `PInt`, `PBool`, `PString`, `PNil`, `PCons`, `PTuple`), `Tuple`, `LetPattern`. 값: `VTuple`
- 테스트 72개 추가 (총 491개)

### 변경
- `Match`의 AST가 `Match (e, [ (pattern, guard, body) ... ])`로 바뀌었습니다. 예전의 두 분기 고정 `match`는 이 일반 형식에 포함됩니다.
- 중첩 `match`의 뒤 분기는 이제 F#처럼 **안쪽** `match`의 것입니다. 예전에는 분기 수가 두 개로 고정이라 바깥 `match`의 것이었습니다. 바깥 분기로 쓰려면 안쪽 `match`를 괄호로 감쌉니다.
- `match`에서 리스트가 아닌 값은 타입 오류 대신 `Match failure`입니다. 패턴은 타입이 달라도 불일치로 처리합니다.
- `_` 하나는 이제 변수 이름이 아닙니다. 식에 쓰면 parse error입니다.
- 함수를 비교할 때의 오류 메시지가 `expected int, bool, string, list or tuple but got function`으로 바뀌었습니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 튜플과 패턴 매칭을 반영했습니다.

## [0.11.0] - 2026-10-28

### 추가
- 렉서·파서 오류에 위치 표시. 줄과 열은 1부터 셉니다.
  - `parse error: unexpected '+' (line 1, column 5)`처럼 문법에 맞지 않는 토큰과 그 위치를 보여 줍니다.
  - 입력이 중간에 끝나면 `parse error: unexpected end of input`이고, 위치는 마지막 글자 바로 뒤입니다.
  - `Unterminated string`은 여는 `"` 위치, `Invalid escape sequence`는 `\` 위치를 보여 줍니다.
- `calc`가 오류 메시지 아래에 오류가 난 줄과 `^` 표시를 출력합니다.
- `Errors` 모듈: `SyntaxError(message, line, column)` 예외
- `Program.errorLocation`: 오류 줄과 `^` 표시 문자열을 만듭니다.
- 테스트 16개 추가 (총 419개)

### 변경
- 렉서 오류가 `Exception` 대신 `Errors.SyntaxError`입니다. `Message`에 위치가 붙습니다.
- 파서 오류 메시지가 `parse error`에서 `parse error: unexpected ... (line L, column C)`로 바뀌었습니다.
- 범위를 넘는 정수 리터럴의 오류가 .NET 메시지(`Value was either too large or too small for an Int32.`) 대신 `Integer literal out of range: <숫자>`입니다.
- `docs/grammar.md`, `docs/testing.md`, `docs/build.md`, `README.md`에 오류 위치 규칙과 메시지를 반영했습니다.

## [0.10.0] - 2026-10-28

### 추가
- 리스트 내장 함수 14개
  - 기본: `head`, `tail`, `isEmpty`, `nth`, `reverse`, `append`, `range`, `sum`, `sort`
  - 고차 함수: `map`, `filter`, `fold`, `exists`, `forall`. F# `List`처럼 함수가 먼저, 리스트가 마지막입니다.
- 평가 오류 `head: empty list`, `tail: empty list`, `nth: index out of range (...)`
- `Eval.compareValues`: `<` 등과 `sort`가 함께 쓰는 순서
- 테스트 71개 추가 (총 403개)

### 변경
- `Eval.callBuiltin`이 첫 인자로 `apply`를 받습니다. 고차 내장 함수가 사용자 함수를 호출하는 데 씁니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 리스트 내장 함수를 반영했습니다.

## [0.9.0] - 2026-10-28

### 추가
- 내장 함수 14개. 처음 환경에 들어 있고, 커링되며, `let`으로 가릴 수 있습니다.
  - 문자열: `length`, `substr`, `upper`, `lower`, `trim`, `contains`, `startsWith`, `endsWith`, `indexOf`, `replace`, `split`, `join`
  - 변환: `toString`, `toInt`
  - `length`는 리스트에도 쓸 수 있습니다.
- 평가 오류 `substr: index out of range (...)`, `replace: empty search string`, `toInt: invalid integer: "..."`
- 값: `VBuiltin`. `<fun>`으로 출력되고 타입 이름은 `function`입니다.
- `Eval.builtins`(내장 함수 환경), `Eval.builtinArity`
- 테스트 66개 추가 (총 332개)

### 변경
- `Eval.eval`이 빈 환경 대신 `builtins` 환경에서 평가합니다. `evalIn Map.empty`에는 여전히 내장 함수가 없습니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 내장 함수를 반영했습니다.

## [0.8.0] - 2026-10-28

### 추가
- 문자열 리터럴 `"..."`. 이스케이프 `\"`, `\\`, `\n`, `\t`, `\r`을 지원하고, 한글 같은 유니코드 문자를 쓸 수 있습니다.
- 문자열 연결 `+`. 왼쪽 값이 int면 덧셈, string이면 연결입니다.
- 문자열 비교 `=`, `<>`, `<`, `>`, `<=`, `>=`. 순서 비교는 문자 코드 순서(ordinal)입니다.
- 렉서 오류 `Unterminated string`, `Invalid escape sequence: \<문자>`
- AST: `Str`, 값: `VString`. 리터럴 형태(`"a\nb"`)로 출력되고 타입 이름은 `string`입니다.
- 테스트 42개 추가 (총 266개)

### 변경
- `+`와 `<` `>` `<=` `>=`의 왼쪽 값이 int도 string도 아니면 오류 메시지가 `expected int or string but got <타입>`입니다. 예전에는 `expected int`였습니다.
- 함수를 비교할 때의 오류 메시지가 `expected int, bool, string or list but got function`으로 바뀌었습니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 문자열 문법과 의미를 반영했습니다.

## [0.7.0] - 2026-10-28

### 추가
- 리스트 리터럴 `[]`, `[1; 2; 3]`. 리터럴은 `::`와 `[]`로 바뀝니다.
- cons 연산자 `::`. 오른쪽 결합이고, 비교보다 강하고 `+ -`보다 약합니다.
- `match e with [] -> e1 | h :: t -> e2`. 두 분기는 순서와 상관없고, 첫 `|`는 생략할 수 있습니다.
- 리스트 비교 `=`, `<>`. 원소를 차례로 비교합니다.
- 렉서: 키워드 `match`, `with`, 토큰 `[`, `]`, `;`, `::`, `|`
- AST: `Nil`, `Cons`, `Match`
- 값: `VList`. `[1; 2; 3]`으로 출력되고 타입 이름은 `list`입니다.
- 테스트 53개 추가 (총 224개)

### 변경
- 함수를 비교할 때의 오류 메시지가 `expected int, bool or list but got function`으로 바뀌었습니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 리스트 문법과 의미를 반영했습니다.

## [0.6.0] - 2026-10-27

### 추가
- 람다 `fun x y -> body`. 여러 인자는 중첩 람다(커링)로 바뀝니다.
- 함수 호출 `f x y`. 붙여 쓰는 방식이고, 왼쪽 결합이며, 가장 높은 우선순위입니다.
- 함수 정의 문법 설탕 `let f x y = body in e`
- 재귀 함수 `let rec f x = body in e`
- 클로저(정적 범위), 부분 적용, 값에 의한 호출(call-by-value)
- 렉서: 키워드 `fun`, `rec`, 토큰 `->`
- AST: `Lambda`, `App`, `LetRec`
- 값: `VClosure`, `VRecClosure`. `<fun>`으로 출력되고 타입 이름은 `function`입니다.
- 테스트 46개 추가 (총 171개)

### 변경
- `calc`는 평가를 512MB 스택의 스레드에서 실행합니다. 기본 스택에서는 재귀 깊이 약 7,000에서 프로세스가 죽었지만, 이제 약 100,000까지 동작합니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 함수 문법과 의미를 반영했습니다.

## [0.5.0] - 2026-10-27

### 추가
- 불리언 리터럴 `true`, `false`
- 비교 연산자 `=`, `<>`, `<`, `>`, `<=`, `>=`
  - `=`와 `<>`는 int끼리 또는 bool끼리 비교합니다. 나머지는 int끼리만 비교합니다.
- 논리 연산자 `&&`, `||`(단락 평가), `not`
- `if <식> then <식> else <식>`. `else`는 생략할 수 없습니다.
- 우선순위 (낮은 것부터): `let`/`if` < `||` < `&&` < 비교 < `+ -` < `* /` < 단항 `-`, `not`
- 실행 중 타입 검사: `Type error: expected <int|bool> but got <int|bool>`
- 렉서: 키워드 `if`, `then`, `else`, `true`, `false`, `not`, 연산자 `<>`, `<`, `>`, `<=`, `>=`, `&&`, `||`
- AST: `Bool`, `If`, `Equal`, `NotEqual`, `Less`, `Greater`, `LessEqual`, `GreaterEqual`, `And`, `Or`, `Not`
- 테스트 70개 추가 (총 125개)
- `README.md`: 프로젝트 소개, 언어 기능, 시작하기, 구조, 문서 링크
- `howto/`: 개발 중 해결한 문제를 기록한 문서 6개
  - fsyacc `%nonassoc` 충돌, `let`/`if` 범위와 우선순위, fslex 키워드 순서
  - Expecto와 `dotnet test`, fsproj 버전 관리, 파서용 FsCheck 속성 테스트

### 변경
- 평가기를 `Program.fs`에서 새 모듈 `Eval.fs`로 옮겼습니다.
  - 평가 결과의 타입이 `int`에서 `Value`(`VInt of int | VBool of bool`)로 바뀌었습니다.
  - 환경의 타입이 `Map<string, Value>`로 바뀌었습니다.
  - `Program.eval`, `Program.evalIn` 대신 `Eval.eval`, `Eval.evalIn`을 씁니다.
- `docs/grammar.md`, `docs/testing.md`, `docs/build.md`에 새 문법과 모듈 구성을 반영했습니다.

## [0.4.0] - 2026-10-27

### 추가
- `let` 바인딩: `let <이름> = <식> in <식>`
  - 본문은 오른쪽으로 최대한 이어집니다. `let x = 1 in x + 2`는 `let x = 1 in (x + 2)`입니다.
  - 섀도잉: 안쪽 바인딩이 우선합니다. `let x = 1 in let x = 2 in x`는 `2`입니다.
- 변수 참조. 정의되지 않은 변수를 쓰면 `Unbound variable: <이름>` 오류가 납니다.
- 렉서: 키워드 `let`, `in`, 식별자(`[A-Za-z_][A-Za-z0-9_]*`), `=` 토큰
- AST: `Var of string`, `Let of string * Expr * Expr`
- 평가기: 환경(`Map<string, int>`)을 받는 `evalIn env expr`. `eval`은 빈 환경으로 `evalIn`을 호출합니다.
- 테스트 25개 추가 (총 55개)
- `docs/grammar.md`: 어휘 구조, EBNF, 우선순위, AST 대응, 평가 규칙, 오류 정리

## [0.3.0] - 2026-10-27

### 추가
- Expecto 테스트 프로젝트 `tests/Calc.Tests` (테스트 30개)
  - Lexer: 토큰 순서, 공백 무시, 오류 처리
  - Parser: 우선순위, 결합 방향, 괄호, 단항 마이너스, 파서 오류
  - Eval: 식 계산 표, 0으로 나누기, FsCheck 속성 테스트
- 솔루션 파일 `Calc.slnx`: 루트에서 `dotnet build` / `dotnet test` 실행 가능

## [0.2.0] - 2026-10-27

### 추가
- [Argu](https://fsprojects.github.io/Argu/) 6.2.5로 명령줄 옵션 처리
  - `--expr <expr>` / `-e <expr>` / `--expr=<expr>`: 식 계산
  - `--version` / `-v`: 버전 출력 (`Calc.fsproj`의 `<Version>`을 어셈블리에서 읽음)
  - `--help` / `-h`: 사용법 출력
- 잘못된 옵션을 주면 오류 메시지와 사용법을 출력하고 종료 코드 1로 끝냄

### 변경
- **호환성 깨짐**: 식을 위치 인자로 받지 않습니다. `calc "1 + 2"` 대신 `calc --expr "1 + 2"`를 쓰세요.
  `--expr`가 없으면 이전처럼 표준 입력에서 읽습니다.

## [0.1.0] - 2026-10-27

fslex/fsyacc로 만든 최소 구성의 정수 사칙연산 계산기입니다.

### 추가

**어휘 (Lexer.fsl)**
- 정수 리터럴: `digit+` (음이 아닌 정수, 32비트 `int`)
- 연산자: `+` `-` `*` `/`
- 괄호: `(` `)`
- 공백, 탭, 줄바꿈은 무시

**문법 (Parser.fsy)**
- 이항 연산: `+`, `-`, `*`, `/`
- 단항 마이너스: `-expr`
- 괄호로 묶은 식: `( expr )`
- 연산자 우선순위 (높은 것부터)
  1. 단항 `-`
  2. `*` `/` (왼쪽 결합)
  3. `+` `-` (왼쪽 결합)
- 문법 충돌 없음

**AST (Ast.fs)**
- `Expr` = `Number` | `Add` | `Subtract` | `Multiply` | `Divide` | `Negate`

**평가기 / 실행 (Program.fs)**
- 트리를 순회하며 계산하는 평가기 `eval`
- 정수 나눗셈 (`7 / 2` → `3`)
- 입력: 명령줄 인자 또는 표준 입력
- 출력: AST(`%A` 형식)와 계산 결과
- 오류 처리: 렉서 오류, 파서 오류, 0으로 나누기가 나면 `Error: ...`를 표준 에러로 출력하고 종료 코드 1로 끝냄

**문서**
- `docs/build.md`: 빌드 및 실행 방법
- `docs/testing.md`: 수동 테스트 방법과 기대 결과

### 아직 지원하지 않음
- 변수, `let` 바인딩
- 불리언, 비교 연산, `if-then-else`
- 함수
- 오류 위치(줄/열) 표시
- 자동화된 테스트
