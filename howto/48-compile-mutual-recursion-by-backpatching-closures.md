---
order: 48
created: 2026-10-28
commit: caef51e
version: 0.27.0
description: let rec f ... and g ...의 클로저는 서로를 가리켜서 하나씩 완성할 수 없다. 그룹의 클로저 블록을 모두 먼저 할당하고, 서로를 가리키는 칸은 그다음에 채운다. 자기 자신은 단일 let rec처럼 %closure로 쓰고, 인터프리터는 호출할 때 그룹 환경을 다시 만든다
---

# mutual recursion 클로저를 할당한 뒤 칸을 채워 만들기

> 관련 커밋: [caef51e](https://github.com/ohama/calc-mlir/commit/caef51e) (v0.27.0)

`let rec even n = … odd (n - 1) and odd n = … even (n - 1)`에서 `even`의 클로저는 `odd`를 캡처하고, `odd`의 클로저는 `even`을 캡처한다. 클로저 블록을 만들 때 캡처한 값을 채우는데, 서로가 아직 없으니 **한 번에 하나씩 완성할 수 없다.**

단일 `let rec`은 이 문제가 없었다. 자기 자신은 캡처하지 않고 호출 규약의 `%closure`를 쓰기 때문이다(`30-implement-closure-conversion-to-top-level-functions.md`). 그룹에서는 형제 함수를 그렇게 얻을 수 없다.

## The Insight

**블록의 "주소"는 내용을 채우기 전에 정해진다.** 그래서 그룹의 클로저 블록을 모두 먼저 할당해 주소를 얻는다. 그다음 형제를 가리키는 칸에 그 주소를 쓴다. 할당한 블록은 아직 아무도 부르지 않았으니, 칸이 잠시 비어 있어도 문제가 없다.

```
1. even 블록 할당: [헤더, @even 코드, ?, ?]        → 주소 A
2. odd  블록 할당: [헤더, @odd  코드, ?, ?]        → 주소 B
3. 칸 채우기: A[odd 자리] ← B,  B[even 자리] ← A,  바깥 변수 k 도 채움
4. 본문에서 even = A, odd = B
```

클로저 변환은 거의 그대로다. 각 함수의 캡처 목록은 "본문의 free variable − 인자 − 자기 자신"이고, 여기에 형제 이름이 자연스럽게 들어간다. 바뀌는 것은 코드 생성 순서뿐이다.

## Why This Matters

- **형제를 차례로 만든 경우:** 첫 클로저를 만들 때 두 번째 클로저가 없어서 빈 값(또는 이전 변수)을 캡처합니다. 호출하면 엉뚱한 곳으로 점프합니다.
- **그룹을 튜플 하나로 묶고 매번 꺼낸 경우:** 동작은 하지만, 호출마다 간접 단계가 하나 더 생기고 함수 값 하나를 따로 넘길 수 없게 됩니다(`map even xs`).
- **불변 데이터만 쓰는 언어라고 칸 쓰기를 피한 경우:** 런타임 블록은 컴파일러가 만든 것이라 다 만들기 전에 고쳐도 됩니다. 사용자에게 보이는 값은 다 채운 뒤의 블록뿐입니다.

## Recognition Pattern

- 서로를 참조하는 클로저(mutual recursion, 재귀 모듈, 객체의 메서드)를 컴파일할 때
- 클로저 변환 결과에서 캡처 목록이 서로를 포함할 때(순환)

## The Approach

### Step 1: `Lower`는 그룹의 클로저를 묶어 둔다

```fsharp
| LetRecs (bindings, inExpr) ->
    let groupScope = bindings |> List.fold (fun acc (name, _, _) -> Set.add name acc) scope
    let closures =
        [ for name, param, body in bindings ->
              name, state.AddFunction(param, Some name, lower state ctors (Set.add param groupScope) body) ]
    CLetRecGroup (closures, lower state ctors groupScope inExpr)
```

`AddFunction`은 자기 자신(`Some name`)을 캡처에서 빼므로, 캡처 목록에는 형제와 바깥 변수만 남는다. `CLetRecGroup`의 free variable은 그룹 이름을 뺀 나머지다.

### Step 2: `Codegen`은 할당과 칸 채우기를 나눈다

```fsharp
| CLetRecGroup (closures, body) ->
    let groupNames = closures |> List.map fst |> Set.ofList
    // 1. 모두 할당: 헤더와 코드 포인터만
    let blocks = [ for name, CMakeClosure (funcName, captured) in closures -> (* alloc, header, code *) ]
    let groupEnv = blocks |> List.fold (fun acc (name, _, value, _) -> Map.add name value acc) env
    // 2. 캡처 칸 채우기: 형제는 방금 만든 주소, 나머지는 바깥 값
    for _, block, _, captured in blocks do
        captured |> List.iteri (fun i c ->
            let v = if groupNames.Contains c then groupEnv[c] else emitExpr fb env (CVar c)
            fb.Emit(sprintf "llvm.store %s, %s : i64, !llvm.ptr" v (emitSlot fb block (2 + i))))
    emitExpr fb groupEnv body
```

`let k = 1 in let rec even n = … odd (n - k) and odd n = … even (n - k) in even 4`가 만드는 코드는 다음과 같다(캡처 순서는 이름 순: `even`은 `[k; odd]`, `odd`는 `[even; k]`).

```mlir
%v3 = llvm.call @calc_alloc(%v2)      // even 블록
...                                    // [0] 헤더, [1] @lambda_0
%v7 = llvm.ptrtoint %v3               // even
%v9 = llvm.call @calc_alloc(%v8)      // odd 블록
...
%v13 = llvm.ptrtoint %v9              // odd
llvm.store %v1, %v14                  // even[2] = k
llvm.store %v13, %v15                 // even[3] = odd
llvm.store %v7, %v16                  // odd[2] = even
llvm.store %v1, %v17                  // odd[3] = k
```

### Step 3: GC에 맡긴다

블록끼리 서로를 가리키는 순환 구조지만, Boehm GC는 도달 가능성으로 회수하므로 순환도 회수한다(reference counting이었다면 따로 처리가 필요하다).

### Step 4: 인터프리터는 호출할 때 그룹 환경을 다시 만든다

인터프리터에는 블록이 없다. 그래서 함수 값이 "그룹 전체와 자기 번호"를 들고 있다가, 호출할 때 그룹의 모든 이름을 환경에 넣는다.

```fsharp
| VRecGroup of index: int * bindings: (string * string * Expr) list * env: Map<string, Value>

let declareRecGroup env bindings =
    bindings |> List.indexed
    |> List.fold (fun acc (i, (name, _, _)) -> Map.add name (VRecGroup (i, bindings, env)) acc) env

| VRecGroup (index, bindings, closureEnv) ->
    let _, param, body = bindings[index]
    evalIn (declareRecGroup closureEnv bindings |> Map.add param av) body
```

단일 `let rec`의 `VRecClosure`가 호출할 때 자기 자신을 넣던 것을 그룹으로 늘린 것이다.

### Step 5: 깊은 재귀와 바깥 변수를 테스트한다

```fsharp
"let k = 100 in let rec f x = if x = 0 then k else g (x - 1) and g y = f y + 1 in f 5", ("105\n", "", 0), "바깥 변수 캡처"
"let rec even n = ... and odd n = ... in even 1000000", ("true\n", "", 0), "깊이 100만"
```

## Example

```
# let rec even n = if n = 0 then true else odd (n - 1)
  and odd n = if n = 0 then false else even (n - 1);;
val even : int -> bool = <fun>
val odd : int -> bool = <fun>
# (even 4, odd 4);;
- : bool * bool = (true, false)
```

## 체크리스트

- [ ] 그룹의 클로저를 모두 할당한 뒤에 캡처 칸을 채우는가?
- [ ] 자기 자신은 캡처하지 않고 `%closure`로 쓰는가?
- [ ] 바깥 변수와 형제를 같은 캡처 목록에서 구분해 채우는가?
- [ ] 다 채우기 전의 블록이 밖으로 새지 않는가? (본문은 다 채운 뒤에 생성)
- [ ] 깊은 재귀와 바깥 변수 캡처를 테스트하는가?

## 관련 문서

- `30-implement-closure-conversion-to-top-level-functions.md` - 단일 `let rec`의 `%closure`
- `08-implement-closures-and-let-rec.md` - 인터프리터의 `VRecClosure`
- `31-link-large-native-stack-for-deep-recursion.md` - 깊이 100만 재귀
