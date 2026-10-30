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

### 빠른 실행 파일 (ReadyToRun)

`dotnet build`의 결과는 실행할 때마다 JIT이 메서드를 컴파일합니다. 빈 프로그램에서도 1,500개쯤입니다(prelude를 파싱하는 파서의 메서드가 그중 600개). ReadyToRun으로 publish하면 미리 컴파일해 둡니다. 실행할 컴퓨터의 런타임 식별자(RID)를 줍니다.

```sh
dotnet publish src/Calc -c Release -r osx-arm64 -p:PublishReadyToRun=true -o dist   # Linux x64 는 -r linux-x64
./dist/calc prog.calc
```

- **효과:** 빈 프로그램이 0.071초에서 0.035초가 됩니다. 첫 실행의 JIT 데우기도 줄어 `benchmarks/fib.calc`는 0.15초에서 0.11초가 됩니다(Apple M4 Max). 자세한 수치는 [benchmarks.md](benchmarks.md)에 있습니다.
- **크기:** `Calc.dll`이 0.9MB에서 2.3MB가 됩니다.
- **같은 동작:** 결과와 `--compile`은 같습니다. 테스트는 `dotnet build` 결과로 돕니다.

## 실행

```sh
# --expr 로 식 전달
dotnet run --project src/Calc -- --expr "2 + 3 * (4 - 1)"

# 표준 입력으로 식 전달 (--expr 생략)
echo "1 + 2 * 3" | dotnet run --project src/Calc

# 프로그램 파일 실행
dotnet run --project src/Calc -- examples/hello.calc
```

- **`-e`와 표준 입력:** 학습용 출력입니다. `AST:`, `Type:`, `Result:`를 차례로 출력하고, 프로그램이 `print`로 출력한 것은 `Type:`과 `Result:` 사이에 나옵니다.
- **REPL(인자 없이 터미널에서, 또는 `--repl`):** OCaml toplevel, `dotnet fsi`처럼 입력마다 결과를 보여 줍니다(아래 "REPL").
- **파일 실행(`calc prog.calc`):** OCaml의 `ocaml prog.ml`, F#의 `dotnet fsi prog.fsx`처럼 프로그램이 출력한 것만 보여 줍니다. 마지막 값이 `()`가 아니면 그 값을 한 줄 더 출력합니다. 경고와 오류는 표준 에러로 나갑니다.

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
| `--version` | `-v` | 버전을 출력하고 종료합니다 (예: `calc 0.63.1`). |
| `--no-typecheck` | | 타입 추론을 건너뛰고 바로 평가합니다. 타입은 평가 중에 동적으로 검사됩니다. |
| `--emit-mlir` | | 평가하지 않고, 컴파일러가 만든 MLIR을 출력합니다. |
| `--compile <path>` | | 평가하지 않고, 네이티브 실행 파일을 `<path>`에 만듭니다. `--compile=<path>` 형식도 됩니다. |
| `--keep-temps` | | `--compile`과 함께 쓰면 중간 파일(`.mlir`, `.ll`, `.o`)을 지우지 않고 폴더 경로를 출력합니다. |
| `--repl` | | REPL을 띄웁니다. 인자 없이 터미널에서 실행해도 REPL이 뜨므로, 표준 입력이 파이프일 때 씁니다. |
| `<file>` | | 프로그램 파일을 실행합니다(옵션이 아닌 마지막 인자). `--compile`과 함께 쓰면 그 파일을 컴파일합니다. `--expr`이 있으면 `--expr`이 우선입니다. |
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
Ast.fs → Errors.fs → Eval.fs → Infer.fs → Core.fs → Lower.fs → Codegen.fs → Toolchain.fs → Parser.fsi → Parser.fs → Lexer.fs → Frontend.fs → Prelude.fs → Repl.fs → Program.fs
```

F#은 앞에 나온 파일만 참조할 수 있습니다. 그래서 순서가 중요합니다.

- `Errors`는 렉서와 파서가 함께 쓰는 `SyntaxError` 예외를 정의합니다. 그래서 `Parser`와 `Lexer`보다 앞에 있습니다.
- `Eval`은 `Ast`의 `Expr`를 평가합니다.
- `Infer`는 `Ast`의 `Expr` 타입을 추론합니다. `Eval`과는 서로 참조하지 않습니다.
- `Program`은 파싱한 다음 `Infer.typecheck`로 타입을 확인하고 `Eval.eval`로 평가합니다.
- `Lower`는 `Ast.Expr`를 Core IR로 바꾸고(closure conversion), `Codegen`은 Core IR을 MLIR 텍스트로 만들고, `Toolchain`은 외부 도구로 실행 파일을 만듭니다.
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

## REPL

인자 없이 터미널에서 `calc`를 실행하면 REPL이 뜹니다. 표준 입력이 파이프일 때는 `--repl`을 씁니다.

```
$ dotnet run --project src/Calc
calc 0.63.1 REPL. End each input with ;;  Exit with #quit;; or Ctrl-D.
# let rec fact n =
    if n = 0 then 1 else n * fact (n - 1);;
val fact : int -> int = <fun>
# fact 10;;
- : int = 3628800
# type shape = Circle of int | Rect of int * int;;
type shape
# let (a, b) = (1, "s");;
val a : int = 1
val b : string = "s"
# a + true;;
Error: Type mismatch: expected int but got bool (line 1, column 5)
  1 | a + true;;
    |     ^
# #quit;;
```

- **입력 끝:** 줄 끝(공백 제외)이 `;;`인 곳까지가 입력 하나입니다. 여러 줄에 걸쳐 쓸 수 있고, 이어 쓰는 줄의 프롬프트는 공백입니다. 문자열이나 주석 안의 줄 끝 `;;`도 입력 끝으로 봅니다.
- **입력 하나:** 프로그램 파일과 같은 문법입니다(최상위 선언, `;;`로 구분한 식). 항목마다 결과를 보여 줍니다.
  - 식은 `- : <타입> = <값>`으로 보여 줍니다.
  - 선언은 `val <이름> : <타입> = <값>`, 타입 선언은 `type <이름>`으로 보여 줍니다.
  - `let () = …`는 아무것도 보여 주지 않고, `let _ = e`는 `- : …`로 보여 줍니다.
- **정의 유지:** 앞에서 정의한 이름, 함수, 타입, 생성자는 다음 입력에서도 보입니다. 같은 이름을 다시 정의하면 앞의 것을 가립니다.
  - 타입은 지금까지의 선언을 앞에 붙여 다시 추론합니다.
  - 값은 이어서 쓰므로 앞 입력의 `print`가 되풀이되지 않습니다.
- **출력과 경고:** `print`의 출력은 결과 줄보다 먼저 나옵니다. 빠진 분기 경고와 버리는 값 경고는 그 입력에서 처음 생긴 것만 보여 줍니다.
- **오류:** 위치를 보여 주고 다음 입력을 기다립니다. 오류가 난 항목과 그 뒤 항목의 정의는 남지 않습니다.
- **끝내기:** `#quit;;` 또는 Ctrl-D(입력 끝)입니다. `;;` 없이 끝난 마지막 입력도 처리합니다.

## 네이티브 컴파일 (실험 중)

`--compile`은 MLIR과 LLVM으로 실행 파일을 만듭니다. 설계와 단계별 계획은 [compiler-design.md](compiler-design.md)에 있습니다. 지금은 **모든 기능**(정수, 불리언, `if`, `let`, 함수와 클로저, 문자열, 리스트, 튜플, 패턴 매칭, ADT, `option`, `unit`과 `;`, 최상위 선언, 타입 표기, 예외, 문자열 포맷, 내장 함수 21개와 Calc로 쓴 prelude 함수 17개)을 컴파일합니다. 실행 파일은 `print`가 출력한 것을 내보내고, 마지막 값이 `()`가 아니면 그 값을 출력합니다(`calc prog.calc`와 같음). 실행 결과와 실행 중 오류(`Error: Attempted to divide by zero.`, 종료 코드 1)는 인터프리터와 같습니다. 앞으로 언어에 기능을 더하면, 컴파일러가 따라잡기 전까지는 `Error: Not supported by the compiler yet: <기능>`으로 멈춥니다.

### 준비

```sh
brew install llvm bdw-gc
```

Homebrew LLVM은 PATH에 넣지 않아도 됩니다. 컴파일러가 도구를 찾는 순서는 다음과 같습니다.

1. 환경 변수 `CALC_LLVM_BIN` (`mlir-opt`, `mlir-translate`, `clang`가 있는 폴더)
2. `/opt/homebrew/opt/llvm/bin`
3. `PATH`

Boehm GC 위치는 `CALC_GC_PREFIX`(기본 `/opt/homebrew/opt/bdw-gc`)로 바꿀 수 있습니다. 도구가 없으면 무엇이 없는지와 설치 방법을 출력합니다.

### 사용

```sh
dotnet run --project src/Calc -- --compile ./answer -e "42"
./answer                      # 42

dotnet run --project src/Calc -- --emit-mlir -e "42"     # 생성한 MLIR 보기
```

컴파일 과정은 다음과 같습니다. `--keep-temps`를 주면 중간 파일을 볼 수 있습니다.

1. `mlir-opt`: scf/cf/arith → llvm dialect
2. `mlir-translate`: LLVM IR
3. `clang -c`: 오브젝트 파일
4. `clang`: 링크. C 런타임 오브젝트와 `-lgc`를 함께 링크합니다.

C 런타임(`runtime/runtime.c`, `builtins.c`)은 한 번만 컴파일해서 임시 폴더에 `calc-<파일>-<해시>.o`로 둡니다. 해시는 런타임의 모든 `.c`·`.h` 파일, `clang` 경로, GC 위치로 만들어서, 이 중 하나가 바뀌면 다시 컴파일합니다.

`upper`, `lower`, `trim`이 쓰는 Unicode 표(`runtime/unicode.h`)는 .NET에서 생성합니다. .NET을 올린 뒤 테스트 `Unicode 표`가 실패하면 다시 만듭니다.

```sh
dotnet fsi src/Calc/runtime/gen-unicode.fsx
```

