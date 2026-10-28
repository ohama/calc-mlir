---
created: 2026-10-28
description: 클로저 변환은 본문을 먼저 낮춘 뒤 그 자유 변수에서 인자와 자기 자신을 빼서 캡처 목록을 얻는다. 함수 입구에서 캡처한 값을 꺼내 이름 → SSA 환경에 넣으면 본문은 let과 같은 방식으로 생성되고, let rec은 클로저 포인터 자신이 된다
---

# 클로저 변환으로 함수를 최상위로 옮기기

MLIR/LLVM의 함수는 모두 최상위에 있고, 바깥 함수의 지역 변수를 볼 수 없다. 그래서 `fun x -> x + k`처럼 바깥 변수를 쓰는 함수는 **클로저 변환**을 거친다. 본문은 최상위 함수 `@lambda_N(%closure, %arg)`가 되고, 바깥 변수는 클로저 블록에 담아 넘긴다.

```
let k = 10 in let add = fun x -> x + k in add 32

Functions = [ { Name = "lambda_0"; Param = "x"; Self = None; Captured = ["k"];
                Body = CPrim (PAdd, [CVar "x"; CVar "k"]) } ]
Main      = CLet ("k", CInt 10,
              CLet ("add", CMakeClosure ("lambda_0", ["k"]),
                CApply (CVar "add", CInt 32)))
```

## The Insight

**캡처 목록은 "이미 낮춘 본문"의 자유 변수에서 얻는다.** 안쪽 함수부터 변환하면 본문 안의 중첩 함수는 이미 `CMakeClosure (name, captured)`가 되어 있다. 이 노드의 자유 변수는 `captured` 자체다. 그래서 자유 변수 함수는 중첩 함수 안으로 들어갈 필요가 없고, 커링처럼 여러 단계로 중첩된 함수도 캡처가 바깥으로 저절로 전파된다.

```fsharp
member _.AddFunction(param: string, self: string option, body: CExpr) =
    let name = sprintf "lambda_%d" functions.Count
    let bound = Set.ofList (param :: Option.toList self)
    let captured = Set.toList (Set.difference (freeVars body) bound)   // 정렬된 순서 = 칸 순서
    functions.Add { Name = name; Param = param; Self = self; Captured = captured; Body = body }
    CMakeClosure (name, captured)

| Lambda (param, body) -> state.AddFunction(param, None, lower state body)   // 본문 먼저
```

`fun a -> fun b -> a + b`는 안쪽 함수가 먼저 만들어진다. 안쪽 함수(`b`)는 `a`를 캡처하고, 바깥 함수(`a`)는 아무것도 캡처하지 않는다.

```
lambda_0: Param = "b", Captured = ["a"]
lambda_1: Param = "a", Captured = []      // 본문 = CMakeClosure ("lambda_0", ["a"])
```

**변수 종류를 IR에 새기지 않아도 된다.** 변수를 `Local | Arg | Captured of index | Self`로 나누는 설계가 흔하다. 하지만 함수 입구에서 캡처한 값을 모두 꺼내 환경에 넣으면, 본문에서는 모든 변수가 "이름 → SSA 값"으로 같아진다. 코드 생성기는 `let`을 처리하던 방식 그대로 함수 본문을 만든다.

```fsharp
let captured = f.Captured |> List.mapi (fun i name -> name, loadSlot "%closure" (2 + i))
let self = match f.Self with Some name -> [ name, ptrtoint "%closure" ] | None -> []
// 나중 것이 이김: 인자 > 자기 자신 > 캡처
let env = Map.ofList (captured @ self @ [ f.Param, "%arg" ])
```

## Why This Matters

- **자유 변수를 원래 AST에서 구한 경우:** 중첩 함수를 처리할 때마다 그 안까지 다시 훑어야 합니다. 안쪽 함수의 캡처가 바깥 함수의 캡처에도 들어가야 한다는 점을 따로 챙겨야 하는데, 빠뜨리면 커링한 함수에서 "환경에 없는 이름"이 나옵니다.
- **`let rec`의 자기 자신을 캡처한 경우:** 클로저가 자기를 가리켜야 해서 할당한 뒤 다시 채우는(backpatch) 코드가 필요합니다. 자기 자신은 이미 `%closure`로 들어오므로 캡처할 필요가 없습니다.
- **우선순위를 틀린 경우:** `let rec f f = f in f 5`가 `<fun>`을 냅니다. 인터프리터는 `5`를 냅니다(인자가 자기 자신을 가림).

## Recognition Pattern

- 일급 함수가 있는 언어를 최상위 함수만 있는 대상(C, LLVM, MLIR `llvm.func`)으로 컴파일할 때
- 커링, 부분 적용, 고차 함수가 있을 때
- 코드 생성기가 "이 변수는 어디에 있나"를 경우마다 따지고 있을 때

## The Approach

### Step 1: 호출 규약을 하나로 정한다

언어의 함수가 원래 인자 하나(커링)라면 모든 함수 값을 클로저로 만들고 호출 규약을 하나로 둔다.

```mlir
llvm.func @lambda_N(%closure: !llvm.ptr, %arg: i64) -> i64
```

- **호출:** 클로저의 1번 칸에서 코드 포인터를 꺼내 간접 호출합니다.
- **`add 1 2`:** 중간 클로저가 하나 생깁니다. 느리지만 규칙이 하나뿐입니다. 직접 호출과 여러 인자 한 번에 받기(uncurrying)는 최적화 단계에서 할 일입니다.

### Step 2: 블록 모양을 정한다

```
[0] 헤더 = 종류(1) | (뒤따르는 단어 수 << 8)
[1] 코드 포인터 (llvm.mlir.addressof @lambda_N)
[2..] 캡처한 값 (Captured 순서)
```

헤더는 런타임이 함수 값을 알아보는 데 쓴다. 예를 들어 `<fun>`을 출력하거나 함수끼리 `=` 비교를 오류로 끝낸다. 블록 주소는 8바이트 정렬이라 그대로 `i64` 값이 된다. 하위 3비트가 `000`이어서 정수(`001`)와 불리언(`011`)과 구분된다.

### Step 3: 본문을 먼저 낮추고, 자유 변수에서 인자와 자기 자신을 뺀다

`freeVars`는 낮춘 IR에서 정의한다. `CLet`은 묶는 이름을 빼고, `CMakeClosure`는 캡처 목록을 그대로 돌려준다.

```fsharp
| CLet (name, value, body) -> Set.union (freeVars value) (Set.remove name (freeVars body))
| CMakeClosure (_, captured) -> Set.ofList captured
```

### Step 4: `let rec`은 자기 자신을 `%closure`로

```
let rec fact n = ... fact (n - 1) ...  in fact 10
→ lambda_0: Param = "n", Self = Some "fact", Captured = []
→ CLet ("fact", CMakeClosure ("lambda_0", []), CApply (CVar "fact", CInt 10))
```

본문의 `fact`는 `llvm.ptrtoint %closure`다. 인터프리터의 `VRecClosure`(호출할 때 자기 자신을 환경에 넣음)와 같은 생각이다.

### Step 5: 환경에 없는 이름은 바깥에서 터지게 둔다

내장 함수(`length` 등)를 쓰는 람다는 그 이름을 캡처 목록에 넣는다. 그 클로저를 만드는 바깥 위치에서 환경을 찾다가 실패한다. 이때 "아직 지원하지 않는 내장 함수"로 알린다. 클로저 변환이 따로 처리할 필요가 없다.

## Example

LLVM은 클로저를 만든 직후의 호출을 알아본다. `clang -O2` 뒤의 `calc_main`(직접 확인함):

```llvm
define i64 @calc_main() {
  %1 = tail call ptr @calc_alloc(i64 24)
  store i64 513, ptr %1                        ; 헤더
  ...                                          ; 코드 포인터와 k 저장
  %4 = tail call i64 @calc_add(i64 257, i64 81)   ; add 32 → lambda_0 이 펼쳐짐
  ret i64 %4
}
```

방금 저장한 코드 포인터를 다시 읽는 대신 저장한 값을 그대로 쓰고(store→load 전달), 간접 호출이 직접 호출이 되어 펼쳐졌다. 균일한 호출 규약의 비용 일부는 LLVM이 알아서 없앤다.

커링한 `add`로 `fib 30`을 계산하면 클로저 할당이 100만 번을 넘는다. 이것을 컴파일한 실행 파일은 약 0.01초, 최대 메모리 3MB로 끝났다. 인터프리터는 0.94초, 70MB였다. Boehm GC는 `i64`로 저장한 블록 주소를 보수적으로 스캔해서 찾아내고 회수한다.

## 체크리스트

- [ ] 호출 규약이 하나인가? (`(클로저, 인자) -> 결과`)
- [ ] 본문을 먼저 낮추고 그 결과에서 자유 변수를 구하는가?
- [ ] 캡처 목록에서 인자와 `let rec`의 자기 자신을 뺐는가?
- [ ] 함수 입구의 환경 우선순위가 인자 > 자기 자신 > 캡처인가?
- [ ] 캡처 순서가 결정적인가? (정렬된 `Set`)
- [ ] 이름 번호(`lambda_N`) 상태가 호출마다 새로 만들어지는가? (병렬 테스트)

## 관련 문서

- `compile-immutable-let-to-ssa-environment.md` - 함수 본문이 그대로 쓰는 이름 → SSA 환경
- `implement-closures-and-let-rec.md` - 인터프리터의 클로저와 `VRecClosure`
- `represent-values-as-tagged-words.md` - 블록 주소가 그대로 값이 되는 이유
- `emit-mlir-as-llvm-func-and-lower.md` - `llvm.mlir.addressof`에 `llvm.func`가 필요한 이유
