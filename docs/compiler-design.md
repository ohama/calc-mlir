# 컴파일러 설계 (MLIR/LLVM)

Calc 언어를 인터프리터(`Eval`) 대신 **네이티브 실행 파일로 컴파일**하기 위한 설계 문서입니다. 구현은 간단한 것부터 단계별로 진행합니다(8장). 각 단계는 그 자체로 동작하고 테스트되는 상태로 끝납니다.

**진행 상황**: 단계 7 완료. 내장 함수 36개까지 모든 기능을 컴파일합니다. 타입 검사를 통과하는 `EvalTests.cases` 263개와 실행 중 오류 예제가 모두 인터프리터와 같아서, 단계 8(완성도)의 기준도 함께 채웠습니다. 남은 것은 단계 9(최적화, 선택)입니다.

지금 코드가 실제로 하는 일(예제 하나로 따라가는 흐름, entry point, 런타임 링크)은 [compiler-architecture.md](compiler-architecture.md)에 있습니다.

이 문서의 MLIR 예제와 도구 동작은 모두 아래 환경에서 직접 실행해서 확인했습니다.

## 1. 목표와 범위

- **목표**: `calc --compile out -e "<식>"`이 실행 파일 `out`을 만들고, `./out`이 인터프리터와 **같은 결과와 같은 오류 메시지**를 출력합니다.
- **기준 semantics는 인터프리터(`Eval`)** 입니다. 컴파일러는 새 의미를 만들지 않습니다. 동작이 다르면 컴파일러 버그입니다.
- 타입 추론(`Infer.typecheck`)을 통과한 프로그램만 컴파일합니다. 그래서 생성 코드는 실행 중 타입 오류를 다루지 않아도 됩니다.
- **범위 밖** (처음에는): 따로 컴파일한 모듈의 링크, 디버그 정보, Windows/Linux 지원(macOS arm64 먼저), 정확한(precise) GC.

## 2. 준비: 필요한 도구 설치

```sh
brew install llvm bdw-gc
```

| 도구 | 확인한 버전 | 용도 |
|---|---|---|
| Homebrew LLVM (`mlir-opt`, `mlir-translate`, `clang`, `llc`, `opt`) | 22.1.7 | MLIR lowering, LLVM IR 변환, 링크 |
| Boehm GC (`bdw-gc`) | 8.2.12 | 런타임의 메모리 할당과 garbage collection (`GC_MALLOC`) |
| .NET SDK | 10.0 | 컴파일러 자체 (기존과 같음) |

- Homebrew LLVM은 macOS 기본 `clang`과 겹치지 않도록 **PATH에 들어가지 않습니다**(keg-only). 컴파일러는 다음 순서로 도구 위치를 찾습니다.
  1. 환경 변수 `CALC_LLVM_BIN`
  2. `/opt/homebrew/opt/llvm/bin`
  3. `PATH`
- Boehm GC도 같은 방식으로 `CALC_GC_PREFIX`를 먼저 보고, 없으면 `/opt/homebrew/opt/bdw-gc`를 씁니다.
- 설치 확인:

  ```sh
  /opt/homebrew/opt/llvm/bin/mlir-opt --version     # Homebrew LLVM version 22.1.7
  ls /opt/homebrew/opt/bdw-gc/include/gc.h
  ```

**MLIR C API는 쓰지 않습니다.** Homebrew의 `libMLIR.dylib`에는 C API 심볼(`mlirContextCreate` 등)이 없습니다(`nm`으로 확인). C API를 쓰려면 LLVM을 소스에서 직접 빌드해야 합니다. 그래서 MLIR을 **텍스트로 출력**하고 명령줄 도구로 처리합니다(4장 D1).

## 3. 전체 구조

```
문자열
  → Lexer / Parser            (기존)  Ast.Expr
  → Infer.typecheck           (기존)  타입이 맞지 않으면 여기서 멈춤
  → Lower.fs                  (새로)  Ast.Expr → Core IR: 자유 변수 분석, 클로저 변환, 패턴 매칭 풀기
  → Codegen.fs                (새로)  Core IR → MLIR 텍스트 (.mlir)
  → mlir-opt                  (외부)  scf/cf/arith → llvm 방언으로 낮추기
  → mlir-translate            (외부)  llvm 방언 → LLVM IR (.ll)
  → clang -c                  (외부)  오브젝트 (.o)
  → clang + runtime.o + libgc (외부)  실행 파일 (runtime.o 는 한 번 컴파일해서 캐시)
```

```sh
mlir-opt prog.mlir --convert-scf-to-cf --convert-cf-to-llvm --convert-arith-to-llvm \
         --reconcile-unrealized-casts -o prog.llvm.mlir
mlir-translate --mlir-to-llvmir prog.llvm.mlir -o prog.ll
clang -O2 -Wno-override-module -c prog.ll -o prog.o
clang -O2 -c runtime.c -I/opt/homebrew/opt/bdw-gc/include -o runtime.o     # 한 번만 (캐시). builtins.c 도 같음
clang prog.o runtime.o builtins.o -L/opt/homebrew/opt/bdw-gc/lib -lgc -Wl,-stack_size,0x20000000 -o prog
```

### 새 명령줄 옵션

| 옵션 | 동작 |
|---|---|
| `--emit-mlir` | 생성한 MLIR 텍스트를 표준 출력으로 보내고 끝냄 (디버깅, 골든 테스트용) |
| `--compile <path>` | 실행 파일 `<path>`를 만듦. 중간 파일은 임시 디렉터리에 둠 |
| `--keep-temps` | 중간 파일(`.mlir`, `.ll`, `.o`)을 지우지 않고 경로를 출력 |

`--compile`은 `--no-typecheck`와 함께 쓸 수 없습니다(1장).

### 새 파일

| 파일 | 내용 |
|---|---|
| `src/Calc/Core.fs` | Core IR 타입 (5장), `NotSupported`, free variable |
| `src/Calc/Lower.fs` | `Ast.Expr` → Core IR: 연산을 `Prim`으로, `&&`/`||`를 `if`로, closure conversion |
| `src/Calc/Codegen.fs` | Core IR → MLIR 텍스트 |
| `src/Calc/Toolchain.fs` | 도구 찾기, 외부 프로세스 실행, 오류 전달 |
| `src/Calc/runtime/runtime.c`, `runtime.h` | C 런타임 (6장): 할당, 출력(`calc_buffer_value`), 오류, polymorphic 연산, `calc_apply`, option 전역. 빌드 출력 폴더의 `runtime/`으로 복사 (`CopyToOutputDirectory`) |
| `src/Calc/runtime/builtins.c` | 내장 함수 36개 (단계 7) |
| `src/Calc/runtime/unicode.h`, `gen-unicode.fsx` | .NET 대소문자 변환과 공백 표 (`upper`, `lower`, `trim`). `dotnet fsi src/Calc/runtime/gen-unicode.fsx`로 다시 만듦 |
| `tests/Calc.Tests/CompileTests.fs` | differential testing (9장) |

컴파일 순서: `Ast → Errors → Eval → Infer → (Core → Lower) → Codegen → Toolchain → Parser → Lexer → Program`

## 4. 핵심 결정

### D1. MLIR을 텍스트로 출력한다

| 방법 | 장점 | 단점 |
|---|---|---|
| **MLIR 텍스트 + `mlir-opt`/`mlir-translate` (선택)** | 설치한 도구만으로 됨. 출력을 눈으로 보고 테스트로 비교 가능 | 외부 프로세스 실행 비용, 오류 메시지가 도구에서 옴 |
| MLIR C API를 P/Invoke | 프로세스 실행 없음, 생성 중 검증 | Homebrew에 C API가 없어 LLVM 소스 빌드 필요 |
| LLVM IR 텍스트를 바로 출력 (MLton 방식) | 단계가 하나 적음 | MLIR을 쓰지 않음. 식으로 된 `if`의 결과를 phi로 직접 만들어야 함 |

나중에 C API로 바꾸더라도 `Codegen`이 만드는 내용은 같습니다. 출력 방식만 바뀝니다.

### D2. dialect: 함수는 `llvm.func`, 본문은 `arith`/`scf`/`llvm`

- **모든 함수를 `llvm.func`로 만듭니다.** 클로저는 함수 포인터가 필요한데, `llvm.mlir.addressof`는 `func.func`를 가리킬 수 없습니다.

  ```
  error: 'llvm.mlir.addressof' op must reference a global defined by 'llvm.mlir.global',
         'llvm.mlir.alias' or 'llvm.func' or 'llvm.mlir.ifunc'
  ```

- 본문에서는 `arith`(정수 연산), `scf.if`(값을 내는 `if`), `llvm`(메모리, 호출)을 섞어 씁니다. `llvm.func` 안의 `arith`와 `scf`도 위의 lowering 패스로 변환되는 것을 확인했습니다.
- `match`처럼 분기가 여럿이고 중간에 빠져나가는 흐름은 `cf.br`/`cf.cond_br`와 **블록 인자**로 만듭니다. 블록 인자가 LLVM의 phi 역할을 하므로 phi를 직접 만들 필요가 없습니다.
- 사용자 dialect(`calc.closure` 같은)는 C++ 빌드가 필요하므로 마지막 단계에서 검토합니다(8장 단계 9).

### D3. value representation: tagged 64비트 단어

인터프리터의 `Eval.Value`를 그대로 옮긴 **균일 표현**입니다. 모든 값은 `i64` 하나입니다.

| 값 | 표현 | 하위 3비트 |
|---|---|---|
| `int n` (32비트) | `(n << 3) \| 1` | `001` |
| `bool b` | `(b << 3) \| 3` | `011` |
| `[]` | `5` | `101` |
| `()` (unit) | `7` | `111` |
| 그 밖의 모든 값 | 힙 블록 포인터 (8-byte alignment) | `000` |

힙 블록은 첫 단어가 **헤더**(종류, 필드 수)입니다.

| 종류 | 필드 |
|---|---|
| `STRING` | 길이, UTF-16 code unit 배열. 헤더는 `2 \| (뒤따르는 단어 수 << 8)`. 상수는 8-byte aligned 전역(`llvm.mlir.global`, GC 대상 아님), 연결 결과는 `GC_MALLOC_ATOMIC`(안에 포인터가 없어 GC가 내용을 훑지 않음) |
| `CONS` | head, tail. 헤더 `3 \| (2 << 8)`. `[]`는 블록이 아니라 immediate value `5` |
| `TUPLE` | 원소 n개. 헤더 `4 \| (n << 8)` |
| `DATA` | 생성자 이름(C 문자열 전역의 포인터), 인자(있으면). 헤더 `5 \| (1 또는 2 << 8)`. 인자 없는 생성자는 8-byte aligned 전역 상수 블록. 처음 설계한 descriptor(이름, 타입 이름, 번호) 대신 이름만 둡니다. 타입 검사를 통과했으므로 같은 타입끼리만 비교하고, 인터프리터도 생성자를 이름으로 비교하기 때문입니다 |
| `CLOSURE` | 코드 포인터, 캡처한 값들 |

**이 표현을 고른 이유**

- **polymorphism이 그냥 동작합니다.** `let id x = x`는 어떤 타입이든 `i64` 하나를 받으므로 코드가 하나면 됩니다. monomorphization(MLton, Roc 방식)은 클로저와 함께 구현하기 어렵습니다. 그래서 최적화 단계로 미룹니다.
- **실행 중에 값의 종류를 알 수 있습니다.** `toString : 'a -> string`, polymorphic `=`, `sort`, 결과 출력(`formatValue`)은 타입 정보 없이 값만 보고 동작해야 합니다. OCaml처럼 int와 bool을 같은 표현으로 두면 `toString true`가 `"1"`이 됩니다. 그래서 둘의 태그를 나눴습니다.
- **Boehm GC와 맞습니다.** conservative GC는 8-byte aligned 값을 포인터로 보고 따라갑니다. int와 bool은 하위 비트가 1이라 포인터와 겹치지 않습니다.
- **32비트 int의 의미가 그대로 유지됩니다.** 태그를 떼고 `i32`로 계산한 뒤(한 바퀴 돎) 다시 붙입니다.
- **문자열은 UTF-16입니다.** 인자에서 `length "😀"`가 2인 것처럼, .NET과 같은 결과를 내기 위해서입니다. 출력할 때 UTF-8로 바꿉니다.

### D4. 함수와 클로저: 모든 함수는 인자 하나짜리 클로저

- 이 언어의 함수는 원래 인자가 하나입니다(`Lambda of string * Expr`, 커링). 그래서 **모든 함수 값은 클로저**이고, calling convention은 하나입니다.

  ```
  llvm.func @lambda_N(%closure: !llvm.ptr, %arg: i64) -> i64
  ```

- **closure conversion**(`Lower`): `fun x -> body`의 free variable을 구해서 클로저 블록에 저장합니다. 본문은 최상위 함수 `@lambda_N`이 되고, free variable을 클로저에서 꺼냅니다.
- **`let rec f x = ...`**: 클로저 포인터 `%closure`가 바로 자기 자신입니다. 그래서 본문 안의 `f`는 `%closure`가 됩니다. 인터프리터의 `VRecClosure`(호출할 때 자기 자신을 넣음)와 같은 생각입니다.
- **호출** `f a`: 클로저의 0번 칸에서 코드 포인터를 꺼내 `llvm.call %code(%f, %a)`로 indirect call합니다.
- 여러 인자 호출 `add 1 2`는 중간 클로저를 만듭니다. 느리지만 단순합니다. 대상이 알려진 direct call과 인자 여러 개 한 번에 받기(uncurrying)는 최적화 단계에서 합니다.

### D5. 메모리: Boehm GC

- 런타임의 `calc_alloc(bytes)`가 `GC_MALLOC`을 부릅니다. 생성 코드는 해제 코드를 만들지 않습니다.
- 확인한 결과: 64바이트 블록 1천만 개(약 640MB)를 할당하는 C 프로그램의 최대 상주 메모리가 3MB였습니다. garbage collection이 동작합니다.
- 참조 카운트(Roc, Lean)나 정확한 GC는 범위 밖입니다. 표현(D3)은 나중에 정확한 GC로 바꿀 수 있게 헤더를 둡니다.

### D6. 코드 생성은 타입 없이, 런타임 태그로 분기

- 처음에는 인터프리터처럼 **타입을 보지 않고** 코드를 만듭니다. `+`, `=`, `<`, `toString`, `length`는 런타임 함수(`calc_add`, `calc_eq`, `calc_compare`, ...)가 태그를 보고 동작을 고릅니다.
- 타입 추론을 통과했으므로 이 함수들은 타입 오류를 만나지 않습니다.
- 정수 사칙연산(`-`, `*`, `/`, 단항 `-`)처럼 타입이 하나뿐인 연산은 처음부터 인라인 `arith`로 만듭니다.
- 타입을 이용한 specialization(int `+`를 인라인으로)은 최적화 단계에서 합니다. 그러려면 `Infer`가 식마다 타입을 기록하도록 바꿔야 합니다.

### D7. 패턴 매칭: 인터프리터와 같은 순서로 검사

- 분기마다 "패턴 검사 → 가드 → 본문" 순서로 검사하고, 실패하면 다음 분기로 갑니다. `Eval.matchPattern`을 코드로 펼친 것입니다.
- **구현(단계 5)은 `cf` 블록 대신 `scf.if`만 씁니다.**
  - 분기마다 조건(패턴 검사와 가드)을 먼저 `i1`로 계산합니다. 가드는 패턴이 일치할 때만 `scf.if` region 안에서 계산합니다.
  - 그다음 `scf.if 조건 { 이름 묶기; 본문 } else { 나머지 분기 }`를 만듭니다. 나머지 분기는 한 번만 생성되므로 코드 크기가 분기 수에 비례합니다.
  - 이름 묶기(필드 읽기)는 가드용과 본문용으로 두 번 생성됩니다. 읽기뿐이라 비용은 작지만, 사이에 런타임 호출(예: 가드의 `calc_compare`)이 있으면 LLVM은 메모리가 바뀌었을 수 있다고 보고 두 번째 읽기를 남깁니다(`clang -O2` 결과로 확인).
  - 하위 패턴은 앞이 일치할 때만 검사하고, 필드도 그때 읽습니다(`x :: rest`는 cons인지 먼저 봄).
  - 어떤 값과도 일치하는 분기(변수, `_`, 그것들로 된 튜플)에서는 검사 코드를 만들지 않고, 그 뒤 분기도 생성하지 않습니다.
- 마지막 분기까지 실패하면 `calc_match_failure(value)`를 호출합니다. 런타임은 `Error: Match failure: <값>`을 출력하고 종료 코드 1로 끝납니다.
- decision tree 컴파일(Maranget)은 최적화 단계로 미룹니다.

- **or 패턴(0.27.0):** 검사는 `scf.if`로 "왼쪽이 맞으면 참, 아니면 오른쪽 검사"를 만듭니다. 이름 묶기는 묶는 이름 수만큼 값을 내는 `scf.if`(`%v:2 = scf.if … -> (i64, i64)`)로, 맞은 쪽에서 값을 꺼냅니다.
- **`let rec … and …`(0.27.0):** `Lower`가 `CLetRecGroup`을 만듭니다. `Codegen`은 그룹의 클로저 블록을 모두 먼저 할당하고, 다른 함수를 가리키는 칸은 그다음에 채웁니다(서로를 가리키므로 한 번에 만들 수 없음). 자기 자신은 단일 `let rec`처럼 `%closure`입니다.
- **`%`:** `arith.remsi`이고, `/`와 같은 두 검사(0, `MinValue % -1`)를 앞에 둡니다.

### D8. 내장 함수: C 런타임 + 클로저

- 내장 함수 36개는 `runtime/builtins.c`의 C 함수 `calc_builtin_<이름>`로 구현합니다(단계 7). 인자 순서, 결과, 오류 메시지는 `Eval.callBuiltin`과 같습니다.
- **호출:** `Lower`가 변수 범위를 따라가서, 범위에 없는 이름만 내장 함수로 봅니다. `let length = ...`처럼 가린 이름은 보통 변수입니다.
  - 인자가 다 모인 호출(`substr s 1 3`)은 런타임 함수를 바로 부릅니다(`CBuiltin`). 남는 인자는 결과에 차례로 적용합니다.
  - 값으로 쓰거나 partial application을 하면(`map length xs`, `substr "abc"`), 인자를 하나씩 받는 클로저 `fun $b0 -> fun $b1 -> ...`가 됩니다. 인터프리터의 `VBuiltin`과 같게 `<fun>`으로 출력되고, `=` 비교 오류도 같습니다. 처음 설계한 "정적 클로저 객체"는 필요 없었습니다. closure conversion이 바깥 인자를 캡처하기 때문입니다.
- **higher-order function:** `map`, `filter`, `fold`, `exists`, `forall`, `tryFind`는 C에서 `calc_apply(closure, arg)`로 사용자 클로저를 부릅니다. 앞에서부터 부르고, `exists`, `forall`, `tryFind`는 결과가 정해지면 멈춥니다.
- **option:** `Some`/`None`의 이름과 `None` 값은 런타임 전역(`calc_name_some`, `calc_name_none`, `calc_none`)입니다. 그래야 `tryHead`가 만든 값과 컴파일한 패턴의 이름 포인터 비교가 맞습니다.
- **`upper`, `lower`, `trim`:** .NET의 `ToUpperInvariant`, `ToLowerInvariant`, `Char.IsWhiteSpace`를 code point마다 표로 만든 `runtime/unicode.h`를 씁니다. 표는 `gen-unicode.fsx`가 만들고, 테스트가 지금 .NET과 같은지 확인합니다.
- **`toString`과 출력:** 같은 코드를 씁니다. 런타임의 `calc_buffer_value`(UTF-16 버퍼에 `Eval.formatValue` 형식으로)입니다.
- `Some`과 `None`, 사용자 생성자는 단계 6에서 정적 클로저가 아니라 `Lower`가 처리합니다. 바로 적용한 `Some 1`은 그 자리에서 `DATA` 블록을 만들고, 값으로 쓴 `Some`은 클로저 `fun x -> Some x`가 됩니다. 생성자 이름은 `llvm.mlir.global` C 문자열 상수입니다.

### D9. 오류, 정수 의미, 스택

인터프리터와 메시지가 같아야 differential testing이 가능합니다.

| 상황 | 런타임 동작 |
|---|---|
| 0으로 나누기 | `Error: Attempted to divide by zero.`, 종료 코드 1 |
| `Int32.MinValue / -1` | LLVM의 `sdiv`는 이 경우가 undefined behavior(UB)이므로 **반드시 미리 검사**. 인터프리터와 같은 `Error: Arithmetic operation resulted in an overflow.` |
| `+`, `-`, `*` 오버플로 | `i32` 연산이라 한 바퀴 돎 (인터프리터와 같음) |
| `head []`, `nth`, `substr`, `toInt`, `Match failure` | 인터프리터와 같은 메시지 |
| 함수끼리 `=` | 인터프리터와 같은 `Type error: ...` (타입 추론이 허용하는 유일한 실행 중 타입 오류) |
| 깊은 재귀 | 링크할 때 메인 스레드 스택을 512MB로 키움(`-Wl,-stack_size,0x20000000`, macOS). 인터프리터의 큰 스택 스레드와 같은 크기이고 재귀 깊이 100만을 확인함. tail call은 최적화 단계 |

### D10. 결과 출력

실행 파일은 마지막에 `calc_print_result(value)`로 결과를 출력합니다. 형식은 `Eval.formatValue`와 같습니다(`[1; 2]`, `Some (-1)`, `"a\n"`, `<fun>`). `AST:`, `Type:`, `Result:` 접두어는 붙이지 않습니다.

## 5. Core IR

`Lower`가 만드는 IR입니다(`src/Calc/Core.fs`). closure conversion이 끝나서 **모든 함수가 최상위**에 있습니다. 아래는 단계 6까지 구현한 모양입니다. 패턴은 `Ast.Pattern`을 그대로 씁니다. `type` 선언은 Core에 남지 않고, `Lower`가 생성자 범위(이름 → 인자 유무)를 정하는 데만 씁니다.

```fsharp
type CExpr =
    | CInt of int
    | CBool of bool
    | CString of string               // 전역 상수 블록 (같은 내용은 하나)
    | CVar of string                  // 지역 변수, 인자, 캡처한 변수, 자기 자신
    | CLet of string * CExpr * CExpr
    | CIf of CExpr * CExpr * CExpr    // && 와 || 도 여기로
    | CPrim of Prim * CExpr list      // 인라인 연산 또는 런타임 호출
    | CMakeClosure of funcName: string * captured: string list
    | CApply of CExpr * CExpr         // 클로저 간접 호출
    | CNil                            // 즉시값 5
    | CCons of CExpr * CExpr
    | CTuple of CExpr list
    | CData of ctor: string * payload: CExpr option                   // 생성자 값
    | CMatch of CExpr * (Ast.Pattern * CExpr option * CExpr) list   // let 패턴도 분기 하나짜리 match

type CFunction =
    { Name: string                    // lambda_0, lambda_1, ...
      Param: string
      Self: string option             // let rec 의 이름. 본문에서 클로저 자신
      Captured: string list           // 클로저의 칸 순서 (이름 순으로 정렬)
      Body: CExpr }

type CProgram = { Functions: CFunction list; Main: CExpr }
```

- **변수 표현을 바꾼 이유:** 처음 설계에서는 변수를 `Local | Arg | Self | Captured of index`로 나누려 했습니다. 구현에서는 이름(`CVar of string`) 하나로 두었습니다. 대신 `Codegen`이 함수 입구에서 캡처한 값을 클로저에서 꺼내고, 자기 자신과 인자와 함께 이름 → SSA 환경에 넣습니다. 그러면 함수 본문도 `let`과 같은 환경 방식으로 생성됩니다.
- **환경에서의 우선순위:** 인자 > 자기 자신 > 캡처한 변수 순입니다. `let rec f f = f`에서 본문의 `f`는 인자입니다(인터프리터와 같음).
- **캡처할 변수:** 본문(이미 `Core`로 바뀐 것)의 free variable에서 인자와 자기 자신을 뺀 것입니다. 안쪽 함수의 `CMakeClosure`는 캡처하는 변수가 곧 free variable입니다.
- **`Prim`:** 각 경우는 "인라인 `arith`로 만들지" 또는 "런타임 함수를 부를지"를 정합니다.

## 6. C 런타임

```c
typedef int64_t value;

void   calc_init(void);                       // GC_INIT 등
void  *calc_alloc(int64_t bytes);             // GC_MALLOC
void   calc_print_result(value v);            // Eval.formatValue 와 같은 형식
void   calc_error(const char *message);       // "Error: ..." 출력, exit(1)
void   calc_error_divide_by_zero(void);       // 인터프리터(.NET)와 같은 문장 (단계 1)
void   calc_error_overflow(void);             // Int32.MinValue / -1 (단계 1)
void   calc_match_failure(value v);
value  calc_add(value a, value b);            // int 또는 string
value  calc_eq(value a, value b);             // 구조적 비교
int32_t calc_compare(value a, value b);       // int 또는 string (ordinal)
value  calc_apply(value closure, value arg);  // 내장 고차 함수가 사용자 클로저를 부름
// 내장 함수 (builtins.c): calc_builtin_length, calc_builtin_substr, ... (36개)
extern const char calc_name_some[], calc_name_none[];   // option 의 생성자 이름
extern const calc_unit calc_none;                       // None 값
```

생성 코드와 런타임은 이 헤더 하나로만 약속합니다. 런타임 함수 이름은 모두 `calc_`로 시작합니다.

## 7. 예제 (직접 실행해서 확인함)

### 정수와 `if` (단계 1)

`let x = 5 in if x > 3 then x * 2 else 0`

```mlir
// int n 은 (n << 3) | 1, bool b 는 (b << 3) | 3 로 표현 (하위 3비트가 종류)
llvm.func @calc_print_result(i64)

llvm.func @calc_main() -> i64 {
  %x = arith.constant 41 : i64            // 5 = (5 << 3) | 1
  %three = arith.constant 25 : i64        // 3
  // 비교: 태그를 떼고 32비트 정수로 비교
  %c3 = arith.constant 3 : i64
  %xu = arith.shrsi %x, %c3 : i64
  %tu = arith.shrsi %three, %c3 : i64
  %xi = arith.trunci %xu : i64 to i32
  %ti = arith.trunci %tu : i64 to i32
  %gt = arith.cmpi sgt, %xi, %ti : i32
  %r = scf.if %gt -> (i64) {
    // x * 2 : 32비트에서 곱하고(한 바퀴 돎) 다시 태그
    %two = arith.constant 2 : i32
    %m = arith.muli %xi, %two : i32
    %m64 = arith.extsi %m : i32 to i64
    %sh = arith.shli %m64, %c3 : i64
    %one = arith.constant 1 : i64
    %t = arith.ori %sh, %one : i64
    scf.yield %t : i64
  } else {
    %zero = arith.constant 1 : i64        // 0 = (0 << 3) | 1
    scf.yield %zero : i64
  }
  llvm.return %r : i64
}

llvm.func @main() -> i32 {
  %v = llvm.call @calc_main() : () -> i64
  llvm.call @calc_print_result(%v) : (i64) -> ()
  %ok = arith.constant 0 : i32
  llvm.return %ok : i32
}
```

실행 결과 `10`이고, 인터프리터의 `Result: 10`과 같습니다. 태그를 떼고 붙이는 코드는 `Codegen`의 도우미 함수가 만듭니다(`untagInt`, `tagInt`).

### 클로저 (단계 3)

`let k = 10 in let add = fun x -> x + k in add 32`

```mlir
// 클로저 = [code 포인터, 캡처한 k]. 코드는 (클로저, 인자) -> 결과
llvm.func @calc_print_result(i64)
llvm.func @calc_alloc(i64) -> !llvm.ptr

llvm.func @lambda_0(%clo: !llvm.ptr, %x: i64) -> i64 {
  // 환경에서 k 꺼내기: 클로저의 1번 칸
  %kp = llvm.getelementptr %clo[1] : (!llvm.ptr) -> !llvm.ptr, i64
  %k = llvm.load %kp : !llvm.ptr -> i64
  %c3 = arith.constant 3 : i64
  %xu = arith.shrsi %x, %c3 : i64
  %ku = arith.shrsi %k, %c3 : i64
  %xi = arith.trunci %xu : i64 to i32
  %ki = arith.trunci %ku : i64 to i32
  %s = arith.addi %xi, %ki : i32
  %s64 = arith.extsi %s : i32 to i64
  %sh = arith.shli %s64, %c3 : i64
  %one = arith.constant 1 : i64
  %t = arith.ori %sh, %one : i64
  llvm.return %t : i64
}

llvm.func @calc_main() -> i64 {
  %k = arith.constant 81 : i64            // 10
  %size = arith.constant 16 : i64
  %clo = llvm.call @calc_alloc(%size) : (i64) -> !llvm.ptr
  %code = llvm.mlir.addressof @lambda_0 : !llvm.ptr
  llvm.store %code, %clo : !llvm.ptr, !llvm.ptr
  %kp = llvm.getelementptr %clo[1] : (!llvm.ptr) -> !llvm.ptr, i64
  llvm.store %k, %kp : i64, !llvm.ptr
  // add 32 : 클로저의 코드 포인터로 간접 호출
  %arg = arith.constant 257 : i64         // 32
  %f = llvm.load %clo : !llvm.ptr -> !llvm.ptr
  %r = llvm.call %f(%clo, %arg) : !llvm.ptr, (!llvm.ptr, i64) -> i64
  llvm.return %r : i64
}

llvm.func @main() -> i32 {
  %v = llvm.call @calc_main() : () -> i64
  llvm.call @calc_print_result(%v) : (i64) -> ()
  %ok = arith.constant 0 : i32
  llvm.return %ok : i32
}
```

실행 결과 `42`입니다. `calc_alloc`을 `GC_MALLOC`으로 구현하고 `-lgc`로 링크해도 같은 결과입니다. 이 예제의 클로저 블록에는 헤더가 없습니다. 실제 구현(단계 3)은 블록이 `[헤더, 코드 포인터, 캡처한 값...]`이고, 헤더는 `1 | (뒤따르는 단어 수 << 8)`입니다. 런타임은 헤더를 보고 함수 값을 `<fun>`으로 출력하고, 함수끼리 `=` 비교는 인터프리터와 같은 타입 오류로 끝냅니다.

## 8. 단계별 구현 계획

각 단계가 끝나면 그 단계의 기능에 대한 differential testing(9장)이 통과하고, 버전을 올립니다(0.x.0). 아직 지원하지 않는 기능이 프로그램에 있으면 `--compile`은 `Not supported by the compiler yet: <기능>`으로 멈춥니다.

| 단계 | 범위 | 새로 생기는 것 | 완료 기준 |
|---|---|---|---|
| **0. 기반** | 빈 파이프라인 | `--emit-mlir`, `--compile`, `Toolchain.fs`, `runtime.c`(`calc_print_result`), differential testing 틀, 도구가 없으면 테스트 건너뛰기 | `42` 하나를 컴파일해서 실행 |
| **1. 정수와 불리언** | 리터럴, `+ - * /`, 단항 `-`, 비교, `&& \|\| not`(short-circuit evaluation), `if` | 태그 붙이고 떼기, `scf.if`, 0으로 나누기·`MinValue / -1` 검사 | 단계 1 기능만 쓰는 `EvalTests` 예제가 모두 같음 |
| **2. `let`과 변수** | `let x = e in body`, 섀도잉 | 이름 → SSA 값 환경 | 위 + `let` 예제 |
| **3. 함수와 클로저** | `fun`, 호출, 커링, `let f x =`, `let rec`, 클로저 | `Core.fs`, `Lower.fs`(free variable, closure conversion), `calc_alloc`(Boehm GC), 큰 스택 링크 옵션 | `fact`, `fib`, 커링, lexical scope 예제. 재귀 깊이 10만 |
| **4. 문자열** | 리터럴, `+`, `=`, `<` | 문자열 상수(`llvm.mlir.global`), `calc_add`, `calc_eq`, `calc_compare`, UTF-16 저장과 UTF-8 출력 | 문자열 예제, 이스케이프 출력 |
| **5. 리스트, 튜플, 패턴 매칭** | `[]`, `::`, `[a; b]`, 튜플, `match`(모든 패턴, 가드), `let (a, b) =` | 블록 할당, 패턴 검사 코드, `cf` 블록과 블록 인자, `calc_match_failure` | 리스트, 튜플, 패턴 예제 |
| **6. ADT와 option** | `type` 선언, 생성자 값과 패턴, `Some`/`None` | 생성자 descriptor 전역 상수, `DATA` 블록 | ADT 예제 |
| **7. 내장 함수** | 문자열 14개, 리스트 14개, `fst`/`snd`, option 3개 | C 구현, 정적 클로저, `calc_apply`, `toString`과 `toInt` | 내장 함수 예제 전부 |
| **8. 완성도** | 모든 `EvalTests` 예제와 오류 메시지 | 실행 중 오류 메시지 정리, 종료 코드 | 타입 검사를 통과하는 `EvalTests.cases` 전부가 같음 |
| **9. 최적화 (선택)** | 성능 | 타입별 specialization, 알려진 함수 direct call, uncurrying, `tail` 호출, `-O2` 비교, 사용자 dialect(`calc`) 검토 | 벤치마크로 인터프리터와 비교 |

단계 1~2는 힙이 필요 없습니다. 그래서 GC와 런타임이 거의 비어 있는 상태로 파이프라인 전체를 먼저 검증할 수 있습니다.

## 9. 테스트 전략: differential testing

- **같은 입력을 인터프리터와 컴파일러로 실행해서 출력을 비교합니다.** 기대값을 새로 적지 않습니다.
  - 인터프리터 쪽: `Eval.formatValue (Eval.eval ast)`, 또는 오류 메시지
  - 컴파일러 쪽: 실행 파일의 표준 출력, 표준 에러, 종료 코드
- 입력은 기존 `EvalTests.cases`에서 **타입 검사를 통과하고 현재 단계가 지원하는 것**을 고릅니다. 지원 여부는 `Lower`가 판단합니다(지원하지 않으면 `Not supported`). 그래서 단계가 올라갈수록 테스트 수가 자동으로 늘어납니다. 각 단계에서 통과 개수의 lower bound를 테스트에 적습니다.
- 오류 예제(`1 / 0`, `head []`, `Match failure` 등)는 메시지와 종료 코드까지 비교합니다.
- 예제마다 실행 파일을 만들면 느립니다(외부 도구 3번, macOS의 새 실행 파일 첫 실행 약 0.3초). 그래서 테스트는 모든 예제를 실행 파일 하나로 묶어 빌드하고, 그 안의 드라이버가 예제마다 `fork`해서 실행합니다(`Toolchain.buildMany`). 예제마다 프로세스가 따로라서 출력과 종료 코드는 단독 실행 파일과 같습니다.
- 도구가 없는 환경에서는 differential testing을 건너뛰고, 건너뛴 이유를 출력합니다.
- `--emit-mlir` 출력의 골든 테스트는 작은 예제 몇 개에만 둡니다. 출력 형식이 자주 바뀌기 때문입니다.
- `mlir-opt`를 아무 패스 없이 실행해서 생성한 MLIR이 올바른지 먼저 검증합니다. 오류가 나면 `Codegen` 버그입니다.

## 10. 열린 문제와 위험

| 문제 | 현재 판단 |
|---|---|
| 외부 도구 실행이 느림 | 단계 0~8에서는 감수. 나중에 MLIR C API(LLVM 소스 빌드)나 `mlir-opt` 한 번 실행으로 줄일 수 있음 |
| 문자열 UTF-16 | 인터프리터와 같은 결과를 위해 선택. 메모리 두 배, C에서 다루기 번거로움 |
| 깊은 재귀 | 스택 크기 옵션으로 시작. tail call 보장은 단계 9 |
| conservative GC | 정수처럼 보이는 포인터 때문에 GC가 메모리를 덜 회수할 수 있음. 태그로 대부분 피함 |
| macOS 전용 경로 | `CALC_LLVM_BIN`, `CALC_GC_PREFIX`로 다른 환경을 지원. CI는 범위 밖 |
| 사용자 dialect | C++ 빌드가 필요. 표준 dialect만으로 단계 8까지 가능한지 먼저 확인 |

## 참고 자료

- [MLIR Toy 튜토리얼 6장: Lowering to LLVM](https://mlir.llvm.org/docs/Tutorials/Toy/Ch-6)
- [MLton LLVMCodegen](https://mlton.sourceforge.io/LLVMCodegen): 텍스트 LLVM IR 출력 방식
- [Lambda the Ultimate SSA](https://arxiv.org/pdf/2201.07272): 함수형 언어를 MLIR로 (Lean 4)
- [cmaes/miniml](https://github.com/cmaes/miniml): MinCaml 구조와 LLVM, 타입이 있는 closure conversion
- [Roc 컴파일러 구조](https://git.joshthomas.dev/language-servers/roc/raw/branch/import-builtins/crates/README.md): monomorphization, 참조 카운트, LLVM
- [Boehm GC](https://www.hboehm.info/gc/)
