# Calc

F#과 [FsLexYacc](https://github.com/fsprojects/FsLexYacc)(fslex, fsyacc)로 만드는 작은 ML 계열 식(expression) 언어입니다. 렉서, 파서, AST, Hindley–Milner 타입 추론, 평가기를 단계별로 확장하면서 언어 구현을 공부하기 위한 프로젝트입니다.

```
$ dotnet run --project src/Calc -- --expr "let x = 5 in if x > 3 && not (x = 4) then x * 2 else 0"
AST:    Let
  ("x", Number 5,
   If
     (And (Greater (Var "x", Number 3), Not (Equal (Var "x", Number 4))),
      Multiply (Var "x", Number 2), Number 0))
Type:   int
Result: 10
```

## 언어 기능

| 기능 | 예 |
|---|---|
| 정수 사칙연산 (32비트, 정수 나눗셈) | `2 + 3 * (4 - 1)`, `7 / 2`, `-5` |
| 불리언 | `true`, `false` |
| 비교 | `=` `<>` `<` `>` `<=` `>=` |
| 논리 연산 (short-circuit evaluation) | `&&`, `\|\|`, `not` |
| `let` 바인딩 | `let x = 1 + 2 in x * x` |
| 조건식 | `if x > 0 then x else -x` |
| 함수 정의와 호출 (커링, 클로저) | `let add x y = x + y in add 1 2`, `(fun x -> x * 2) 21` |
| 재귀 함수 | `let rec fact n = if n <= 1 then 1 else n * fact (n - 1) in fact 10` |
| 문자열 (`+`로 연결, 비교) | `"Hello, " + name`, `"a" < "b"` |
| 문자열 내장 함수 | `length`, `substr`, `upper`, `split`, `join`, `toInt`, `toString` 등 14개 |
| 리스트·튜플·option·출력 함수 | `map`, `filter`, `fold`, `range`, `sum`, `sort`, `head`, `nth`, `fst`, `snd`, `tryHead`, `defaultValue`, `print`, `println`, `ignore` 등 22개. 이 중 16개는 Calc로 쓴 표준 라이브러리(`src/Calc/prelude.calc`) |
| 리스트 | `[1; 2; 3]`, `0 :: xs` |
| 튜플 | `(1, "a")`, `let (a, b) = p in a + b` |
| algebraic data type, `option` | `type shape = Circle of int \| Rect of int * int in ...`, `Some 1`, `None` |
| 패턴 매칭 (중첩 패턴, `when` 가드) | `match xs with [] -> 0 \| [x] -> x \| x :: _ when x > 0 -> x \| _ -> -1` |
| 최상위 선언 (`in` 없이, 식은 `;;` 뒤에) | `let x = 1`, `let rec f n = ...`, `type t = ...`, `;; f x` |
| `unit`, 순서 실행 `;`, 출력 | `()`, `let f () = ...`, `println "a"; print 1`, `ignore e` |
| 주석 | `// 줄 끝까지`, `(* 블록, (* 중첩 *) 가능 *)` |
| REPL | `calc` (터미널), `# let x = 1;;` → `val x : int = 1` |
| 나머지, 파이프, 합성 | `7 % 3`, `xs \|> map f \|> sum`, `(inc >> dbl) 5` |
| `function`, or 패턴 | `function Some x -> x \| None -> 0`, `\| 1 \| 2 \| 3 -> "small"` |
| mutual recursion | `let rec even n = ... and odd n = ...` |
| 타입 표기 | `(xs : int list)`, `let f (x: int) : string = ...`, `fun (x: 'a) -> x` |
| 예외 | `exception Neg of int`, `raise (Neg 1)`, `failwith "bad"`, `try e with Failure m -> m \| Not_found -> ""` |
| 문자열 포맷 | `sprintf "%d is %s" 42 "x"`, `printfn "%A" xs`, `$"Hello {name}, {age + 1}"` |
| 레코드 | `type point = { x: int; y: int }`, `{ x = 1; y = 2 }`, `p.x`, `{ p with x = 1 }`, `match p with { x = 0 } -> …` |
| 모듈 | `module Stack = struct type 'a t = … let push x s = … end`, `Stack.push`, `open Stack` |
| indentation 문법 (F# light syntax) | 들여쓴 블록이 `in`, `;`, `;;`, `done`, `struct … end`를 대신함. 0열의 새 줄은 새 항목 |
| 배열 | `[\| 1; 2 \|]`, `a.[i]`, `a.[i] <- v`, `Array.length`, `Array.init`, `Array.map` |
| mutable reference, 반복문 | `let r = ref 0 in r := !r + 1`, `incr r`, `for i = 1 to 10 do … done`, `while !n > 0 do … done` |

- 우선순위 (낮은 것부터): `;` < `let`/`if`/`fun`/`match` < `:=` < `||` < `&&` < 비교, `|>`, `>>` < `::` < `+ -` < `* / %` < 단항 `-`, `not` < 함수 호출 < `!` < `.` (필드)
- **타입 추론** (Hindley–Milner, let-polymorphism): 실행 전에 타입을 추론하고, 맞지 않으면 평가하지 않습니다. 타입 오류는 문제가 된 식의 위치(줄, 열, `^`)를 가리킵니다.
  - `fun f x -> f (f x)`는 `('a -> 'a) -> 'a -> 'a`, `[1; true]`는 `Type mismatch: expected int list but got bool list`
  - `--no-typecheck`를 주면 추론을 건너뛰고 평가 중에 동적으로 검사합니다.
  - value restriction: 함수 호출의 결과는 generalize하지 않습니다(`let r = ref []`의 타입은 처음 쓴 곳이 정함, OCaml·SML과 같음).
- **빠진 `match` 분기 경고**: 모든 값을 덮지 못하는 `match`와 `let` 패턴에 대해, 덮지 못하는 값의 예와 위치를 경고합니다(OCaml Warning 8, F# FS0025와 같음).
- **쓰이지 않는 분기 경고**: 앞의 분기들이 이미 덮어서 절대 선택되지 않는 분기를 경고합니다(OCaml Warning 11, F# FS0026과 같음).
- **버리는 값 경고**: `e1; e2`의 `e1`이 `unit`이 아니면 경고합니다(F# FS0020과 같음).
- **타입 변수 경고**: 타입 표기의 `'a`가 다른 타입으로 정해지면 경고합니다(F# FS0064와 같음).
- **tail call**: 함수의 마지막 계산인 호출은 스택을 쓰지 않습니다. `let rec loop n = … loop (n - 1)`은 깊이에 상관없이 돕니다(인터프리터와 컴파일러 모두, Scheme·OCaml과 같음).

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

# 프로그램 파일 실행: 프로그램이 출력한 것만 보여 줌
dotnet run --project src/Calc -- examples/hello.calc

# REPL: 인자 없이 터미널에서 실행 (입력은 ;; 로 끝냄)
dotnet run --project src/Calc
```

```
calc 0.60.0 REPL. End each input with ;;  Exit with #quit;; or Ctrl-D.
# let add a b = a + b;;
val add : int -> int -> int = <fun>
# add 1 2;;
- : int = 3
```

`examples/`에 예제 프로그램이 있습니다(`hello.calc`, `tree.calc`).

```
// examples/hello.calc 일부 (indentation 문법)
let greet name = "Hello, " + name + "!"

let area s =
    match s with
    | Circle r -> 3 * r * r
    | Rect (w, h) -> w * h

println (greet "Calc")
println (map area [Circle 2; Rect (3, 4)])
```

`--` 뒤의 인자는 `dotnet run`이 아니라 Calc 프로그램에 전달됩니다.

| 옵션 | 설명 |
|---|---|
| (인자 없음) | 터미널에서 실행하면 REPL을 띄웁니다. 표준 입력이 파이프면 그 식을 계산합니다. |
| `--repl` | 표준 입력이 파이프여도 REPL로 실행합니다. |
| `<file>` | 프로그램 파일을 실행합니다. 프로그램이 출력한 것과, `()`가 아닌 마지막 값만 출력합니다. |
| `--expr <expr>`, `-e <expr>` | 식을 계산하고 `AST:`, `Type:`, `Result:`를 출력합니다. 생략하면 표준 입력에서 읽습니다. |
| `--version`, `-v` | 버전을 출력합니다. |
| `--no-typecheck` | 타입 추론을 건너뛰고 평가합니다. |
| `--compile <path>`, `--emit-mlir` | 네이티브 실행 파일을 만들거나 MLIR을 출력합니다. 실험 중이며 내장 함수를 포함한 모든 기능을 지원합니다. 결과와 오류 메시지는 인터프리터와 같습니다([설계](docs/compiler-design.md)). |
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
│   ├── Modules.fs            # 모듈을 M.f 같은 이름의 최상위 선언으로 펼침 (파서 직후)
│   ├── Eval.fs               # 평가기 (Value, Env, eval)
│   ├── Infer.fs              # Hindley–Milner 타입 추론 (typecheck)
│   ├── Core.fs               # 컴파일러: 중간 표현(Core IR)
│   ├── Lower.fs              # 컴파일러: AST → Core IR, 클로저 변환
│   ├── Codegen.fs            # 컴파일러: Core IR → MLIR 텍스트
│   ├── Toolchain.fs          # 컴파일러: mlir-opt, mlir-translate, clang 실행
│   ├── Frontend.fs           # 파싱, 큰 스택 실행, 오류 위치 (명령줄과 REPL 이 함께 씀)
│   ├── Prelude.fs            # 프로그램 앞에 표준 라이브러리 정의를 붙임
│   ├── prelude.calc          # Calc 로 쓴 표준 라이브러리 (map, fold, head, ...)
│   ├── Repl.fs               # REPL
│   ├── runtime/              # 컴파일한 프로그램의 C 런타임 (Boehm GC, 내장 함수, Unicode 표)
│   ├── Parser.fsy            # fsyacc 문법 → Parser.fs (빌드 시 생성)
│   ├── Lexer.fsl             # fslex 규칙 → Lexer.fs (빌드 시 생성)
│   ├── LayoutFilter.fs       # indentation 문법: 렉서와 파서 사이에서 가상 토큰을 넣음
│   └── Program.fs            # 명령줄 처리 (Argu)
├── tests/Calc.Tests/         # Expecto + FsCheck 테스트
├── docs/                     # 빌드, 테스트, 문법 문서
├── benchmarks/               # Python, 인터프리터, 컴파일러 성능 비교 (python3 benchmarks/run.py)
├── examples/                 # 예제 프로그램 (calc examples/hello.calc)
├── howto/                    # 문제 해결 기록
├── CHANGELOG.md
└── LICENSE                   # MIT
```

처리 흐름:

```
문자열 → Lexer.tokenize → LayoutFilter → Parser.start (Modules.resolveProgram) → Ast.Expr → Prelude.wrap (표준 라이브러리) → Infer.typecheck (타입) → Eval.eval → Value
```

`Lexer.fs`와 `Parser.fs`는 빌드할 때 `.fsl`과 `.fsy`에서 생성되며 git에 포함되지 않습니다. 직접 수정하지 말고 `.fsl`, `.fsy` 파일을 고치세요.

## 문서

| 문서 | 내용 |
|---|---|
| [docs/build.md](docs/build.md) | 빌드, 실행, 빌드 과정과 파일 순서, 문법 충돌 확인 |
| [docs/testing.md](docs/testing.md) | 자동 테스트(Expecto) 실행과 추가 방법, 수동 테스트 표 |
| [docs/grammar.md](docs/grammar.md) | 토큰, EBNF, 우선순위, AST 대응, 평가 규칙, 오류 |
| [docs/compiler-design.md](docs/compiler-design.md) | MLIR/LLVM 네이티브 컴파일 설계와 단계별 계획 (단계 7 완료) |
| [docs/compiler-architecture.md](docs/compiler-architecture.md) | 컴파일러 구조와 흐름: 예제 하나로 따라가는 MLIR → LLVM IR → 실행 파일, entry point `main`, 런타임 링크 |
| [docs/benchmarks.md](docs/benchmarks.md) | 예제 다섯 개로 Python, 인터프리터, 컴파일러의 출력과 실행 시간 비교 |
| [howto/](howto/README.md) | 개발하면서 부딪힌 문제와 해결 방법 |
| [CHANGELOG.md](CHANGELOG.md) | 버전별 변경 사항 |

## 버전

버전은 [Semantic Versioning](https://semver.org/lang/ko/)을 따르며, 다음 세 곳을 항상 같게 유지합니다.

1. `src/Calc/Calc.fsproj`의 `<Version>`. `calc --version`이 이 값을 출력합니다.
2. `CHANGELOG.md`의 버전 항목
3. git 태그 `vX.Y.Z`

## 라이선스

[MIT](LICENSE)
