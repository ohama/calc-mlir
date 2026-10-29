---
order: 59
created: 2026-10-29
commit: da3b5f6
version: 0.34.0
description: 커링된 함수를 이름으로 부를 때 인자마다 클로저를 만들지 않도록, 인자를 모두 받는 direct 함수를 하나 더 만들고 바깥 클로저를 넘겨 캡처를 함께 쓴다. 인자 수가 달라 musttail이 깨지므로 모든 함수를 tailcc로 바꾸고, C가 부를 수 없는 calc_apply는 weak_odr로 모듈에 둔다
---

# 알려진 함수를 direct call하고 tailcc로 tail call 지키기

> 관련 커밋: [da3b5f6](https://github.com/ohama/calc-mlir/commit/da3b5f6) (v0.34.0)

커링된 언어에서 `fold f acc xs`는 함수 호출 세 번이다. `fold f`가 클로저를 할당해서 돌려주고, `(fold f) acc`가 또 하나를 할당하고, 세 번째 호출에서야 본문이 실행된다. 호출은 모두 클로저의 코드 포인터를 읽는 indirect call이다. prelude의 `fold`, `map`, `filter`는 원소마다 이것을 되풀이한다.

이 문서는 두 가지를 다룬다.

- **direct 함수:** 이름에 묶은 함수에 인자를 모두 받는 함수를 하나 더 만들어, 인자가 다 모인 호출을 direct call로 바꾸는 방법
- **tail call 보장:** 그러면 `musttail`로 보장하던 tail call이 깨지는 이유와, `tailcc`로 다시 보장하는 방법

## The Insight

**이름에 묶은 함수는 호출하는 자리에서 어떤 코드인지 안다.** `let rec go n acc = …` 안의 `go (n - 1) (acc + 1)`에서 `go`는 다시 묶지 않는 한 그 함수다. 그러면 하나씩 받는 클로저 사슬을 거칠 필요 없이 "인자 두 개를 받는 함수"를 바로 부를 수 있다.

```
let rec go n acc = …
  @lambda_0         (closure, n)        -> acc 를 받는 클로저   값으로 쓸 때 (map (go 1) xs)
  @lambda_1         (closure, acc)      -> 본문
  @lambda_0_direct  (closure, n, acc)   -> 본문                 go 10 0, 본문의 go (n - 1) (acc + 1)
```

두 가지 관찰이 구현을 작게 만든다.

1. **direct 함수의 캡처는 바깥 클로저의 캡처와 같다.** 둘 다 "본문의 free variable − 인자 − 자기 자신"이다. 그래서 direct 함수는 바깥 클로저(그 이름의 값)를 첫 인자로 받아 **같은 칸**에서 캡처한 값을 꺼낸다. 캡처를 따로 넘기는 calling convention을 새로 만들 필요가 없다.
2. **direct 함수가 생기면 함수마다 인자 수가 달라진다.** `musttail`은 부르는 함수와 불리는 함수의 인자 목록이 같아야 하므로, 인자 수를 섞어 쓰는 순간 tail call 보장이 깨진다. LLVM의 `tailcc` calling convention은 이 제약이 없다.

## Why This Matters

- **direct call이 없으면** prelude로 옮긴 내장 함수(`52-write-standard-library-as-prelude-in-the-language.md`)가 C 루프일 때보다 몇 배 느리다. 이 프로젝트의 측정 프로그램은 0.24초였고, direct call을 넣고 0.12초가 되었다(100만 개 리스트에 `range`, `map`, `filter`, `sum`, `exists`, `-O2`, 세 번 잰 값).
- **`musttail`을 그대로 두고 direct 함수를 부르면** LLVM이 거부한다.

  ```
  cannot guarantee tail call due to mismatched parameter counts
    %8 = musttail call i64 @b(ptr %0, i64 %7)
  ```

- **`musttail`을 빼고 보통 call로 두면** `-O2`에서는 대개 tail call이 되지만 `-O0`에서는 스택이 넘친다. `54-guarantee-tail-calls-with-musttail-in-mlir.md`에서 막은 문제가 그대로 돌아온다. 인자 수가 다른 두 함수가 서로를 1억 번 부르는 아래 실험에서, 보통 call은 `-O0`에서 segfault(exit 139)이고 `tailcc` + `tail`은 0을 출력한다.

## Recognition Pattern

- 커링된 언어를 클로저 변환으로 컴파일했는데, 인자가 여럿인 함수 호출이 클로저 할당과 indirect call 여러 번으로 바뀌어 느릴 때
- 함수마다 인자 수가 다른데 tail call을 `-O0`에서도 보장해야 할 때
- C 런타임이 생성한 코드의 함수를 부르는데, 생성한 함수의 calling convention을 바꾸려 할 때
- 같은 런타임 함수를 여러 모듈이 정의하고 한 실행 파일로 링크할 때

## The Approach

### Step 1: 범위에 "알려진 함수"를 함께 들고 다닌다

lowering의 범위는 원래 보이는 변수의 집합이었다. 여기에 이름 → direct 함수를 더한다. **이름을 다시 묶는 모든 곳**(`let`, 함수 인자, 패턴 변수)에서 그 이름을 알려진 함수에서 뺀다. 묶는 곳을 하나라도 빠뜨리면, 가려진 이름을 부르는 호출이 옛 함수로 가는 버그가 된다. 그래서 묶는 동작을 함수 하나(`bind`)로만 하게 한다.

```fsharp
type private Known = { Direct: string; Arity: int }
type private Scope = { Vars: Set<string>; Known: Map<string, Known> }

let private bind name scope = { Vars = Set.add name scope.Vars; Known = Map.remove name scope.Known }
let private know name known scope = { bind name scope with Known = Map.add name known scope.Known }
```

기존 코드의 `Set.add name scope`를 모두 `bind name scope`로 바꾸면, 컴파일러가 빠진 곳을 타입 오류로 알려 준다.

### Step 2: 이름을 먼저 정하고 본문을 낮춘다

재귀 호출이 direct 함수 이름을 알아야 하므로, 본문을 낮추기 **전에** 이름을 정한다. `and` 그룹은 모든 함수의 이름을 먼저 정한 다음 각 본문을 낮춘다.

```fsharp
member this.Reserve(arity: int) =
    let outer = this.FreshName()
    // 인자가 하나면 바깥 클로저의 함수가 곧 direct 함수
    outer, { Direct = (if arity = 1 then outer else outer + "_direct"); Arity = arity }
```

`fun a -> fun b -> …`에서 인자 목록을 꺼낼 때, 인자 타입 표기(`let f (a: int) b = …`가 만드는 `let a = (a : int) in …`)와 결과 표기는 값이 그대로이므로 건너뛴다. 건너뛰지 않으면 타입을 적은 함수만 direct call이 안 된다.

### Step 3: 본문은 한 번만 낮추고 두 함수가 함께 쓴다

하나씩 받는 클로저 사슬과 direct 함수의 가장 안쪽 본문은 같은 식이다. 본문을 두 번 낮추면 본문 안의 람다도 두 번 만들어진다. 한 번 낮춘 `CExpr`을 두 함수에 넣는다.

```fsharp
member this.AddCurried(outer, parameters, self, body) =
    match parameters with
    | first :: rest ->
        let inner = List.foldBack (fun p acc -> this.AddFunction(p, None, acc)) rest body
        let closure = this.AddFunctionNamed(outer, first, self, inner)
        match closure with
        | CMakeClosure (_, captured) when not rest.IsEmpty ->
            // 바깥 클로저와 같은 캡처 목록 = 같은 칸 순서
            functions.Add { Name = outer + "_direct"; Params = parameters; Self = self; Captured = captured; Body = body }
        | _ -> ()
        closure
```

`CFunction`의 인자를 `Param: string`에서 `Params: string list`로 바꿨다. 클로저 함수는 인자가 하나, direct 함수는 여럿이다.

### Step 4: 인자가 다 모인 호출만 바꾼다

호출 `f a1 a2 …`를 머리와 인자 목록으로 펴서(`spine`), 머리가 알려진 이름이고 인자가 `Arity` 이상일 때만 direct call로 바꾼다.

```fsharp
let known = scope.Known[name]
let call = CCallDirect (known.Direct, CVar name, args |> List.take known.Arity |> List.map lower)
args |> List.skip known.Arity |> List.fold (fun f a -> CApply (f, lower a)) call
```

| 호출 | 결과 |
|---|---|
| `add 1 2` (arity 2) | `CCallDirect` |
| `map (add 1) xs` | 인자가 모자람: 지금처럼 클로저 (partial application) |
| `pick false 1 2` (arity 1, 결과가 함수) | `pick false`는 direct, 남은 `1`, `2`는 `CApply` |
| `let f x = … in f 5` (`f`를 다시 묶음) | 새 `f`의 direct 함수 (옛 `f`는 범위에서 빠짐) |
| `fun f -> f 1 2` | 인자 `f`는 알려진 함수가 아님: 클로저 적용 |

생성한 MLIR (`let k = 10 in let add a b = a + b + k in add 1 2`):

```
llvm.func internal tailcc @lambda_0_direct(%closure: !llvm.ptr, %arg0: i64, %arg1: i64) -> i64 {
  %v2 = llvm.getelementptr %closure[2] : (!llvm.ptr) -> !llvm.ptr, i64
  %v1 = llvm.load %v2 : !llvm.ptr -> i64                          // 캡처한 k: 바깥 클로저의 칸 2
  %v3 = llvm.call @calc_add(%arg0, %arg1) : (i64, i64) -> i64
  %v4 = llvm.call @calc_add(%v3, %v1) : (i64, i64) -> i64
  llvm.return %v4 : i64
}
...
  %v12 = llvm.call tailcc @lambda_0_direct(%v11, %v9, %v10) : (!llvm.ptr, i64, i64) -> i64
```

### Step 5: 모든 함수를 tailcc로 만들고 tail call은 `tail`로 부른다

생성하는 함수를 모두 `tailcc`로 만들고, tail position의 호출은 `musttail` 대신 `tail`을 붙인다. MLIR 문법에서는 calling convention이 먼저, tail 종류가 다음이다.

```
llvm.func internal tailcc @lambda_0_direct(%closure: !llvm.ptr, %arg0: i64, %arg1: i64) -> i64 { ... }
%r = llvm.call tailcc tail @lambda_0_direct(%c, %n, %acc) : (!llvm.ptr, i64, i64) -> i64
llvm.return %r : i64
```

LLVM LangRef에 따르면 `tailcc`의 `tail` 호출은 인자 목록이 달라도 tail call로 만들어진다(guaranteed tail call optimization). 최적화 수준과 상관이 없으므로 `-O0`에서도 지켜진다. 클로저를 부르는 indirect call도 `llvm.call tailcc %code(...)`로 calling convention을 맞춘다. 정의와 호출의 calling convention이 다르면 undefined behavior다.

`musttail`과 달리 `tail`은 표시가 틀려도 오류를 내지 않는다. 그래서 보장은 테스트로 고정한다. 인자 수가 1, 2, 3인 함수와 클로저 호출이 서로 tail call하는 깊이 1000만 프로그램을 `-O0`으로 컴파일해서 돌린다.

### Step 6: C가 부르는 입구는 생성한 모듈에 둔다

C 런타임은 `calc_apply(f, a)`로 클로저를 부른다(`calc_try`, 리스트 비교 등). C 컴파일러는 `tailcc`로 함수를 부를 수 없다. 그래서 `calc_apply`를 생성한 모듈 안에 LLVM dialect로 정의한다. C는 보통 calling convention으로 이 함수를 부르고, 이 함수가 `tailcc`로 클로저를 부른다.

```
llvm.func weak_odr @calc_apply(%f: i64, %a: i64) -> i64 {
  %block = llvm.inttoptr %f : i64 to !llvm.ptr
  %slot = llvm.getelementptr %block[1] : (!llvm.ptr) -> !llvm.ptr, i64
  %code = llvm.load %slot : !llvm.ptr -> !llvm.ptr
  %r = llvm.call tailcc %code(%block, %a) : !llvm.ptr, (!llvm.ptr, i64) -> i64
  llvm.return %r : i64
}
```

linkage는 `weak_odr`여야 한다. 테스트는 모듈 여러 개를 한 실행 파일로 링크하므로 linkage를 고르는 데 실패한 경우가 둘 있다.

| linkage | 두 모듈을 한 실행 파일로 링크 | `-O2`에서 모듈 안에 쓰는 곳이 없을 때 |
|---|---|---|
| 기본(external) | `duplicate symbol '_calc_apply'` | 남음 |
| `linkonce_odr` | 하나만 남음 | **지워짐** → C 런타임에서 `Undefined symbols` |
| `weak_odr` | 하나만 남음 | 남음 |

`linkonce_odr`는 "쓰는 곳이 없으면 버려도 된다"는 뜻이다. 모듈 안의 생성 코드가 `calc_apply`를 부르지 않아도 C 런타임은 부르는데, 최적화기는 모듈 바깥의 C 코드를 모른다. `-O0`에서는 남아 있어서 테스트가 통과하다가 `-O2`에서만 링크가 깨진다.

## Example

인자 수가 다른 두 함수의 상호 재귀를 깊이 1억으로 돌리는 실험이다. `-O0`, arm64에서 확인했다.

```
llvm.func tailcc @a(%c: !llvm.ptr, %n: i64, %d: i64) -> i64 {
  ...
  %r = llvm.call tailcc tail @b(%c, %m) : (!llvm.ptr, i64) -> i64
  llvm.return %r : i64
}
llvm.func tailcc @b(%c: !llvm.ptr, %n: i64) -> i64 {
  %one = llvm.mlir.constant(1 : i64) : i64
  %r = llvm.call tailcc tail @a(%c, %n, %one) : (!llvm.ptr, i64, i64) -> i64
  llvm.return %r : i64
}
```

| 호출 방식 | `-O0`, 깊이 1억 |
|---|---|
| `musttail` (보통 calling convention) | `cannot guarantee tail call due to mismatched parameter counts` |
| 보통 call | segfault (exit 139) |
| `tailcc` + `tail` | `0` |

컴파일러로는 이렇게 확인한다.

```
$ cat go.calc
let rec go n acc = if n = 0 then acc else go (n - 1) (acc + 1) in go 10000000 0
$ calc --emit-mlir go.calc | grep 'tail @'
  %v17 = llvm.call tailcc tail @lambda_0_direct(%v16, %v14, %v15) : (!llvm.ptr, i64, i64) -> i64
$ calc --compile=go go.calc && ./go
10000000
```

## 체크리스트

- [ ] 이름을 묶는 모든 곳(`let`, 함수 인자, 패턴 변수, `match` 분기)에서 알려진 함수를 지우는가? (`let f a b = … in let f x = … in f 5`)
- [ ] 재귀 호출과 `and` 그룹의 상호 호출도 direct call인가? (이름을 본문보다 먼저 정했는가?)
- [ ] 인자가 모자란 호출은 클로저로, 남는 인자는 결과에 차례로 적용하는가?
- [ ] 본문을 한 번만 낮추는가? (본문 안의 람다가 두 번 생기지 않는가?)
- [ ] 정의와 모든 호출(직접, 간접)의 calling convention이 같은가?
- [ ] 인자 수가 다른 함수 사이의 tail call을 `-O0` 테스트로 고정했는가?
- [ ] C가 부르는 입구가 `tailcc`가 아닌가? 여러 모듈을 링크해도, `-O2`에서도 남는 linkage인가?

## 관련 문서

- `54-guarantee-tail-calls-with-musttail-in-mlir.md` - 이 문서 전에 쓰던 `musttail` 방식과 `-O0` 테스트
- `52-write-standard-library-as-prelude-in-the-language.md` - direct call이 필요해진 이유 (prelude의 비용)
- `30-implement-closure-conversion-to-top-level-functions.md` - 캡처와 칸 순서
- `48-compile-mutual-recursion-by-backpatching-closures.md` - `and` 그룹의 클로저
