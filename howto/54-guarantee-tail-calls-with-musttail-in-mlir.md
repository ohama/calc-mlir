---
order: 54
created: 2026-10-28
commit: 0b5f95e
version: 0.31.0
description: clang -O2가 tail call을 jump로 바꿔 주는 것은 보장이 아니다. 같은 IR을 -O0으로 컴파일하면 깊이 1000만에서 죽는다. tail position의 호출을 musttail로 부르고 바로 ret한다. scf.if 안에서는 musttail이 될 수 없으므로 tail position의 분기만 블록과 llvm.cond_br로 만들고, -O0 테스트로 보장을 확인한다
---

# MLIR에서 musttail로 tail call 보장하기

> 관련 커밋: [0b5f95e](https://github.com/ohama/calc-mlir/commit/0b5f95e) (v0.31.0)

ML 계열 언어에서 반복문은 tail recursion이다. `let rec go n acc = if n = 0 then acc else go (n - 1) (acc + 1)`이 깊이에 상관없이 돌아야 한다. 컴파일한 Calc 프로그램은 이미 그렇게 도는 것처럼 보였다.

```
$ calc --compile ./t -e "let rec go n acc = if n = 0 then acc else go (n - 1) (acc + 1) in go 100000000 0"
$ ./t
100000000                       ← 1억 번, 스택 512MB 로는 프레임을 쌓아서는 불가능
```

mutual recursion, `match`와 가드, `;`, 함수 인자로 받은 함수까지 여덟 가지 모양이 모두 1억 번 통과했다. 그런데 이것은 컴파일러가 보장한 것이 아니었다.

## The Insight

**`-O2`가 해 주는 tail call은 최적화일 뿐이고, 보장은 `musttail`만 한다.** 생성한 LLVM IR을 그대로 두고 최적화 수준만 바꿔 보면 드러난다.

```bash
clang -O0 -c prog.ll -o p.o && clang p.o runtime.o builtins.o -lgc ... -o p && ./p
```

| 같은 `prog.ll` | 결과 |
|---|---|
| `clang -O0` | exit 138 (stack overflow) |
| `clang -O1`, `-O2` | 통과 |

LLVM의 tail call elimination과 블록 정리가 "호출 → 결과를 모으는 블록으로 분기 → `ret`"을 "호출 → `ret`"으로 바꾸고, 백엔드가 그것을 jump로 만든다. 모두 최적화 pass라서, 최적화를 끄거나 모양이 조금만 달라져도 사라진다. 보장이 필요하면 LLVM에 직접 요구해야 한다.

```llvm
%r = musttail call i64 %code(ptr %closure, i64 %arg)
ret i64 %r
```

`musttail`은 "반드시 tail call로 만들어라, 못 하면 오류"다. 대신 조건이 까다롭다.

1. 호출 **바로 다음**이 그 결과를 돌려주는 `ret`이어야 한다.
2. 호출하는 함수와 호출되는 함수의 **signature가 같아야** 한다(calling convention 포함).

## Why This Matters

- **`-O2`만 믿은 경우:** 디버그 빌드, 다른 최적화 설정, 다른 LLVM 버전, 함수 모양의 작은 변화(예: 호출 뒤에 GC 관련 코드가 붙음)로 갑자기 stack overflow가 날 수 있다. 원인을 찾기 어렵다.
- **`scf.if` 안에 `musttail`을 넣은 경우:** `scf.if`는 값을 `scf.yield`로 내보내고, lowering하면 결과를 모으는 블록으로 분기한다. 호출 바로 뒤가 `ret`이 아니므로 LLVM 검증이 실패한다.
- **signature가 다른 경우:** C 런타임의 `calc_apply(value f, value x)`를 거쳐 부르면 `(i64, i64)`와 `(ptr, i64)`가 달라서 `musttail`을 쓸 수 없다.

## Recognition Pattern

- 함수형 언어를 LLVM이나 MLIR로 컴파일하는데 반복을 재귀로 표현할 때
- 깊은 재귀 테스트가 Release에서는 통과하고 다른 설정에서 죽을 때
- 구조적 제어 흐름(`scf.if`, `scf.while`)으로 함수 본문을 만들고 있을 때

## The Approach

### Step 1: 지금이 최적화 덕분인지 잰다

`--keep-temps`로 중간 파일(`prog.ll`)을 남기고, 같은 `.ll`을 `-O0`, `-O1`, `-O2`로 컴파일해서 돌린다. `-O0`만 죽으면 보장이 없는 것이다. 이전 버전과 비교하려면 `git worktree add <폴더> HEAD`로 따로 빌드한다.

### Step 2: 모든 클로저 함수의 signature를 같게 둔다

```mlir
llvm.func internal @lambda_N(%closure: !llvm.ptr, %arg: i64) -> i64
```

Calc의 함수는 모두 인자 하나짜리 클로저라 이미 signature가 같다. 호출도 런타임 함수를 거치지 않고, 클로저의 1번 칸에서 코드 포인터를 꺼내 바로 부른다. 그래서 어떤 클로저든 다른 클로저를 `musttail`로 부를 수 있다. 진입 함수(`@calc_main() -> i64`)는 signature가 달라서 tail call을 하지 않는다. 한 번만 불리므로 상관없다.

### Step 3: tail position만 따로 생성한다

`emitExpr`(값을 SSA 이름으로 돌려줌) 옆에, 끝을 직접 만드는 `emitTail`을 둔다. tail position은 본문, `let`과 `;`의 마지막 식, `if`의 두 분기, `match`의 분기 본문이다.

```fsharp
and private emitTail (fb: FunctionBuilder) (env: Map<string, string>) (expr: CExpr) : unit =
    match expr with
    | CLet (name, value, body) -> emitTail fb (Map.add name (emitExpr fb env value) env) body
    | CIf (cond, thenExpr, elseExpr) ->
        let c = untagBool fb (emitExpr fb env cond)
        branch c (fun () -> emitTail fb env thenExpr) (fun () -> emitTail fb env elseExpr)   // llvm.cond_br + 블록
    | CApply (f, a) -> fb.Emit(sprintf "llvm.return %s : i64" (emitApply fb env "musttail " f a))
    | CMatch (scrutinee, cases) -> (* 분기마다 llvm.cond_br, 마지막은 calc_match_failure 다음 llvm.unreachable *)
    | _ -> fb.Emit(sprintf "llvm.return %s : i64" (emitExpr fb env expr))
```

- tail position의 분기는 `scf.if`가 아니라 **함수 영역의 블록과 `llvm.cond_br`** 다. 분기마다 자기 `llvm.return`으로 끝나므로 결과를 모을 필요가 없다.
- tail position이 아닌 곳(`1 + f x`의 `f x`, `if`의 조건)은 그대로 `emitExpr`과 `scf.if`다. 한 함수 안에 블록과 `scf.if`가 섞여도 `--convert-scf-to-cf`가 처리한다.
- 분기 전에 만든 SSA 값은 두 블록을 모두 dominate하므로, 이름 → SSA 이름 환경을 그대로 넘기면 된다.

### Step 4: MLIR 문법을 작게 먼저 확인한다

```mlir
%r = llvm.call musttail %code(%closure, %v) : !llvm.ptr, (!llvm.ptr, i64) -> i64
llvm.return %r : i64
```

블록, `scf.if`, `musttail`을 섞은 작은 파일을 `mlir-opt` → `mlir-translate` → `clang -O0`으로 통과시켜 본 뒤에 코드 생성을 고친다. `.ll`에 `musttail call` 다음 줄이 `ret`인지 본다.

### Step 5: -O0 테스트로 보장을 고정한다

`-O2`에서는 `musttail`이 없어도 통과하므로, `-O2` 테스트로는 회귀를 잡지 못한다. 도구 설정에 생성 코드의 최적화 수준을 두고, 테스트에서 `-O0`으로 깊은 tail recursion을 돌린다.

```fsharp
type Tools = { ...; OptimizationLevel: string }          // 기본 "-O2"

compileAndRunWith (fun t -> { t with OptimizationLevel = "-O0" }) input   // 깊이 1000만, 다섯 가지 모양
```

MLIR 텍스트 테스트도 둔다. `musttail` 다음 줄이 바로 그 결과의 `llvm.return`인지, tail position이 아닌 호출에는 `musttail`이 없는지 확인한다.

## Example

```mlir
llvm.func internal @lambda_0(%closure: !llvm.ptr, %arg: i64) -> i64 {     // go 의 두 번째 인자 acc
  ...
  llvm.cond_br %v7, ^bb1, ^bb2                                             // n = 0
^bb1:
  llvm.return %arg : i64
^bb2:
  %v25 = llvm.call %v23(%v22, %v21) : !llvm.ptr, (!llvm.ptr, i64) -> i64           // go (n - 1): 보통 호출
  ...
  %v31 = llvm.call musttail %v29(%v28, %v27) : !llvm.ptr, (!llvm.ptr, i64) -> i64  // (go (n - 1)) (acc + 1)
  llvm.return %v31 : i64
}
```

`go (n - 1) (acc + 1)`은 커링되어 호출이 둘이다. 안쪽 `go (n - 1)`은 결과에 다시 적용하므로 tail call이 아니고, 바깥 적용만 `musttail`이다.

| 같은 프로그램 (깊이 1000만, 다섯 가지 모양) | `-O0` |
|---|---|
| 0.30.0 (`musttail` 0개) | exit 138 |
| 0.31.0 (`musttail` 7개) | `(0, 0, true, 10000000, 1)` |

## 체크리스트

- [ ] 같은 `.ll`을 `-O0`으로 컴파일해서 깊은 tail recursion을 돌려 봤는가?
- [ ] tail position의 호출이 `musttail`이고 바로 다음이 그 결과의 `ret`인가?
- [ ] 호출하는 함수와 호출되는 함수의 signature가 같은가?
- [ ] tail position의 분기를 `scf.if`가 아니라 블록과 `cond_br`로 만드는가?
- [ ] `Match failure`처럼 돌아오지 않는 끝은 `unreachable`로 닫는가?
- [ ] `-O0`에서 도는 테스트가 있는가?

## 관련 문서

- `28-emit-short-circuit-with-scf-if-regions.md` - 값을 내는 `scf.if`
- `30-implement-closure-conversion-to-top-level-functions.md` - 모든 클로저 함수의 같은 signature
- `31-link-large-native-stack-for-deep-recursion.md` - tail call이 아닌 재귀의 스택 한계
- `53-enable-fsharp-tailcalls-in-debug-for-interpreters.md` - 인터프리터 쪽의 같은 문제
