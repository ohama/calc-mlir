# 컴파일러 구조와 흐름

`calc --compile`이 소스 코드에서 실행 파일을 만드는 과정을 예제 하나로 따라갑니다. 설계의 이유와 단계별 계획은 [compiler-design.md](compiler-design.md)에 있습니다. 이 문서는 **지금 코드가 실제로 하는 일**을 설명합니다.

이 문서의 출력은 모두 직접 실행해서 얻었습니다. 2장의 예제는 단계 2(0.17.0), 6장의 클로저는 단계 3, 7장의 문자열은 단계 4, 8장의 패턴 매칭은 단계 5 기준입니다. 환경은 macOS arm64, Homebrew LLVM 22.1.7, bdw-gc 8.2.12입니다.

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
                     │ clang          prog.o + runtime.o + -lgc → 실행 파일│
                     └────────────────────────────────────────────────────┘
```

| 단계 | 파일 | 하는 일 |
|---|---|---|
| 파싱 | `Lexer.fsl`, `Parser.fsy` | 소스 → `Ast.Expr` (인터프리터와 같음) |
| 타입 검사 | `Infer.fs` | 통과한 프로그램만 컴파일 (`--no-typecheck`와 함께 쓸 수 없음) |
| 낮추기 | `Core.fs`, `Lower.fs` | `Ast.Expr` → Core IR. 연산을 `Prim`으로, `&&`/`||`를 `if`로, 클로저 변환(함수를 최상위로). 지원하지 않는 기능은 `NotSupported` |
| 코드 생성 | `Codegen.fs` | Core IR → MLIR 텍스트 |
| 빌드 | `Toolchain.fs` | 도구 찾기, 임시 폴더에서 외부 도구(`mlir-opt`, `mlir-translate`, `clang`)를 차례로 실행, C 런타임 오브젝트 캐시 |
| 런타임 | `runtime/runtime.c`, `runtime.h` | 초기화, 결과 출력, 오류 종료, 다형 연산(`calc_add`, `calc_eq`, `calc_compare`) |
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
  %v16 = llvm.call @calc_compare(%v14, %v15) : (i64, i64) -> i32
  ...
  %v24 = scf.if %v22 -> (i64) {
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

- **값 표현:** 모든 값은 `i64` 하나입니다. 하위 3비트가 종류를 나타내고, int `n`은 `(n << 3) | 1`, bool은 `3`(false)과 `11`(true)입니다(설계 D3).
- **함수 형태:** 함수는 모두 `llvm.func`이고, 본문에는 `arith`과 `scf`를 섞어 씁니다(설계 D2).
- **`let`:** 코드를 만들지 않습니다. `Codegen`의 환경(`Map<string, string>`, 이름 → SSA 이름)에 넣을 뿐입니다.

### ③ `mlir-opt`가 만드는 `prog.llvm.mlir`: 모두 `llvm` 방언으로

```
mlir-opt prog.mlir --convert-scf-to-cf --convert-cf-to-llvm \
         --convert-arith-to-llvm --reconcile-unrealized-casts -o prog.llvm.mlir
```

`scf.if`는 기본 블록과 분기(`llvm.cond_br %22, ^bb1, ^bb6`)로 바뀌고, `arith.*`는 `llvm.*`로 바뀝니다. 이 과정에서 상수 계산의 상당 부분이 접힙니다.

### ④ `mlir-translate`가 만드는 `prog.ll`: LLVM IR

```llvm
declare void @calc_init()                 ; (가)의 선언이 declare 로
declare void @calc_print_result(i64)
declare i32 @calc_compare(i64, i64)
...

define i64 @calc_main() {
  %1 = call i32 @calc_compare(i64 28801, i64 24001)   ; total(3600) > 3000 ?
  %2 = icmp sgt i32 %1, 0
  ...
  br i1 false, label %6, label %7                      ; 0 검사는 이미 false 로 접힘
  ...
  %12 = phi i64 [ 28801, %10 ], [ 25921, %9 ]          ; 3600 또는 3240
  ret i64 %12
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
clang prog.o $TMPDIR/calc-runtime-<해시>.o -L/opt/homebrew/opt/bdw-gc/lib -lgc \
      -Wl,-stack_size,0x20000000 -o discount
```

이 문서의 처음 판(0.17.0)에서는 `clang -O2 prog.ll runtime.c ...` 한 번으로 컴파일과 링크를 했습니다. 지금은 런타임을 한 번만 컴파일해 둡니다(4장).

최종 `calc_main`(arm64 어셈블리, `otool -tV discount`):

```
mov  w0, #0x7081          ; 28801 = 3600
mov  w1, #0x5dc1          ; 24001 = 3000
bl   _calc_compare        ; 런타임 함수라 접을 수 없음
cmp  w0, #0x0
mov  w8, #0x6541          ; 25921 = 3240
csel x0, x8, x19, gt
ret
```

## 3. 진입점 `main`

실행 파일의 진입점은 **컴파일러가 프로그램마다 만들어 넣는** `@main`입니다. C 런타임에는 `main`이 없습니다.

| 함수 | 만드는 곳 | 하는 일 |
|---|---|---|
| `main` | `Codegen.emitProgram` (프로그램마다 같은 모양) | 런타임 초기화 → `calc_main` → 결과 출력 → `return 0` |
| `calc_main` | `Codegen.emitExpr` (프로그램마다 다름) | Calc 식 전체를 계산해서 값 하나(`i64`)를 돌려줌 |
| `calc_*` | `runtime.c` | 초기화, 출력, 오류, 다형 연산 |

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

처음 `--compile`할 때 `clang -O2 -c`로 `runtime.c`를 컴파일해서 임시 폴더에 `calc-runtime-<해시>.o`로 둡니다. 해시는 런타임 소스·헤더·`clang` 경로·GC 위치로 만들고, 같은 해시의 파일이 있으면 다시 씁니다. 다른 프로세스와 동시에 만들 수 있으므로 임시 이름으로 만든 뒤 옮깁니다.

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
U _GC_init  U _GC_malloc  U _printf  U _fprintf  U _exit ← 실행할 때 동적 라이브러리에서
```

동적 라이브러리(`otool -L discount`):

```
/opt/homebrew/opt/bdw-gc/lib/libgc.1.dylib
/usr/lib/libSystem.B.dylib
```

### 호출: 보통의 C 호출 규약

- **타입 대응:** MLIR의 `i64`는 C의 `int64_t`(`value`), `i32`는 `int32_t`, 인자 없는 함수는 `void f(void)`입니다. 플랫폼의 C 호출 규약 그대로 레지스터로 넘깁니다.
- **약속을 적은 곳:** 생성 코드와 런타임 사이의 약속은 `runtime.h`에 적습니다(태그 상수, 함수 목록). 런타임 함수 이름은 모두 `calc_`로 시작합니다.
- **런타임 함수가 필요한 연산:** 타입이 여럿일 수 있는 연산(`+`, `=`, `<`)과 바깥 세계와 닿는 일(출력, 오류 종료, GC)을 런타임이 맡습니다. 정수 사칙연산처럼 타입이 하나뿐인 연산은 인라인 `arith`로 만듭니다(설계 D6).

## 5. 지금 구조의 한계

| 한계 | 결과 | 생각해 볼 해결책 |
|---|---|---|
| 실행 파일이 Homebrew의 `libgc.1.dylib`에 동적으로 의존 | `bdw-gc`가 없는 기기에서는 실행되지 않음 | `libgc.a` 정적 링크 |
| MLIR 선언(`Codegen.fs`의 문자열)과 `runtime.h`를 맞추는 검사가 없음 | 인자 타입이 어긋나도 링크는 성공하고, 실행 중 값이 틀림. 지금은 차등 테스트만 잡음 | 선언을 한 곳에서 만들기, 또는 헤더와 비교하는 테스트 |
| 런타임 호출은 최적화를 막음 | 위 어셈블리에서 `calc_compare` 호출이 남음 | 타입별 특수화(설계 단계 9) |

## 6. 함수와 클로저 (단계 3)

```
let k = 10 in let add = fun x -> x + k in add 32
```

### Lower: 클로저 변환

`fun x -> x + k`를 최상위 함수로 옮깁니다. 본문의 자유 변수 중 인자를 뺀 `k`가 캡처할 변수입니다.

```
Functions = [ { Name = "lambda_0"; Param = "x"; Self = None; Captured = ["k"];
                Body = CPrim (PAdd, [CVar "x"; CVar "k"]) } ]
Main      = CLet ("k", CInt 10,
              CLet ("add", CMakeClosure ("lambda_0", ["k"]),
                CApply (CVar "add", CInt 32)))
```

### Codegen: 함수 하나와 클로저 블록

모든 함수 값은 클로저이고, 호출 규약은 `(클로저, 인자) -> 결과` 하나입니다.

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

- **클로저 블록:** `[헤더, 코드 포인터, 캡처한 값...]`이고 `calc_alloc`(Boehm GC)으로 할당합니다. 해제 코드는 없습니다.
- **값으로서의 클로저:** 블록 주소를 그대로 `i64`로 씁니다. 8바이트 정렬이라 하위 3비트가 `000`이고, 런타임은 이것으로 정수·불리언과 구분합니다.
- **헤더:** 런타임이 헤더의 종류(1)를 보고 함수 값을 `<fun>`으로 출력합니다. 함수끼리 `=`로 비교하면 인터프리터와 같은 타입 오류로 끝냅니다.
- **`let rec f x = ...`:** 본문의 `f`는 `%closure` 자신(`llvm.ptrtoint %closure`)입니다. 자기 자신을 캡처하지 않으므로 순환 참조를 만들 필요가 없습니다.

`clang -O2`는 클로저를 만든 직후의 호출을 알아봅니다. 저장한 코드 포인터와 `k`를 다시 읽지 않고 그대로 쓰고, `lambda_0`을 펼쳐 넣습니다.

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
  %v2 = llvm.ptrtoint %v1 : !llvm.ptr to i64           // 값 = 전역의 주소 (8바이트 정렬)
  %v3 = llvm.mlir.addressof @str_1 : !llvm.ptr
  %v4 = llvm.ptrtoint %v3 : !llvm.ptr to i64
  %v5 = llvm.call @calc_add(%v2, %v4) : (i64, i64) -> i64   // 런타임이 태그와 헤더로 연결을 고름
  llvm.return %v5 : i64
}
```

- **저장:** 코드 단위는 .NET `string`과 같은 UTF-16입니다. 그래서 비교(`String.CompareOrdinal`)가 코드 단위 순서로 인터프리터와 같습니다. 예를 들어 `"😀" < "\uFFFF"`는 `true`입니다.
- **출력:** 런타임이 UTF-8로 바꿉니다. 짝이 없는 대리 코드 단위는 U+FFFD가 됩니다.
- **상수:** 전역이라 GC가 관리하지 않습니다.
- **`+`의 결과:** `GC_MALLOC_ATOMIC`으로 할당합니다. 블록 안에 포인터가 없으므로 GC가 내용을 훑지 않습니다.
- **출력 형식:** `Eval.formatValue`와 같습니다. 따옴표로 감싸고 `\\ \" \n \t \r`만 이스케이프합니다.

## 8. 리스트와 패턴 매칭 (단계 5)

```
match [7] with | x :: _ when x > 5 -> x | _ -> 0
```

```mlir
// [7] = 7 :: [] → cons 블록 [헤더 515 = 3 | (2 << 8), head 57, tail 5]
%v4 = llvm.call @calc_alloc(%v3) : (i64) -> !llvm.ptr
...
%v8 = llvm.ptrtoint %v4 : !llvm.ptr to i64

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
- **런타임:** `format_value`(출력과 Match failure 메시지)와 `equals`(구조적 `=`)가 cons와 튜플 블록을 따라갑니다. 리스트는 재귀가 아니라 반복문으로 돌아서 긴 리스트도 C 스택을 쓰지 않습니다. `equals`는 인터프리터처럼 길이를 먼저 비교합니다. 그래서 `[fun x -> x] = []`는 오류 없이 `false`입니다.

## 관련 문서

- [compiler-design.md](compiler-design.md): 설계 결정(D1–D10), 값 표현, 단계별 계획
- [build.md](build.md): 도구 설치(`brew install llvm bdw-gc`), `CALC_LLVM_BIN`과 `CALC_GC_PREFIX`, 옵션
- [testing.md](testing.md): 컴파일러 차등 테스트
