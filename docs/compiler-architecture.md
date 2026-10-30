# 컴파일러 구조와 흐름

`calc --compile`이 소스 코드에서 실행 파일을 만드는 과정을 예제 하나로 따라갑니다. 설계의 이유와 단계별 계획은 [compiler-design.md](compiler-design.md)에 있습니다. 이 문서는 **지금 코드가 실제로 하는 일**을 설명합니다.

이 문서의 출력은 모두 직접 실행해서 얻었습니다. 2장의 예제는 단계 2(0.17.0), 6장의 클로저는 단계 3, 7장의 문자열은 단계 4, 8장의 패턴 매칭은 단계 5, 9장의 ADT는 단계 6, 10장의 내장 함수는 단계 7 기준입니다. 환경은 macOS arm64, Homebrew LLVM 22.1.7, bdw-gc 8.2.12입니다.

## 1. 큰 그림

하나의 소스를 인터프리터와 컴파일러가 함께 씁니다. 파서와 타입 검사까지는 같은 코드이고, 그 뒤에서 갈라집니다.

```
                        calc (F#, Calc.dll)
 소스 ──▶ Lexer/Parser ──▶ AST ──▶ Infer (타입 검사)
                                   │
              ┌────────────────────┴─────────────────────┐
              ▼ (기본)                                     ▼ (--compile / --emit-mlir)
        Eval.eval                                   Lower.lowerProgram
        "Result: 3240"                              Core IR (클로저 변환)
                                                          │  Codegen.emitProgram
                                                          ▼
                                                    MLIR 텍스트 (문자열)
                                                          │  Toolchain.build
                                                          ▼
                     ┌──────────── 외부 도구 (Homebrew LLVM) ────────────┐
                     │ mlir-opt       prog.mlir      → prog.llvm.mlir    │
                     │ mlir-translate prog.llvm.mlir → prog.ll (LLVM IR) │
                     │ clang -O2 -c   prog.ll → prog.o                   │
                     │ clang          prog.o + runtime.o → 실행 파일     │
                     └────────────────────────────────────────────────────┘
```

| 단계 | 파일 | 하는 일 |
|---|---|---|
| 파싱 | `Lexer.fsl`, `LayoutFilter.fs`, `Parser.fsy`, `Modules.fs` | 소스 → `Ast.Expr` (인터프리터와 같음). 모듈은 이 단계에서 `M.f` 같은 이름의 선언으로 펼쳐지므로 뒤 단계에는 없음 |
| prelude | `Prelude.fs`, `prelude.calc` | 프로그램이 쓰는 표준 라이브러리 정의(`map`, `fold` 등)를 AST 앞에 `let` 사슬로 붙임 (인터프리터와 같음). 그다음부터는 보통 함수 |
| 타입 검사 | `Infer.fs` | 통과한 프로그램만 컴파일 (`--no-typecheck`와 함께 쓸 수 없음) |
| lowering | `Core.fs`, `Lower.fs` | `Ast.Expr` → Core IR. 연산을 `Prim`으로, `&&`/`||`를 `if`로, closure conversion(함수를 최상위로). 지원하지 않는 기능은 `NotSupported` |
| 코드 생성 | `Codegen.fs` | Core IR → MLIR 텍스트 |
| 빌드 | `Toolchain.fs` | 도구 찾기, 임시 폴더에서 외부 도구(`mlir-opt`, `mlir-translate`, `clang`)를 차례로 실행, C 런타임 오브젝트 캐시 |
| 런타임 | `runtime/runtime.c`, `builtins.c`, `runtime.h`, `unicode.h` | 초기화, 결과 출력, 오류 종료, polymorphic 연산(`calc_add`, `calc_eq`, `calc_compare`. int는 생성 코드가 먼저 처리), 내장 함수 21개, 예외(`calc_try`, `calc_raise`). `calc_apply`는 생성한 모듈이 정의(12장) |
| 진입 | `Program.fs`의 `compile` | 위 단계를 잇고, 오류를 인터프리터와 같은 형식(`Error: ...`)으로 출력 |

F#으로 하는 일은 MLIR **텍스트**를 만드는 데까지입니다. LLVM을 라이브러리로 링크하지 않고, 텍스트 파일을 외부 도구에 넘깁니다(설계 D1).

## 2. 예제 하나로 따라가기

`discount.calc`:

```
let price = 1200 in
let qty = 3 in
let total = price * qty in
if total > 3000 then total - total / 10 else total
```

```
$ calc < discount.calc                                    # 인터프리터
Result: 3240
$ calc --compile ./discount --keep-temps < discount.calc  # 컴파일
Intermediate files: /var/folders/.../calc-72dbaad5
$ ./discount
3240
```

`--keep-temps`를 주면 중간 파일(`prog.mlir`, `prog.llvm.mlir`, `prog.ll`, `prog.o`)이 남습니다. `--emit-mlir`은 ②의 결과만 출력합니다.

### ① AST와 타입 검사: 인터프리터와 같은 코드

```
Let ("price", Number 1200,
  Let ("qty", Number 3,
    Let ("total", Multiply (Var "price", Var "qty"),
      If (Greater (Var "total", Number 3000),
          Subtract (Var "total", Divide (Var "total", Number 10)),
          Var "total"))))
Type: int
```

### ② `Codegen`이 만드는 `prog.mlir`: 모듈은 세 부분

```mlir
// (가) 런타임 함수 선언: 본문 없음 → C 런타임이 정의
llvm.func @calc_init()
llvm.func @calc_alloc(i64) -> !llvm.ptr        // 단계 3부터
llvm.func @calc_print_result(i64)
llvm.func @calc_error_divide_by_zero()
llvm.func @calc_error_overflow()
llvm.func @calc_add(i64, i64) -> i64
llvm.func @calc_eq(i64, i64) -> i64
llvm.func @calc_compare(i64, i64) -> i32

// (나) 프로그램 본체: 식 전체가 함수 하나, 결과는 태그가 붙은 i64
llvm.func @calc_main() -> i64 {
  %v1 = arith.constant 9601 : i64        // 1200 = (1200 << 3) | 1
  %v2 = arith.constant 25 : i64          // 3
  ...                                    // let 은 코드 없음: price = %v1, qty = %v2
  %v9 = arith.muli %v5, %v8 : i32        // 태그를 떼고 i32 곱셈
  %v14 = arith.ori %v12, %v13 : i64      // 다시 태그 → total = %v14
  %v16 = arith.cmpi sgt, %v14, %v15 : i64  // total > 3000: 타입 추론이 int 로 정했으므로 태그를 붙인 채로 바로 비교
  ...
  %v22 = scf.if %v20 -> (i64) {
    ...                                  // total / 10 (0 검사, MinValue / -1 검사 포함)
    scf.yield %v56 : i64
  } else {
    scf.yield %v14 : i64                 // total
  }
  llvm.return %v24 : i64
}

// (다) 진입점: 프로그램마다 똑같은 모양으로 붙음
llvm.func @main() -> i32 {
  llvm.call @calc_init() : () -> ()                       // GC_INIT()
  %result = llvm.call @calc_main() : () -> i64            // 프로그램 실행
  llvm.call @calc_print_result(%result) : (i64) -> ()     // "3240\n"
  %ok = arith.constant 0 : i32
  llvm.return %ok : i32                                   // 종료 코드 0
}
```

- **value representation:** 모든 값은 `i64` 하나입니다. 하위 3비트가 종류를 나타내고, int `n`은 `(n << 3) | 1`, bool은 `3`(false)과 `11`(true)입니다(설계 D3).
- **함수 형태:** 함수는 모두 `llvm.func`이고, 본문에는 `arith`과 `scf`를 섞어 씁니다(설계 D2).
- **`let`:** 코드를 만들지 않습니다. `Codegen`의 환경(`Map<string, string>`, 이름 → SSA 이름)에 넣을 뿐입니다.

### ③ `mlir-opt`가 만드는 `prog.llvm.mlir`: 모두 `llvm` dialect로

```
mlir-opt prog.mlir --convert-scf-to-cf --convert-cf-to-llvm \
         --convert-arith-to-llvm --reconcile-unrealized-casts -o prog.llvm.mlir
```

`scf.if`는 basic block과 분기(`llvm.cond_br %22, ^bb1, ^bb6`)로 바뀌고, `arith.*`는 `llvm.*`로 바뀝니다. 이 과정에서 상수 계산의 상당 부분이 접힙니다.

### ④ `mlir-translate`가 만드는 `prog.ll`: LLVM IR

```llvm
declare void @calc_init()                 ; (가)의 선언이 declare 로
declare void @calc_print_result(i64)
declare i32 @calc_compare(i64, i64)
...

define i64 @calc_main() {
  br i1 true, label %1, label %6                       ; total > 3000: 이미 true 로 접힘
  ...
  br i1 false, label %2, label %3                      ; 0 검사도 false 로 접힘
  ...
  %8 = phi i64 [ 28801, %6 ], [ 25921, %5 ]            ; 3600 또는 3240
  ret i64 %8
}

define i32 @main() {                      ; (다)가 C 의 main 심볼이 됨
  call void @calc_init()
  %1 = call i64 @calc_main()
  call void @calc_print_result(i64 %1)
  ret i32 0
}
```

`price * qty`와 `total / 10`이 이미 상수로 접혔습니다. 28801은 3600, 25921은 3240을 태그로 나타낸 값입니다.

### ⑤ `clang`: 오브젝트로 컴파일하고 런타임과 링크

```
clang -O2 -Wno-override-module -c prog.ll -o prog.o
/usr/bin/clang prog.o $TMPDIR/calc-runtime-<해시>.o $TMPDIR/calc-builtins-<해시>.o $TMPDIR/calc-gc-<해시>.o \
      -Wl,-stack_size,0x20000000 -o discount      # 링크는 Apple clang (Homebrew clang 은 libLTO 를 읽느라 느림)
```

이 문서의 처음 판(0.17.0)에서는 `clang -O2 prog.ll runtime.c ...` 한 번으로 컴파일과 링크를 했습니다. 지금은 런타임을 한 번만 컴파일해 둡니다(4장).

최종 `calc_main`(arm64 어셈블리, `otool -tV discount`):

```
mov  w0, #0x6541          ; 25921 = 3240
ret
```

`>`의 피연산자는 타입 추론이 int로 정했으므로 태그 검사도 런타임 경로도 없습니다(0.54.0부터). 0.48.0까지는 `>`가 언제나 `calc_compare`를 불러서, 런타임 함수라 접을 수 없는 호출(`bl _calc_compare`)과 `csel`이 남았습니다. 지금은 int 비교를 생성 코드에서 하므로 clang이 프로그램 전체를 상수 3240으로 접습니다.

## 3. entry point `main`

실행 파일의 entry point는 **컴파일러가 프로그램마다 만들어 넣는** `@main`입니다. C 런타임에는 `main`이 없습니다.

| 함수 | 만드는 곳 | 하는 일 |
|---|---|---|
| `main` | `Codegen.emitProgram` (프로그램마다 같은 모양) | 런타임 초기화 → `calc_main` → 결과 출력 → `return 0` |
| `calc_main` | `Codegen.emitExpr` (프로그램마다 다름) | Calc 식 전체를 계산해서 값 하나(`i64`)를 돌려줌 |
| `calc_*` | `runtime.c` | 초기화, 출력, 오류, polymorphic 연산 |

- **실행 순서:** OS 로더(dyld)가 C 시작 코드를 거쳐 `main` 심볼을 부릅니다. `runtime.c`에는 `main`이 없으므로 이름이 충돌하지 않습니다.
- **`main`과 `calc_main`을 나눈 이유:** 실행 틀(초기화, 출력, 종료 코드)과 프로그램 계산을 분리하려는 것입니다. 단계 3에서 최상위 함수가 늘어나도 `main`은 그대로입니다.
- **실행 중 오류:** `calc_error`가 stdout을 비우고 stderr에 `Error: <메시지>`를 쓴 뒤 `exit(1)`로 바로 끝냅니다. `main`의 `return 0`까지 오지 않습니다. 결과는 인터프리터와 같습니다(메시지, 종료 코드 1).

## 4. 런타임의 배포, 링크, 호출

### 배포: C 소스로 들고 다님

```xml
<!-- src/Calc/Calc.fsproj -->
<None Include="runtime\**" CopyToOutputDirectory="PreserveNewest" LinkBase="runtime" />
```

빌드하면 `runtime.c`와 `runtime.h`가 `Calc.dll` 옆의 `runtime/` 폴더로 복사됩니다. `Toolchain.find`는 이 파일을 `AppContext.BaseDirectory/runtime/runtime.c`에서 찾습니다. `ProjectReference`를 따라 테스트 프로젝트 출력 폴더에도 복사되므로, 테스트도 같은 코드로 찾습니다.

처음 `--compile`할 때 `clang -O2 -c`로 런타임의 `.c` 파일(`runtime.c`, `builtins.c`, `gc.c`)을 컴파일해서 임시 폴더에 `calc-<파일>-<해시>.o`로 둡니다. 해시는 런타임의 모든 `.c`·`.h` 파일과 `clang` 경로로 만들고, 같은 해시의 파일이 있으면 다시 씁니다. 다른 프로세스와 동시에 만들 수 있으므로 임시 이름으로 만든 뒤 옮깁니다.

### 링크: 선언과 정의를 이름으로 맞춤

```
prog.mlir   llvm.func @calc_compare(i64, i64) -> i32    (본문 없음 = 선언)
   ↓ mlir-translate
prog.ll     declare i32 @calc_compare(i64, i64)          (외부 심볼)
   ↓ clang 이 prog.o 와 runtime.o(캐시)를 링커로 넘기고, 링커가 이름으로 연결
runtime.c   int32_t calc_compare(value a, value b) {...}  (정의, value = int64_t)
```

링크된 실행 파일의 심볼(`nm discount`):

```
T _main  T _calc_main                                   ← 생성한 코드
T _calc_init  T _calc_print_result  T _calc_compare
T _calc_add  T _calc_eq  T _calc_error ...              ← runtime.c (실행 파일 안)
T _calc_alloc  T _calc_write_barrier ...                ← gc.c (실행 파일 안)
U _printf  U _fprintf  U _exit  U _mmap                ← 실행할 때 libSystem 에서
```

동적 라이브러리(`otool -L discount`). 0.65.2까지는 Homebrew의 `libgc.1.dylib`도 있었습니다.

```
/usr/lib/libSystem.B.dylib
```

### 호출: 보통의 C calling convention

- **타입 대응:** MLIR의 `i64`는 C의 `int64_t`(`value`), `i32`는 `int32_t`, 인자 없는 함수는 `void f(void)`입니다. 플랫폼의 C calling convention 그대로 레지스터로 넘깁니다.
- **약속을 적은 곳:** 생성 코드와 런타임 사이의 약속은 `runtime.h`에 적습니다(태그 상수, 함수 목록). 런타임 함수 이름은 모두 `calc_`로 시작합니다.
- **생성한 함수끼리는 `tailcc`:** 클로저 함수와 direct 함수는 tail call을 보장하는 `tailcc` calling convention입니다(12장). C는 `tailcc` 함수를 부를 수 없으므로, 런타임이 클로저를 부르는 `calc_apply`는 생성한 모듈에 있습니다.
- **런타임 함수가 필요한 연산:** 타입이 여럿일 수 있는 연산(`+`, `=`, `<`)과 바깥 세계와 닿는 일(출력, 오류 종료, GC)을 런타임이 맡습니다. 정수 사칙연산처럼 타입이 하나뿐인 연산은 인라인 `arith`로 만듭니다(설계 D6).

## 5. 지금 구조의 한계

| 한계 | 결과 | 생각해 볼 해결책 |
|---|---|---|
| MLIR 선언(`Codegen.fs`의 문자열)과 `runtime.h`를 맞추는 검사가 없음 | 인자 타입이 어긋나도 링크는 성공하고, 실행 중 값이 틀림. 지금은 differential testing만 잡음 | 선언을 한 곳에서 만들기, 또는 헤더와 비교하는 테스트 |
| 런타임 호출은 최적화를 막음 | 위 어셈블리에서 `calc_compare` 호출이 남음 | 타입별 specialization(설계 단계 9) |
| 함수 호출은 모두 클로저를 거침 | `go (n - 1) acc`처럼 이름이 정해진 함수도 커링된 클로저를 하나씩 적용하고, 중간 클로저를 할당함 | 알려진 함수 direct call, uncurrying(설계 단계 9, `TODO.md`) |

## 6. 함수와 클로저 (단계 3)

```
let k = 10 in let add = fun x -> x + k in add 32
```

### Lower: closure conversion

`fun x -> x + k`를 최상위 함수로 옮깁니다. 본문의 free variable 중 인자를 뺀 `k`가 캡처할 변수입니다.

```
Functions = [ { Name = "lambda_0"; Param = "x"; Self = None; Captured = ["k"];
                Body = CPrim (PAdd, [CVar "x"; CVar "k"]) } ]
Main      = CLet ("k", CInt 10,
              CLet ("add", CMakeClosure ("lambda_0", ["k"]),
                CApply (CVar "add", CInt 32)))
```

### Codegen: 함수 하나와 클로저 블록

모든 함수 값은 클로저이고, calling convention은 `(클로저, 인자) -> 결과` 하나입니다.

```mlir
llvm.func internal @lambda_0(%closure: !llvm.ptr, %arg: i64) -> i64 {   // internal: 모듈 밖에서 보이지 않음
  %v2 = llvm.getelementptr %closure[2] : (!llvm.ptr) -> !llvm.ptr, i64
  %v1 = llvm.load %v2 : !llvm.ptr -> i64                // 입구에서 k 를 꺼냄
  %v3 = llvm.call @calc_add(%arg, %v1) : (i64, i64) -> i64
  llvm.return %v3 : i64
}

llvm.func @calc_main() -> i64 {
  %v1 = arith.constant 81 : i64                         // k = 10
  %v2 = arith.constant 24 : i64                         // 3 단어
  %v3 = llvm.call @calc_alloc(%v2) : (i64) -> !llvm.ptr // GC_MALLOC
  %v4 = arith.constant 513 : i64                        // 헤더 = 1 | (2 << 8)
  llvm.store %v4, %v3 : i64, !llvm.ptr                  // [0] 헤더
  %v5 = llvm.mlir.addressof @lambda_0 : !llvm.ptr
  ...                                                   // [1] 코드 포인터, [2] k
  %v8 = llvm.ptrtoint %v3 : !llvm.ptr to i64            // add (포인터, 하위 3비트 000)
  %v9 = arith.constant 257 : i64                        // 32
  %v10 = llvm.inttoptr %v8 : i64 to !llvm.ptr
  %v12 = llvm.getelementptr %v10[1] : (!llvm.ptr) -> !llvm.ptr, i64
  %v11 = llvm.load %v12 : !llvm.ptr -> !llvm.ptr        // 코드 포인터
  %v13 = llvm.call %v11(%v10, %v9) : !llvm.ptr, (!llvm.ptr, i64) -> i64   // 간접 호출
  llvm.return %v13 : i64
}
```

- **클로저 블록:** `[헤더, 코드 포인터, 캡처한 값...]`이고 `calc_alloc`으로 할당합니다. 해제 코드는 없습니다. 인자가 여럿인 함수의 바깥 클로저는 마지막 칸에 direct 함수 포인터를 하나 더 두고, 헤더의 32비트 위에 인자 수를 적습니다(15장).
- **값으로서의 클로저:** 블록 주소를 그대로 `i64`로 씁니다. 8-byte aligned라 하위 3비트가 `000`이고, 런타임은 이것으로 정수·불리언과 구분합니다.
- **헤더:** 런타임이 헤더의 종류(1)를 보고 함수 값을 `<fun>`으로 출력합니다. 함수끼리 `=`로 비교하면 인터프리터와 같은 타입 오류로 끝냅니다.
- **`let rec f x = ...`:** 본문의 `f`는 `%closure` 자신(`llvm.ptrtoint %closure`)입니다. 자기 자신을 캡처하지 않으므로 순환 참조를 만들 필요가 없습니다.

`clang -O2`는 클로저를 만든 직후의 호출을 알아봅니다. 저장한 코드 포인터와 `k`를 다시 읽지 않고 그대로 쓰고, `lambda_0`을 inline합니다.

```llvm
define i64 @calc_main() {
  %1 = tail call ptr @calc_alloc(i64 24)
  store i64 513, ptr %1
  ...                                       ; 코드 포인터와 k 저장
  %4 = tail call i64 @calc_add(i64 257, i64 81)   ; add 32 → calc_add(32, 10)
  ret i64 %4
}
```

### 깊은 재귀

`clang`에 `-Wl,-stack_size,0x20000000`을 넘겨 메인 스레드 스택을 512MB로 만듭니다. 인터프리터가 평가에 쓰는 큰 스택 스레드와 같은 크기입니다. 기본 8MB에서는 깊이 100만 재귀가 세그폴트(종료 코드 139)로 죽고, 512MB에서는 끝까지 갑니다. 이 옵션은 macOS `ld` 전용입니다.

## 7. 문자열 (단계 4)

```
"hi" + "!"
```

문자열 상수는 전역 상수 블록이 됩니다. 같은 내용은 하나만 만듭니다.

```mlir
llvm.mlir.global internal constant @str_0() {addr_space = 0 : i32, alignment = 8 : i64}
    : !llvm.struct<(i64, i64, array<2 x i16>)> {
  ...                                    // [헤더 514 = 2 | (2 << 8), 길이 2, 'h' 'i']
  %a = llvm.mlir.constant(dense<[104, 105]> : tensor<2xi16>) : !llvm.array<2 x i16>
  ...
}

llvm.func @calc_main() -> i64 {
  %v1 = llvm.mlir.addressof @str_0 : !llvm.ptr
  %v2 = llvm.ptrtoint %v1 : !llvm.ptr to i64           // 값 = 전역의 주소 (8-byte alignment)
  %v3 = llvm.mlir.addressof @str_1 : !llvm.ptr
  %v4 = llvm.ptrtoint %v3 : !llvm.ptr to i64
  %v5 = llvm.call @calc_concat2(%v2, %v4) : (i64, i64) -> i64   // 타입 추론이 string 으로 정한 + (아래)
  llvm.return %v5 : i64
}
```

- **저장:** code unit은 .NET `string`과 같은 UTF-16입니다. 그래서 비교(`String.CompareOrdinal`)가 code unit 순서로 인터프리터와 같습니다. 예를 들어 `"😀" < "\uFFFF"`는 `true`입니다.
- **출력:** 런타임이 UTF-8로 바꿉니다. 짝이 없는 surrogate code unit은 U+FFFD가 됩니다.
- **상수:** 전역이라 GC가 관리하지 않습니다.
- **`+`의 결과:** `calc_alloc_atomic`으로 할당합니다. 블록 안에 포인터가 없으므로 GC가 내용을 훑지 않습니다.
- **연쇄를 한 번에 (0.62.0부터):** 타입 추론이 피연산자를 string으로 정한 `+` 노드는 `Infer.stringAdds`로 나옵니다(int의 `Infer.intOperations`와 같은 방식). `Lower`는 그런 `+`가 이어진 식을 펴서 조각 목록 하나(`PConcat`)로 만들고, Codegen은 조각을 왼쪽부터 계산한 뒤 `calc_concatN`을 한 번 부릅니다. 런타임은 길이를 더해 한 번 할당하고 차례로 복사합니다.
  - **조각 순서:** `a + (b + c)`도 `a, b, c`입니다. 원래 식도 `a`, `b`, `c` 순서로 계산하므로 같습니다.
  - **조각 수:** 런타임에 `calc_concat2`부터 `calc_concat8`까지 있습니다. 8개가 넘으면 앞 8개를 먼저 잇고 그 결과를 첫 조각으로 다시 셉니다. 조각 배열을 스택에 만들어 넘기지 않는 것은, 반복문(`scf.while`) 안의 `alloca`가 반복마다 스택을 키우기 때문입니다.
  - **그대로인 것:** 타입 검사를 하지 않은 식의 `+`는 전처럼 `calc_add`가 태그를 봅니다. 인터프리터는 바뀌지 않았습니다.
  - **효과:** `"item " + toString i + " of " + …` 같은 일곱 조각 2백만 번이 0.17초에서 0.08초, `sprintf "%d-%d:%s"` 2백만 번이 0.11초에서 0.06초가 되었습니다.
- **출력 형식:** `Eval.formatValue`와 같습니다. 따옴표로 감싸고 `\\ \" \n \t \r`만 이스케이프합니다.

## 8. 리스트와 패턴 매칭 (단계 5)

```
match [7] with | x :: _ when x > 5 -> x | _ -> 0
```

```mlir
// [7] = 7 :: [] → 헤더 없는 cons 칸 [head 57, tail 5] (16바이트), 값은 주소 | 2
%v4 = llvm.call @calc_alloc(%v3) : (i64) -> !llvm.ptr     // %v3 = 16
...
%v8 = arith.ori %v7, %v9 : i64                         // ptrtoint | 2

// 첫 분기의 조건 (i1): cons 인가? 그렇다면 head 를 읽어 가드 x > 5
%v9 = arith.cmpi ne, %v8, %v10 : i64                  // %v10 = 5 ([])
%v11 = scf.if %v9 -> (i1) {
  %v13 = llvm.load %v14 : !llvm.ptr -> i64            // x = head
  ...                                                  // x > 5
  scf.yield %v22 : i1
} else {
  %v24 = arith.constant 0 : i1
  scf.yield %v24 : i1
}

// 조건이 참이면 이름을 묶고 본문, 아니면 나머지 분기 (_ -> 0: 검사 없음, 그 뒤는 생성하지 않음)
%v25 = scf.if %v11 -> (i64) {
  %v27 = llvm.load %v28 : !llvm.ptr -> i64            // x = head (다시 읽음)
  scf.yield %v27 : i64
} else {
  %v29 = arith.constant 1 : i64                       // 0
  scf.yield %v29 : i64
}
```

- **분기 하나:** "조건(`i1`)을 만드는 `scf.if`"와 "본문 또는 나머지를 고르는 `scf.if`" 두 개가 됩니다. 나머지 분기는 한 번만 생성됩니다.
- **실패:** 마지막 분기까지 실패하면 `calc_match_failure(v)`가 `Error: Match failure: <값>`을 출력하고 종료 코드 1로 끝냅니다.
- **`let (a, b) = e in body`:** 분기 하나짜리 `match`로 바뀝니다. 튜플 분해는 실패할 수 없으므로 검사 코드 없이 필드만 읽습니다.
- **`match (a, b) with (p, q) -> …` (0.51.0부터):** 분기가 모두 원소 수가 같은 튜플 패턴이나 `_`면 튜플 블록을 만들지 않습니다. 원소를 왼쪽부터 계산한 뒤 원소마다 패턴을 맞춥니다. 맞는 분기가 없을 때만 튜플을 만들어 `calc_match_failure`에 줍니다. merge sort의 `match (xs, ys) with`가 단계마다 하던 할당이 없어졌습니다.
- **`append (reverse xs) ys` (0.51.0부터):** Codegen이 이 모양을 알아보고 런타임의 `calc_rev_append` 하나로 부릅니다. 거꾸로 만든 리스트를 다시 복사하지 않으므로 리스트 칸을 n개만 만듭니다(전에는 2n개).
- **런타임:** `format_value`(출력과 Match failure 메시지)와 `equals`(structural `=`)가 cons와 튜플 블록을 따라갑니다. 리스트는 재귀가 아니라 반복문으로 돌아서 긴 리스트도 C 스택을 쓰지 않습니다. `equals`는 인터프리터처럼 길이를 먼저 비교합니다. 그래서 `[fun x -> x] = []`는 오류 없이 `false`입니다.
- **cons 칸 (0.53.0부터):** 헤더가 없는 `[head, tail]` 16바이트 블록이고, 값은 블록 주소에 태그 `2`를 붙인 것입니다. head는 `값 - 2`, tail은 `값 + 6` 바이트에서 읽습니다. `=`의 빠른 경로는 "태그가 홀수(int, bool, `[]`, `()`)면 비트를 비교"이므로 cons는 런타임이 내용을 비교합니다. merge sort 20만이 0.064초에서 0.049초, 최대 메모리가 72MB에서 38MB가 되었습니다.

## 9. ADT와 option (단계 6)

```
match Some 2 with | Some x -> x | None -> 0
```

```mlir
llvm.mlir.global internal constant @ctor_0("Some\00") {addr_space = 0 : i32}   // 생성자 이름 (C 문자열)
llvm.mlir.global internal constant @ctor_1("None\00") {addr_space = 0 : i32}

// 생성자 번호 = (@ctor_0 의 주소 - @calc_name_none 의 주소) << 32
// Some 2 → DATA 블록 [헤더 = 번호 | 5 | (1 << 8), 인자 17]
%v5 = llvm.call @calc_alloc(%v4) : (i64) -> !llvm.ptr
...
// | Some x : 블록(태그 0)이고 헤더의 위쪽 32비트가 번호와 같은가?
%v12 = llvm.load %v13 : !llvm.ptr -> i64               // 헤더
%v14 = arith.andi %v12, %c : i64                       // -1 << 32
%v10 = arith.cmpi eq, %v14, %number : i64
%v16 = scf.if %v10 -> (i64) {
  %v18 = llvm.load %v19 : !llvm.ptr -> i64             // x = 칸 1
  scf.yield %v18 : i64
} else { ... }                                         // | None -> 0 (즉시값 6 과 비교), 그다음 Match failure
```

- **생성자 번호 (0.59.0부터):** 생성자 이름 전역의 주소에서 런타임의 `calc_name_none` 주소를 뺀 값입니다. 생성한 코드와 런타임이 한 실행 파일에 링크되므로 32비트에 들어갑니다. 링크할 때 정해지므로 생성 코드가 주소 두 개를 빼서 계산하고, LLVM이 반복 밖으로 뺍니다. 런타임은 `calc_name_none`에 번호를 더해 이름을 찾습니다. 이 덧셈은 정수로 합니다. 포인터로 더하면 배열 밖을 가리키는 연산(undefined behavior)이라 clang이 `strlen`을 잘못 최적화했습니다.
- **블록에 이름 칸이 없음:** 블록은 `[헤더, 인자...]`입니다. 0.58.0까지는 `[헤더, 이름 포인터, 인자...]`였습니다. `Node (l, v, r)`이 40바이트(Boehm에서 48바이트)에서 32바이트가 되고, `Some x`는 24바이트(32바이트)에서 16바이트가 됩니다.
- **인자 없는 생성자(`None`, `Leaf`):** 즉시값 `(번호 << 32) | 6`입니다. 쓰지 않던 태그 `110`을 씁니다. 패턴 검사는 값 비교 한 번이고 메모리를 읽지 않습니다. 0.58.0까지는 전역 상수 블록 `[헤더, 이름]`의 주소였습니다.
- **생성자 검사:** 인자 없는 생성자는 즉시값과 비교합니다. 인자 있는 생성자는 값이 블록(태그 0)인지 먼저 보고, 헤더의 위쪽 32비트를 번호와 비교합니다. 같은 타입의 인자 없는 생성자는 즉시값이라 헤더를 읽기 전에 태그를 봐야 합니다. 런타임의 `=`도 번호(헤더 전체)를 비교합니다.
- **`type` 선언:** 코드를 만들지 않습니다. `Lower`가 생성자 범위(이름 → 인자 유무)를 넓히는 데만 씁니다. `Some`과 `None`은 처음부터 범위에 있습니다.
- **인자 있는 생성자를 값으로 쓸 때(`let f = Some in f 3`):** `Lower`가 클로저 `fun x -> Some x`로 바꿉니다. 그래서 `<fun>`으로 출력되고, `=`로 비교하면 인터프리터와 같은 타입 오류가 납니다.
- **출력:** 인터프리터와 같은 괄호 규칙을 씁니다. `Some (Some 1)`, `Some (-1)`, `Some None`, `Rect (2, 3)`입니다.
- **튜플 인자는 블록에 폄 (0.50.0부터):** `Rect (2, 3)`, `Node (l, v, r)`은 튜플 블록을 따로 만들지 않고 `[헤더 번호 | 5 | (n << 8), 원소...]` 블록 하나가 됩니다. 단어 수가 1이면 튜플이 아닌 인자 하나, 2 이상이면 편 원소들이라서 컴파일러가 생성자의 인자 수를 알 필요가 없습니다.
  - **패턴:** `Node (l, v, r)`은 칸 1, 2, 3을 바로 읽습니다. 위의 `Some x`처럼 인자 전체를 묶는 패턴은 단어 수가 1인지 먼저 보고, 아니면 `calc_data_payload`로 튜플을 다시 만듭니다.
  - **만들 때:** 인자가 튜플 리터럴이 아니면(위의 `Some 2`, 또는 `map Node xs`), 만들 때 인자가 튜플 블록인지 확인합니다. 튜플이면 `calc_make_data`가 폅니다.
  - **확인 코드:** 위의 MLIR에는 이 두 확인 코드가 더 붙습니다. 상수이면 clang이 접습니다.
  - **효과:** BST 20만(`benchmarks/bst.calc`)이 0.131초에서 0.064초가 되었습니다. 트리 노드 하나가 블록 둘에서 하나가 되어 GC가 표시할 객체가 반이 되었습니다.

## 10. 내장 함수 (단계 7)

```
let f = substr "hello" in (f 1 3, map length ["a"; "bb"], toInt "7")
```

`Lower`는 변수 범위를 따라가서, 범위에 없는 이름(`substr`, `length`, `toInt`)만 내장 함수로 봅니다. `map`은 prelude가 앞에 붙인 `let`으로 범위에 있으므로 보통 변수입니다(11장).

| 쓰는 모양 | Core | 생성 코드 |
|---|---|---|
| 인자가 다 모임: `toInt "7"` | `CBuiltin ("toInt", [...])` | `llvm.call @calc_builtin_toInt(%s) : (i64) -> i64` |
| partial application: `substr "hello"` | 클로저 `fun $b0 -> fun $b1 -> fun $b2 -> CBuiltin (...)`에 차례로 적용 | 클로저 3개, 마지막 함수가 `@calc_builtin_substr` 호출 |
| 값으로: `map length xs` | `CApply (CApply (CVar "map", <length 클로저>), xs)` | prelude의 `map` 클로저가 `calc_apply`로 `length` 클로저를 부름 |
| 가린 이름: `let length = ... in length x` | 보통 변수 `CVar` | 클로저 호출 |

- **선언:** 모듈은 21개 내장 함수를 모두 `llvm.func @calc_builtin_<이름>(i64, ...) -> i64`로 선언합니다. 링커가 `builtins.o`와 이름으로 연결합니다. mutable reference의 `ref`, `!`, `:=`는 런타임 함수 없이 Codegen이 바로 만듭니다(16장).
- **`calc_apply(f, x)`:** 런타임(C)이 클로저를 부를 때 씁니다. 클로저 1번 칸의 코드 포인터를 `tailcc`로 부릅니다. C는 `tailcc` 함수를 부를 수 없으므로 생성한 모듈이 `weak_odr`로 정의합니다(12장).
- **int의 빠른 경로 (0.61.0부터):**
  - **`toString`, `print`, `println`:** int이면 `calc_format_int`가 10진수 글자를 바로 씁니다. `snprintf`, 버퍼의 `malloc`/`free`, UTF-8 변환을 거치지 않습니다. `toString i` 2백만 번이 0.09초에서 0.02초, `println i` 3백만 번이 0.25초에서 0.09초(C의 `printf`는 0.13초)가 되었습니다. `sprintf`의 `%d`도 `toString`이라 같이 빨라집니다.
  - **`sort`:** 원소가 모두 int이면 tagged 값을 그대로 비교하는 merge sort(stable)입니다. 태그가 같아서 값의 순서가 int의 순서와 같습니다. 그 밖에는 `qsort`에 `calc_compare`를 넘깁니다. int 2백만 개가 0.18초에서 0.12초가 되었습니다.
- **option:** 생성 코드는 `Some`/`None`의 이름 전역을 따로 만들지 않고, 런타임의 `calc_name_some`, `calc_name_none`을 외부 전역으로 선언해서 씁니다(`llvm.mlir.global external constant`). `None`은 번호가 0이라 즉시값 `6`입니다. 0.29.0까지는 런타임의 `tryHead`, `tryFind`도 `Some`을 만들어서 패턴 검사의 포인터 비교가 맞으려면 이름이 한 곳에 있어야 했습니다. 지금은 option을 만드는 내장 함수가 없지만, 선언 없이 모든 프로그램에 있는 타입이라 그대로 런타임에 둡니다.

## 11. prelude (Calc로 쓴 표준 라이브러리)

`map`, `fold`, `head`처럼 Calc로 쓸 수 있는 함수 17개는 C 런타임에 없습니다. `src/Calc/prelude.calc`의 Calc 코드를 프로그램 앞에 붙여서, 사용자 함수와 똑같이 컴파일합니다.

```
Ast.Expr ──▶ Prelude.wrap ──▶ let rec fold f acc xs = …  in
                              let map f xs = …          in
                              <프로그램>
```

- **쓰는 정의만:** `Prelude.wrap`은 프로그램에 나오는 이름에서 시작해, 붙일 정의가 쓰는 이름을 따라 필요한 정의를 고릅니다. 위 10장의 예제는 `map`만 붙습니다(`map`이 쓰는 `reverse`는 내장 함수). 생성한 MLIR의 함수는 `@lambda_0`부터 `@lambda_7`까지 8개입니다(알려진 함수의 `_direct` 함수는 빼고 셈).
- **같은 정의:** 인터프리터도 같은 `Prelude.wrap`을 거칩니다. 차등 테스트가 같은 정의의 두 실행을 비교합니다.
- **비용:** 내장 함수였을 때는 C 루프였지만, 지금은 원소마다 Calc 함수를 부릅니다. 100만 개 리스트에 `range`, `map`, `filter`, `sum`, `exists`를 쓰는 프로그램이 0.04초에서 0.23초가 되었습니다. 0.34.0의 알려진 함수 direct call(15장)로 0.12초가 되었습니다. 0.44.0 다음에는 `reverse`, `append`, `map`, `filter`를 `fold`에 함수를 넘기지 않는 직접 재귀로 바꿔서, 원소마다 `calc_apply`로 부르던 간접 호출이 없어졌습니다(`benchmarks/msort.calc` 0.29초 → 0.20초). 0.48.0에서는 함수 인자 없이 리스트를 만들기만 하는 `reverse`, `append`, `range`를 C 내장 함수로 되돌렸습니다(0.20초 → 0.17초).
- **`upper`, `lower`, `trim`:** C 표준 라이브러리 대신 .NET에서 만든 표(`unicode.h`)를 씁니다. `ß`와 `İ`처럼 invariant 변환이 그대로 두는 문자까지 인터프리터와 같습니다.

## 12. tail call

함수 본문의 tail position에 있는 호출은 `tail` 표시를 붙여 부르고, 바로 뒤에서 그 결과를 `llvm.return`합니다. 생성한 함수는 모두 `tailcc` calling convention입니다. LLVM은 `tailcc` 함수 사이의 `tail` 호출을 최적화 수준과 상관없이 호출한 함수의 스택 프레임을 다시 쓰는 jump로 만듭니다.

```
let rec go n acc = if n = 0 then acc else go (n - 1) (acc + 1) in go 10 0
```

`go`는 함수 세 개가 됩니다. `n`을 받아 클로저를 만드는 `@lambda_0`과 `acc`를 받는 `@lambda_1`(값으로 쓸 때)이 있고, 인자를 모두 받는 `@lambda_0_direct`가 있습니다(15장). 아래는 `@lambda_0_direct`입니다(`--emit-mlir`, 일부 생략).

```mlir
llvm.func internal tailcc @lambda_0_direct(%closure: !llvm.ptr, %arg0: i64, %arg1: i64) -> i64 {
  %v1 = llvm.ptrtoint %closure : !llvm.ptr to i64                // go (바깥 클로저)
  %v3 = llvm.call @calc_eq(%arg0, %v2) : (i64, i64) -> i64       // n = 0
  ...
  llvm.cond_br %v4, ^bb1, ^bb2
^bb1:
  llvm.return %arg1 : i64                                        // acc
^bb2:
  ...                                                            // %v18 = n - 1
  %v20 = llvm.call @calc_add(%arg1, %v19) : (i64, i64) -> i64    // acc + 1
  %v21 = llvm.inttoptr %v1 : i64 to !llvm.ptr
  %v22 = llvm.call tailcc tail @lambda_0_direct(%v21, %v18, %v20) : (!llvm.ptr, i64, i64) -> i64
  llvm.return %v22 : i64
}
```

- **tail position의 분기는 블록으로:** tail position이 아닌 `if`와 `match`는 값을 내는 `scf.if`입니다. 그런데 `scf.if` 안의 호출은 뒤에 결과를 모으는 분기가 있어서 tail call이 될 수 없습니다(tail call 바로 뒤에는 `ret`만 올 수 있음). 그래서 함수 본문의 tail position은 `Codegen.emitTail`이 따로 만듭니다. 분기는 `llvm.cond_br`와 블록으로, 끝은 분기마다 `llvm.return`으로 만듭니다. tail position은 본문, `let`과 `;`의 마지막 식, `if`의 두 분기, `match`의 분기 본문입니다. 마지막 분기 뒤의 `Match failure`는 `llvm.unreachable`로 끝납니다.
- **`musttail`이 아니라 `tailcc`:** 0.31.0~0.33.0은 `musttail`을 썼습니다. `musttail`은 호출하는 함수와 호출되는 함수의 signature가 같아야 합니다. 클로저 함수가 모두 `(!llvm.ptr, i64) -> i64`일 때는 이것으로 충분했습니다. 그런데 인자를 모두 받는 direct 함수(15장)는 인자 수만큼 signature가 다릅니다. `tailcc`는 signature가 달라도 `tail` 호출을 보장합니다(arm64와 x86-64). 그래서 0.34.0부터 생성한 함수를 모두 `tailcc`로 만듭니다. 진입 함수(`@calc_main() -> i64`)는 C에서 부르므로 보통 C calling convention이고, 그 안의 호출은 tail call이 아닙니다. 진입 함수는 한 번만 불리므로 문제가 없습니다.
- **C에서 클로저 부르기:** C는 `tailcc` 함수를 부를 수 없습니다. 그래서 런타임(`calc_try`)이 클로저를 부르는 `calc_apply`를 C가 아니라 생성한 모듈에 정의합니다(보통 C calling convention 함수가 `tailcc`로 클로저를 부름). 테스트는 모듈 여럿을 한 실행 파일로 링크하므로 `weak_odr`로 정의합니다. `linkonce_odr`는 그 모듈에서 쓰지 않으면 버려져서 링크 오류가 납니다.
- **`-O2`만으로는 보장되지 않음:** `musttail`이 없던 0.30.0도 `-O2`에서는 LLVM이 대부분의 tail call을 jump로 바꿨습니다. 같은 코드를 `-O0`으로 컴파일하면 깊이 1000만에서 stack overflow로 죽었습니다(exit 138). 지금은 `-O0`에서도 통과하고, 테스트가 `Toolchain.Tools.OptimizationLevel = "-O0"`으로 이것을 확인합니다. 인자 수가 다른 함수 사이의 tail call(`one n → two n d → three n d z → one …`)도 같은 테스트에 있습니다.

## 13. 예외

`try e with …`는 `Lower`에서 클로저 두 개와 런타임 호출 하나가 됩니다.

```
try body with | p1 -> e1 | p2 -> e2
  ==>  CTry (fun () -> body,
             fun $exn -> match $exn with | p1 -> e1 | p2 -> e2 | _ -> raise $exn)
  ==>  llvm.call @calc_try(%body, %handler) : (i64, i64) -> i64
```

```c
value calc_try(value body, value handler) {
    calc_handler h;
    h.outer = calc_handlers;
    calc_handlers = &h;
    if (_setjmp(h.jump) == 0) {
        value result = calc_apply(body, CALC_UNIT);   /* 본문 */
        calc_handlers = h.outer;
        return result;
    }
    calc_handlers = h.outer;                          /* handler 안의 raise 는 바깥 try 로 */
    return calc_apply(handler, calc_raised);
}
```

- **왜 C에서 `setjmp`를 하나:** `longjmp`는 `setjmp`를 부른 함수가 아직 살아 있을 때만 돌아갈 수 있습니다. 생성한 코드에서 `setjmp`를 부르려면 그 함수에 `returns_twice`를 붙이고 `jmp_buf`의 크기를 플랫폼마다 맞춰야 합니다. 본문을 클로저로 만들어 C 함수 안에서 부르면 이 문제가 없습니다. 대신 `try`마다 클로저 두 개를 할당합니다.
- **`raise`:** 내장 함수 `calc_builtin_raise`가 `calc_raise`를 부릅니다. 가장 안쪽 `calc_try`의 `jmp_buf`로 `_longjmp`합니다. 그 사이의 생성한 코드 프레임은 그대로 버려집니다. 할당은 모두 GC가 관리하므로 정리할 것이 없습니다.
- **`_setjmp`와 `_longjmp`:** `setjmp`와 달리 시그널 마스크를 저장하지 않아서 시스템 호출이 없습니다.
- **잡히지 않은 예외:** `calc_try`가 없으면 `calc_raise`가 `Failure msg`는 `Error: msg`로, 나머지는 `Error: exception <값>`으로 출력하고 종료 코드 1로 끝냅니다(인터프리터의 `CalcException.Message`와 같음).
- **생성자:** 예외는 `exn` 타입의 생성자라서 보통 생성자처럼 이름 전역과 `DATA` 블록(인자 없는 예외는 즉시값)이 됩니다(9장). 실행 중에는 생성자 번호(이름 전역의 주소)로 구분하므로, 타입 검사가 같은 이름의 예외를 프로그램에 하나만 허용합니다.
- **tail call:** `try`의 본문과 handler는 클로저 안에서 불리므로 `try` 바깥 함수의 tail position이 아닙니다. 본문 안의 tail call은 그 클로저 안에서 그대로 tail call입니다(12장).

## 14. 문자열 포맷

`sprintf`, `printf`, `printfn`과 `$"..."`는 런타임 함수가 없습니다. 포맷 문자열 하나가 정하는 함수를 `Format.expand`가 Calc 코드로 펼치고, `Lower`가 그것을 보통 함수처럼 낮춥니다.

```
sprintf "%d is %s" 42 "x"
  ==> App (App (Printf ("sprintf", "%d is %s"), 42), "x")                  (Lower 가 리터럴을 보고 바꿈)
  ==> (fun $f0 -> fun $f1 -> toString $f0 + " is " + $f1) 42 "x"          (Format.expand)
$"a{x}"
  ==> App (Printf ("sprintf", "a%A"), x)                                   (렉서와 파서)
```

- **타입:** `Infer`는 펼친 코드를 추론하지 않고 포맷 문자열에서 바로 타입을 만듭니다(`%d`는 `int`, `%A`는 새 type variable). 펼친 코드는 `%A`의 인자를 `toString`으로 바꾸므로, 추론하면 `%d`와 `%A`를 구분할 수 없습니다.
- **범위:** 펼친 코드는 `toString`, `print`, `println`이 내장 함수인 범위에서 낮춥니다(`Lower`의 `scope`가 빈 집합). 사용자가 `toString`을 가려도 포맷의 결과는 같습니다. 인터프리터도 펼친 코드를 처음 환경(`Eval.builtins`)에서 평가합니다.
- **`sprintf`를 가린 경우:** `Lower`와 `Infer`는 `sprintf`가 아직 내장 함수일 때만 포맷으로 다룹니다. `Infer`는 처음 환경의 `sprintf`에 보통 타입 대신 표시(`$format:sprintf`)를 넣어 둡니다. 사용자가 같은 이름을 묶으면 그 표시가 사라집니다.
- **C 런타임:** 포맷만을 위한 함수는 없습니다. 모듈의 선언에서도 `sprintf`, `printf`, `printfn`은 뺍니다.
- **연결 (0.62.0부터):** 펼친 코드의 `+`는 모두 문자열 연결입니다(조각이 문자열 리터럴, `toString`의 결과, `%s`의 인자). 펼친 식은 타입 추론을 거치지 않으므로, `Lower`가 펼칠 때 그 `+`를 string으로 표시합니다(`State.MarkStringAdds`). 그래서 7장의 연쇄처럼 `calc_concatN` 한 번이 됩니다.

## 15. 알려진 함수의 direct call

커링된 함수 `fold f acc xs`를 부르면 인자 하나마다 클로저 호출이 한 번씩 일어납니다. `fold f`가 클로저를 할당하고, `(fold f) acc`가 또 할당하고, 마지막 호출에서야 본문이 실행됩니다. 원소마다 이것을 반복하는 prelude 함수(`fold`, `map`, `filter`)에서는 이 비용이 큽니다.

**이름에 묶은 함수**(`let f a b = …`, `let rec`, `and`)는 그 이름으로 부를 때 어떤 함수인지 컴파일할 때 압니다. 그래서 인자를 모두 받는 함수를 하나 더 만들고, 인자가 다 모인 호출은 그 함수를 바로 부릅니다.

```
let rec go n acc = …
  @lambda_0         (closure, n)        -> acc 를 받는 클로저   값으로 쓸 때 (map (go 1) xs)
  @lambda_1         (closure, acc)      -> 본문                 
  @lambda_0_direct  (closure, n, acc)   -> 본문                 go 10 0, 본문의 go (n - 1) (acc + 1)
```

- **Lower:** `Let`, `LetRec`, `LetRecs`의 값이 `fun a -> fun b -> …`이면(인자 표기 `let a = (a : t) in`과 결과 표기는 건너뜀) `State.AddCurried`가 두 가지를 만듭니다. 하나는 하나씩 받는 클로저들이고, 다른 하나는 `Params`가 여럿인 direct 함수(`CFunction`)입니다. 본문은 한 번 낮춘 것을 함께 씁니다. 그래서 안쪽에 정의한 함수가 두 번 생기지 않습니다.
- **같은 캡처:** direct 함수는 바깥 클로저(그 이름의 값)를 `%closure`로 받아서 같은 칸에서 캡처한 값을 꺼냅니다. 바깥 클로저의 캡처는 "본문의 free variable − 인자 − 자기 자신"이고, direct 함수에 필요한 것도 같습니다. 그래서 캡처할 값을 따로 들고 다닐 필요가 없습니다.
- **호출:** 범위의 `Known`에 있는 이름에 인자를 그 수 이상 주면 `CCallDirect (direct, CVar 이름, 인자들)`입니다. 모자라면 지금처럼 클로저 적용(partial application)이고, 남는 인자는 결과에 차례로 적용합니다. 인자가 하나인 함수는 바깥 클로저의 함수가 곧 direct 함수입니다(간접 호출 대신 직접 호출).
- **다시 묶으면 빠짐:** `let`, 함수 인자, 패턴이 같은 이름을 묶으면 `Known`에서 뺍니다(`Lower.bind`). `let f a b = … in fun f -> f 1 2`의 안쪽 `f`는 인자이므로 클로저 적용입니다.
- **이름을 먼저 정함:** 재귀 호출이 direct 함수 이름을 알아야 하므로, 바깥 클로저 이름(`lambda_N`)과 direct 함수 이름(`lambda_N_direct`)을 본문을 낮추기 전에 정합니다(`State.Reserve`). `and` 그룹은 모든 함수의 이름을 먼저 정합니다.
- **효과:** 100만 개 리스트에 `range`, `map`, `filter`, `sum`, `exists`를 쓰는 프로그램이 0.24초에서 0.12초가 되었습니다(`-O2`, 3번 잰 값). 인터프리터는 바뀌지 않았습니다.

**모르는 함수에 인자 여럿 (0.58.0부터).** `fold f acc xs`의 본문에 있는 `f acc x`는 `f`가 무엇인지 컴파일할 때 모릅니다. 그래서 `f acc`가 중간 클로저를 만들고, 그 클로저에 `x`를 적용했습니다. 원소마다 할당이 하나씩 생깁니다.

```
fun a b -> body                     클로저: [헤더 | 2 << 32, @lambda_0, 캡처한 값..., @lambda_0_direct]

f a1 a2 (f 를 모름)                 f 의 헤더 >> 32 = 2 이면 @lambda_0_direct(f, a1, a2)
                                    아니면 (f a1) a2 (지금처럼 하나씩)
```

- **클로저에 direct 함수를 넣음:** 인자가 여럿인 함수의 바깥 클로저(`CFunction.Direct`)는 마지막 칸에 direct 함수 포인터를 두고, 헤더의 32비트 위에 인자 수를 적습니다. 인자 하나짜리 클로저와 중간 클로저는 인자 수가 0입니다. 런타임은 클로저의 크기를 쓰지 않으므로 헤더의 위쪽 비트를 써도 됩니다.
- **익명 함수도:** `fun a b -> …`도 이름에 묶은 함수처럼 `State.AddCurried`로 낮춥니다. 그래서 `fold (fun acc x -> …)`의 함수에도 direct 함수가 생깁니다.
- **호출:** Codegen이 `CApply`가 겹친 모양(인자 k개, k ≥ 2)을 보면 헤더의 인자 수와 k를 비교합니다. 같으면 마지막 칸의 함수를 `(클로저, 인자 k개)`로 간접 호출하고, 다르면 하나씩 적용합니다. 인자 수보다 많거나 적게 준 호출, 인자 하나짜리 함수가 함수를 돌려주는 경우(`fun a -> (print a; fun b -> …)`)는 하나씩 적용하는 쪽입니다.
- **계산 순서:** 하나씩 적용할 때는 원래처럼 `f`, `a1`, `(f a1)`, `a2`, … 순서입니다. direct 함수를 부를 때는 `(f a1)`을 건너뛰지만, 그 호출은 클로저를 만들기만 하므로 결과가 같습니다.
- **tail position:** 두 경우 모두 블록과 `llvm.cond_br`로 나누고, 각각 `tail` 호출 뒤 `llvm.return`입니다(12장).
- **효과:** `myfold f (f acc x) r`(3천만 개)가 0.44초에서 0.21초가 되었고, 최대 메모리가 1.25GB에서 0.52GB가 되었습니다. 직접 쓴 재귀와 같습니다. prelude의 `fold`에 `fun acc x -> …`를 넘긴 프로그램은 0.44초에서 0.38초입니다.

### 튜플 인자 펴기 (0.63.0부터)

Calc에는 인자 자리의 튜플 패턴(`let f (a, b) = …`)이 없어서, 튜플을 받는 함수는 본문에서 바로 `match`합니다. 이 모양이면 direct 함수가 튜플 대신 원소를 따로 받습니다.

```
let rec go i p = match p with (a, b) -> … go (i - 1) (b, a + b)

lambda_0         (클로저로 부를 때)   p 를 받아 match 한 뒤 본문
lambda_0_direct  (이름으로 부를 때)   (클로저, i, a, b) → 본문         튜플 블록이 없음
go (i - 1) (b, a + b)                 lambda_0_direct(go, i - 1, b, a + b)
```

- **알아보는 모양 (`Lower.tupleParameters`):** 본문이 `match p with (a, b) -> body`이고 `p`가 그 함수의 인자이며, 분기가 하나, 가드가 없고, 원소 패턴이 모두 이름이나 `_`입니다. `body`가 다시 다른 인자를 같은 모양으로 `match`하면 그 인자도 폅니다(`dist p q`). 원소 이름이 다른 인자의 이름과 겹치면 direct 함수의 인자 이름이 겹치므로 펴지 않습니다.
- **두 쪽이 같은 본문:** 클로저 쪽은 전처럼 튜플을 받아 `CMatch`하고, direct 함수는 원소를 인자로 받습니다. `body`는 한 번만 낮춰서 같이 씁니다. 두 함수의 캡처한 값은 같습니다(본문의 자유 변수에서 인자와 원소 이름을 뺀 것). 본문이 `p` 자체를 쓰면 direct 함수 안에서 `let p = (a, b)`로 다시 만듭니다. 튜플은 바꿀 수 없는 값이라 결과가 같습니다.
- **호출하는 곳:** `Known.Widths`가 인자마다 편 원소 수를 기억합니다.
  - 펴는 인자가 모두 같은 크기의 튜플 리터럴이면 원소를 그 자리에서 계산해 넘깁니다. 원소는 원래처럼 왼쪽부터 계산합니다.
  - 그 밖에는 인자를 왼쪽부터 계산해 `$argN`에 묶고, 튜플에서 원소를 꺼내 부릅니다.
- **인자 하나짜리 함수도:** 펴면 바깥 클로저의 함수와 따로 `_direct` 함수를 만듭니다.
- **값으로 넘길 때 (0.63.1부터):** 모르는 함수로 인자 여럿을 주는 호출(`emitApplyMany`)은 튜플을 펴지 않습니다. 그래서 인자가 여럿인 편 함수는 튜플을 그대로 받는 함수(`_tupled`, 클로저 쪽과 같은 본문)를 하나 더 만들고, 클로저가 인자 수와 이 함수를 알립니다. 0.63.0에서는 클로저가 알리지 않아서, `fold step 0 pairs`처럼 넘긴 함수를 인자를 하나씩 적용해 불렀습니다(3백만 쌍 5번이 0.17초에서 0.27초). 지금은 다시 0.17초입니다.
  - **함수 셋:** 바깥 클로저의 함수(인자 하나씩), `_direct`(원소를 따로, 이름으로 부를 때), `_tupled`(튜플 그대로, 간접 호출)입니다. 본문은 한 번 낮춘 것을 같이 쓰지만 생성 코드는 세 곳에 있습니다.
  - **인자 하나짜리 함수:** 간접 호출의 한 번에 부르기는 인자가 둘 이상일 때만 쓰므로 `_tupled`를 만들지 않습니다.
- **효과:** `go (i - 1) (b, (a + b) % 1000003)` 5천만 번이 0.27초에서 0.20초(인자 둘로 쓴 것과 같음), `dist (i, i + 1) (i % 7, 3)` 3천만 번이 0.28초에서 0.11초가 되었습니다. 튜플을 돌려주는 함수는 그대로입니다.

### 튜플을 돌려주는 함수 (0.64.0부터)

끝(tail position)이 모두 튜플 리터럴인 함수는 direct 함수가 튜플 블록 대신 LLVM struct로 돌려줍니다. 원소는 레지스터로 돌아옵니다.

```
let divmod a b = (a / b, a % b)
match divmod i 7 with (q, r) -> …

lambda_0_direct(%closure, a, b) -> !llvm.struct<(i64, i64)>     블록을 만들지 않음
%s = llvm.call tailcc @lambda_0_direct(...)
%q = llvm.extractvalue %s[0]                                    match 하는 곳은 원소를 바로 꺼냄
```

- **고르는 조건 (`Lower.tupleResult`):** 이름에 묶은 함수이고, 본문의 끝이 모두 같은 크기(2~4)의 튜플 리터럴이거나 자기 자신에게 인자를 모두 주는 호출입니다. 끝은 `if`의 두 갈래, `match`의 분기, `let`과 `;` 뒤를 따라갑니다. 다른 함수 호출, 변수, `try`로 끝나면 하지 않습니다.
  - **자기 재귀만 허용하는 이유:** struct를 돌려주는 함수끼리만 tail call이 됩니다. 값(i64)을 돌려주는 함수를 끝에서 부르면 그 결과를 풀어야 하므로 tail call이 끊기고, 서로 부르는 재귀에서 스택이 자랍니다.
  - **크기 4까지:** 큰 struct는 backend가 레지스터 대신 메모리로 돌려줄 수 있어서, 확인한 크기까지로 정했습니다(원소 넷을 돌려주는 자기 재귀 5천만 번에서 스택이 자라지 않음).
- **세 가지 함수:** 바깥 클로저의 함수(인자 하나씩, 튜플 블록을 돌려줌), `_direct`(이름으로 부를 때, struct), `_tupled`(인자가 여럿일 때 간접 호출, 튜플 블록)입니다. 튜플 인자 펴기와 같은 구조이고, 두 변환을 같이 할 수 있습니다.
- **Codegen:** 함수마다 돌려주는 원소 수(`CFunction.Returns`)를 보고 `emitTail`이 끝을 만듭니다.
  - 튜플 리터럴은 `llvm.insertvalue`로 struct를 만들어 `llvm.return`합니다.
  - 같은 크기를 돌려주는 direct 함수 호출은 그대로 tail call입니다.
  - 값을 돌려주는 함수에서 struct를 돌려주는 함수를 부르면, 원소로 튜플 블록을 만들어 돌려줍니다. 이 경우는 tail call이 아닙니다.
- **호출하는 곳:** `match f x with (a, b) -> …`와 `let (a, b) = f x`는 분기가 모두 같은 길이의 튜플 패턴이나 `_`이면 `llvm.extractvalue`로 원소를 꺼냅니다. `match (a, b) with`를 튜플 없이 맞추는 것과 같은 길입니다(`tupleScrutinee`). 결과를 값으로 쓰면(`r := f x`, `[f x]`) 그 자리에서 튜플 블록을 만듭니다.
- **효과:** `match divmod i 7 with (q, r)` 3천만 번이 0.18초에서 0.11초(튜플 없이 쓴 것과 같음), `fib2`(재귀 결과를 `match`) 30만 번이 0.12초에서 0.08초가 되었습니다. 결과를 ref에 넣는 `p := step !p`는 블록이 필요하므로 그대로입니다.

## 16. mutable reference와 반복문

```
let s = ref 0 in for i = 1 to 10 do s := !s + i done; !s
```

- **표현:** `ref v`는 블록 `[헤더, 내용]`이고, 헤더는 `6 | (1 << 8)`입니다(`CALC_KIND_REF`). `r := v`는 1번 칸에 쓰고 `()`(immediate value `7`)를 돌려줍니다.
- **런타임 함수가 없음:** `ref`, `!`, `:=`는 내장 함수(`Eval.builtinArity`)라서 `Lower`가 보통 `CBuiltin`으로 만듭니다. Codegen은 이 세 이름을 C 호출 대신 할당, `llvm.load`, `llvm.store`로 바로 만들고 선언도 하지 않습니다(`inlineBuiltins`). `!`와 `:=`는 C 이름이 될 수 없어서 `calc_builtin_<이름>` 규칙을 쓸 수도 없습니다.

  ```
  %v6 = llvm.inttoptr %v3 : i64 to !llvm.ptr
  %v8 = llvm.getelementptr %v6[1] : (!llvm.ptr) -> !llvm.ptr, i64
  %v7 = llvm.load %v8 : !llvm.ptr -> i64                 // !s
  ```

- **런타임:** 결과 출력(`calc_buffer_value`)은 `ref 1`, `ref (Some 1)`처럼 생성자와 같은 괄호 규칙으로 출력하고, `calc_eq`는 내용을 비교합니다. 인터프리터의 `Eval.formatValue`, `Eval.valueEquals`와 같습니다.
- **GC:** `:=`는 쓴 뒤 write barrier를 부릅니다. 쓰는 값이 포인터(태그 0이나 2)일 때만 `calc_write_barrier`를 부르고, 오래된 ref에 젊은 객체를 쓰면 GC가 칸을 기록합니다(0.65.0부터, [generational-gc-design.md](generational-gc-design.md)).
- **반복문:** 파서가 `while`, `for`를 `$while`, `$for`라는 `let rec` 함수로 바꿉니다([grammar.md](grammar.md) "mutable reference와 반복문"). 인터프리터는 이 함수를 그대로 씁니다.
  - **컴파일러 (0.57.0부터):** `Lower`가 이 모양을 알아보고 Core의 `CWhile`, `CFor`로 바꿉니다. Codegen은 지금 함수 안의 `scf.while`을 만듭니다. 반복 몸체를 최상위 함수로 만들지 않으므로 클로저가 없고, 바깥 변수는 SSA 값 그대로입니다.
  - **`for`의 끝:** `for`는 `(i, 계속할지)`를 반복이 넘기고, 몸체 뒤에 `i`가 끝값 앞일 때만 `i ± 1`을 계산합니다. 그래서 끝값이 int의 끝이어도 넘치지 않습니다(파서의 `$for`와 같은 규칙).
  - **효과:** 바깥 반복 한 바퀴마다 안쪽 반복의 클로저를 만들던 비용이 없어졌습니다. 겹친 `for` 2만 × 2만이 0.17초에서 0.13초가 되었습니다.
  - **0.56.0까지:** 다음 반복은 tail position의 호출이라 `tail` 호출이 되고(12장), 이름에 묶은 함수라 direct call이었습니다(15장). 그래서 스택은 자라지 않았지만, 반복문마다 클로저를 만들었습니다.
- **속도:** `for i = 1 to 100000000 do s := !s + i % 7 done`은 0.22초이고, 같은 일을 하는 tail recursion(`let rec go i s = …`)과 같습니다(`-O2`).

## 17. 레코드

```
type p = { x: int; y: int } in let a = { y = 2; x = 1 } in { a with x = a.y }
```

- **표현:** 블록 `[헤더, 필드 이름들, 값...]`이고, 헤더는 `7 | ((1 + n) << 8)`입니다(`CALC_KIND_RECORD`). 값은 **선언한 순서**입니다. 필드 이름은 `"x;y"`처럼 `;`로 이은 C 문자열 전역 하나이고, 결과 출력(`{ x = 1; y = 2 }`)에만 씁니다. 생성자 이름과 같은 표(`StringTable.CtorName`)에 둡니다. 필드 이름은 소문자로 시작하고 `;`가 들어가므로 생성자 이름과 겹치지 않습니다.
- **Lower:** 범위(`Scope.Fields`)가 필드 이름 → 그 레코드 타입의 필드 목록을 기억합니다. `RecordDecl`이 넓히고, 같은 필드 이름의 타입을 다시 선언하면 뒤의 것이 가립니다(타입 추론과 같은 규칙).
  - `{ y = e1; x = e2 }`: 값을 쓴 순서대로 `$field_y`, `$field_x`에 묶고, 블록에는 선언한 순서로 넣습니다(`CRecord`). 계산 순서가 인터프리터와 같습니다.
  - `e.x`: 선언한 순서의 번호로 칸을 읽습니다(`CField (e, i)`, 칸 `2 + i`). 실행 중에 이름을 찾지 않습니다.
  - `{ e with x = v }`: `e`를 `$record`에 묶고, 바꾸는 필드는 새 값, 나머지는 `$record`의 칸으로 새 블록을 만듭니다.
- **패턴:** `Lower`가 레코드 패턴을 선언한 순서의 모든 필드로 채웁니다(쓰지 않은 필드는 `_`, `fillRecordFields`). Codegen은 i번째 하위 패턴을 칸 `2 + i`에서 읽어 검사하고 묶습니다. 생성자가 하나라 확인할 태그가 없으므로, 하위 패턴이 모두 irrefutable이면 검사 코드가 없습니다.
- **런타임:** `calc_buffer_value`는 이름 문자열을 `;`로 나눠 `{ x = 1; y = 2 }`를 출력하고, `calc_eq`는 값 칸을 차례로 비교합니다. 인터프리터의 `Eval.formatValue`, `Eval.valueEquals`와 같습니다.

## 18. 배열

- **표현:** 블록 `[헤더, 원소...]`이고, 헤더는 `8 | (n << 8)`입니다(`CALC_KIND_ARRAY`). 길이는 헤더의 단어 수입니다.
- **bool 배열 (0.60.0부터):** 원소가 bool이면 `[헤더 9 | (n << 8), 바이트 n개]`입니다(`CALC_KIND_BOOL_ARRAY`). 원소마다 바이트 하나(0 또는 1)라서 8바이트 원소보다 메모리가 1/8입니다.
  - **만드는 곳:** 런타임의 `Array.create`와 `Array.ofList`가 원소 값을 보고 정합니다. 타입 검사를 통과했으므로 원소 하나가 bool이면 모두 bool입니다. 리터럴은 `Array.ofList`를 거치고, prelude의 `init`, `map`, `copy`는 첫 원소로 `Array.create`합니다. 빈 배열은 어느 종류여도 됩니다(`=`는 길이와 원소로 비교).
  - **GC:** 안에 포인터가 없으므로 `calc_alloc_atomic`으로 할당합니다. GC가 내용을 훑지 않습니다.
  - **접근:** 생성 코드는 헤더의 종류를 보고 바이트(`i8` load/store, bool 값 `(b << 3) | 3`과 바꿈) 또는 단어로 읽고 씁니다. 헤더 읽기에는 따로 TBAA 태그(`#calc_tbaa_header`)를 붙입니다. 헤더는 런타임이 만든 뒤 바뀌지 않습니다. 런타임 함수는 `calc_array_get`/`calc_array_set`으로 두 종류를 다룹니다.
  - **효과:** 체 1억이 0.65초에서 0.41초, 최대 메모리가 804MB에서 103MB가 되었습니다. int 배열의 반복은 종류 검사가 더해졌지만 그대로입니다(0.83초).
- **내장 함수:** 리터럴 `[| … |]`, `a.[i]`, `a.[i] <- v`는 파서가 사용자가 쓸 수 없는 이름의 내장 함수(`[||]`, `.[]`, `.[]<-`) 호출로 만듭니다. 그래서 Lower와 Codegen은 보통 내장 함수처럼 다룹니다.
  - 이 이름은 C 이름이 될 수 없으므로 Codegen의 `builtinSymbol`이 런타임 함수 이름으로 바꿉니다. `Array.length`처럼 점이 있는 이름은 `calc_builtin_Array_length`가 됩니다.
  - 리터럴과 `Array.ofList`는 같은 C 함수(`calc_builtin_Array_ofList`)라서 선언은 한 번만 냅니다.
- **범위 검사:** 생성 코드가 원소 수(헤더의 단어 수)와 비교하고, 범위 밖이면 `calc_error_index`가 인터프리터와 같은 문장(`Index was outside the bounds of the array.`)으로 끝냅니다. 음수 인덱스는 부호 없는 수로 비교해 한 번에 잡습니다. 값으로 쓴 `.[]`는 런타임 함수 `calc_builtin_array_get`/`set`을 부릅니다.
- **prelude:** `Array.init`, `map`, `iter`, `fold`, `copy`는 prelude의 `module Array`에 Calc 코드로 있습니다. prelude도 파싱한 뒤 `Modules.resolve`로 펼치므로 `Array.init` 같은 이름의 선언이 됩니다. 프로그램의 `Array.init`은 선언하지 않은 모듈의 이름이라 바뀌지 않고, `Prelude.wrap`이 이름으로 찾아 붙입니다.
  - **반복문으로 채움 (0.61.0부터):** `init`과 `map`은 `f`의 첫 결과로 `Array.create`한 뒤 `for`로 나머지를 채우고, `fold`는 ref 하나로 돕니다. 전에는 `Array.ofList (map f (range 0 (n - 1)))`처럼 리스트를 거쳐서 원소마다 리스트 칸을 두세 개 만들었습니다. `f`를 앞 원소부터 부르는 순서는 같습니다. 3백만 개의 `init`, `map`, `fold`가 실행 파일에서 0.14초(238MB)에서 0.01초(48MB), 인터프리터에서 1.39초에서 0.72초가 되었습니다.
- **GC:** 단어 배열의 원소를 바꾼 뒤 write barrier를 부릅니다(mutable reference와 같음). bool 배열은 포인터가 없어 필요 없습니다.

## 관련 문서

- [compiler-design.md](compiler-design.md): 설계 결정(D1–D10), value representation, 단계별 계획
- [build.md](build.md): 도구 설치(`brew install llvm`), `CALC_LLVM_BIN`, GC 환경 변수, 옵션
- [testing.md](testing.md): 컴파일러 differential testing
