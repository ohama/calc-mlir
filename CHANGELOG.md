# Changelog

이 파일은 버전별 변경 사항을 기록합니다. 형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/)를,
버전 번호는 [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.
버전은 `src/Calc/Calc.fsproj`의 `<Version>`에서 관리합니다.

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
