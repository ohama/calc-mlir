---
created: 2026-10-28
description: 함수 포인터가 필요한 언어는 MLIR 함수를 처음부터 llvm.func로 만든다. llvm.mlir.addressof는 func.func를 가리킬 수 없고, 본문의 scf/cf/arith는 정해진 순서의 패스로 lowering해야 mlir-translate가 받아 준다
---

# MLIR을 llvm.func로 생성하고 LLVM IR까지 내리기

언어 구현을 MLIR 텍스트로 컴파일할 때 함수는 **처음부터 `llvm.func`로** 만든다. 본문에는 `arith`과 `scf` 같은 고수준 dialect를 섞어 쓰고, `mlir-opt` 패스 네 개로 모두 `llvm` dialect까지 내린다. 그다음 `mlir-translate`로 LLVM IR(`.ll`)을 만들고 `clang`으로 링크한다.

```
prog.mlir ─mlir-opt─▶ prog.llvm.mlir ─mlir-translate─▶ prog.ll ─clang─▶ 실행 파일
  (llvm.func +          (llvm 방언만)                    (LLVM IR)      (+ runtime.c, -lgc)
   arith/scf)
```

## The Insight

MLIR 튜토리얼은 대부분 `func.func`로 시작해서 `--convert-func-to-llvm`으로 내린다. 하지만 **함수를 값으로 다루는 순간**(클로저, 콜백) 함수의 주소가 필요하다. 주소를 얻는 연산 `llvm.mlir.addressof`는 `llvm` dialect 심볼만 가리킬 수 있다.

```
error: 'llvm.mlir.addressof' op must reference a global defined by
'llvm.mlir.global', 'llvm.mlir.alias' or 'llvm.func' or 'llvm.mlir.ifunc'
```

이 오류는 **파일을 읽을 때의 검증(verifier)** 에서 난다. 따라서 `--convert-func-to-llvm` 패스를 추가해도 소용없다. 패스가 돌기 전에 이미 실패한다(직접 확인함). 그러니 `func.func`와 `llvm.func`를 섞지 말고, 처음부터 모든 함수를 `llvm.func`로 만든다.

이렇게 해도 함수 **본문**에는 `arith.addi`, `arith.cmpi`, `scf.if`를 그대로 쓸 수 있다. dialect는 연산 단위로 섞을 수 있고, 함수를 감싸는 연산만 `llvm.func`면 된다.

## Why This Matters

- **`func.func`로 시작한 경우:** 단계 1~2(정수, `if`, `let`)는 잘 된다. 그런데 클로저를 넣는 단계에서 모든 함수 정의와 호출(`func.call` → `llvm.call`, `return` → `llvm.return`)을 다시 써야 한다.
- **패스가 빠진 경우:** `mlir-opt`는 성공한다. 오류는 다음 단계인 `mlir-translate`에서 `Dialect 'scf' not found` 같은 모호한 메시지로 난다. 어느 패스가 빠졌는지 메시지만 보고는 알기 어렵다.

## Recognition Pattern

- 클로저, higher-order function, 함수 포인터 테이블이 있는 언어를 MLIR로 컴파일하려 할 때
- `mlir-translate`가 `Dialect 'X' not found for custom op`를 낼 때. X dialect를 내리는 패스가 빠진 것이다.
- `llvm.mlir.addressof`에서 "must reference a global" 오류가 날 때

## The Approach

**dialect마다 "누가 이것을 llvm으로 내리나"를 확인한다.** `mlir-translate --mlir-to-llvmir`는 `llvm` dialect(과 `builtin`)만 받는다. 생성하는 dialect 하나하나에 대응하는 변환 패스가 필요하다.

| 생성하는 연산 | lowering 경로 | 필요한 패스 |
|---|---|---|
| `scf.if`, `scf.while` | scf → cf → llvm | `--convert-scf-to-cf`, `--convert-cf-to-llvm` |
| `arith.*` | arith → llvm | `--convert-arith-to-llvm` |
| 패스 사이에 생긴 형변환 | 정리 | `--reconcile-unrealized-casts` |
| `llvm.func`, `llvm.call`, `llvm.mlir.addressof` | 이미 llvm | 없음 |

### Step 1: 도구 찾기

macOS Homebrew의 `llvm`은 keg-only라서 PATH에 없다. `mlir-opt`도 같은 폴더에 있다.

```bash
brew install llvm
B=/opt/homebrew/opt/llvm/bin
$B/mlir-opt --version     # Homebrew LLVM version 22.1.7
```

### Step 2: 함수는 llvm.func, 본문은 arith/scf

```mlir
llvm.func @main() -> i32 {
  %a = arith.constant 3 : i64
  %b = arith.constant 5 : i64
  %c = arith.cmpi slt, %a, %b : i64
  %r = scf.if %c -> (i32) {
    %x = arith.constant 7 : i32
    scf.yield %x : i32
  } else {
    %y = arith.constant 9 : i32
    scf.yield %y : i32
  }
  llvm.return %r : i32
}
```

반환은 `return`이 아니라 `llvm.return`이고, 호출은 `llvm.call`이다.

### Step 3: 네 개의 패스, 그다음 translate와 clang

```bash
$B/mlir-opt prog.mlir \
  --convert-scf-to-cf --convert-cf-to-llvm \
  --convert-arith-to-llvm --reconcile-unrealized-casts -o prog.llvm.mlir
$B/mlir-translate --mlir-to-llvmir prog.llvm.mlir -o prog.ll
$B/clang -O2 -Wno-override-module prog.ll runtime.c -o prog
./prog; echo $?    # 7
```

`-Wno-override-module`은 `.ll`에 target triple이 없어서 clang이 내는 경고(`overriding the module target triple`)를 끈다. 결과에는 영향이 없다.

패스를 하나씩 빼 보면 오류가 이렇게 바뀐다(위 예제로 확인함).

| 빠진 패스 | `mlir-translate` 오류 |
|---|---|
| `--convert-scf-to-cf` | `Dialect 'scf' not found for custom op 'scf.if'` |
| `--convert-cf-to-llvm` | `Dialect 'cf' not found for custom op 'cf.cond_br'` |
| `--convert-arith-to-llvm` | `Dialect 'arith' not found for custom op 'arith.constant'` |

`scf`를 `cf`로 내리면 새로 `cf` 연산이 생긴다. 그래서 `scf-to-cf`가 `cf-to-llvm`보다 앞에 와야 한다.

## Example

함수 포인터로 호출하기. `@inc`가 `func.func`면 첫 줄의 `addressof`에서 검증 오류가 난다.

```mlir
// ❌ BAD: func.func 는 addressof 의 대상이 될 수 없음 (패스 전에 검증 실패)
func.func @inc(%x: i64) -> i64 { ... return %r : i64 }

// ✅ GOOD: llvm.func 로 정의하고 본문은 arith 그대로
llvm.func @inc(%x: i64) -> i64 {
  %c = arith.constant 1 : i64
  %r = arith.addi %x, %c : i64
  llvm.return %r : i64
}
llvm.func @main() -> i32 {
  %f = llvm.mlir.addressof @inc : !llvm.ptr
  %a = arith.constant 41 : i64
  %r = llvm.call %f(%a) : !llvm.ptr, (i64) -> i64     // 간접 호출
  %t = arith.trunci %r : i64 to i32
  llvm.return %t : i32                                  // 종료 코드 42
}
```

외부 C 함수는 본문 없이 선언만 한다. 예: `llvm.func @calc_print_result(i64)`

## 체크리스트

- [ ] 모든 함수가 `llvm.func`인가? (`func.func`를 섞지 않음)
- [ ] 반환과 호출이 `llvm.return`, `llvm.call`인가?
- [ ] 생성하는 dialect마다 내리는 패스가 있는가? (scf → cf → llvm, arith → llvm)
- [ ] 마지막에 `--reconcile-unrealized-casts`가 있는가?
- [ ] Homebrew LLVM 경로(`/opt/homebrew/opt/llvm/bin`)를 직접 지정했는가? (PATH에 없음)

## 관련 문서

- `represent-values-as-tagged-words.md` - 생성한 코드가 다루는 value representation
- `run-external-toolchain-from-dotnet.md` - 이 파이프라인을 .NET에서 실행하기
- 이 프로젝트: `docs/compiler-design.md`, `src/Calc/Codegen.fs`, `src/Calc/Toolchain.fs`
