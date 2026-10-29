---
order: 68
created: 2026-10-29
commit: 9c77efd
version: 0.42.0
description: Map 환경의 AST 인터프리터가 느리면 먼저 Map 비용만 따로 잰다. 변수를 (바깥 함수 단계, 칸)으로 미리 정하고 F# 클로저로 바꾸면 3배 빨라진다. 인자가 다 모인 호출은 Map을 없앤 뒤에야 효과가 있고, 덜 받는 함수는 하나씩 적용해야 계산 순서가 같다
---

# 칸 배열 환경으로 AST 인터프리터 빠르게 하기

> 관련 커밋: [9c77efd](https://github.com/ohama/calc-mlir/commit/9c77efd) (v0.42.0)

이 인터프리터는 식을 그대로 훑으며 평가했다. 환경은 `Map<string, Value>`였다. 표준 라이브러리를 언어로 쓴 뒤(`52-write-standard-library-as-prelude-in-the-language.md`) 원소마다 Calc 함수를 부르게 되면서 느려졌다. 100만 개 리스트에 `range`, `map`, `filter`, `sum`, `exists`를 쓰는 예제가 5.3초였다. 같은 예제를 컴파일하면 0.12초였다.

후보는 두 가지였다.

- **(A):** 인자가 다 모인 호출을 한 번에 부른다.
- **(B):** 환경을 배열로 바꾼다.

둘 다 넣었다. 5.3초가 1.5초가 됐다.

## The Insight

**어디가 느린지 먼저 따로 잰다.** 인터프리터 전체를 프로파일하지 않아도, 의심 가는 부분만 같은 모양으로 재면 된다. 예제의 `range`는 반복마다 `Map.add` 3번과 변수 찾기 6번을 한다. 이것만 F# 스크립트로 100만 번 돌렸다.

```fsharp
let env0 = names |> List.fold (fun m n -> Map.add n (box n) m) Map.empty   // 이름 64개
for k in 1 .. 1000000 do
    let e = env0 |> Map.add "go" (box k) |> Map.add "i" (box k) |> Map.add "acc" (box k)
    for n in [| "i"; "a"; "go"; "i"; "i"; "acc" |] do
        if Map.containsKey n e then hits <- hits + 1
// map: 441 ms
```

`range` 하나가 0.5초인데 `Map`만으로 0.44초다. 그러니 (A)처럼 호출 모양을 고치는 것보다 환경을 바꾸는 것이 먼저다. 실제로 재 보면 다음과 같다.

| 방법 | 시간 |
|---|---|
| 전 (`Map` 환경) | 5.3초 |
| (A)만 | 4.8~5.3초 |
| (B)만 | 1.9초 |
| (A)+(B) | 1.5초 |

**변수의 위치는 실행 전에 정해진다.** 정적 범위에서 `x`가 "몇 단계 바깥 함수의 몇 번째 칸"인지는 식의 모양만 보고 안다. 그래서 식을 한 번 훑어 위치를 정하고, 그 칸을 읽는 F# 클로저로 바꾼 뒤 실행한다. 실행 중에는 문자열을 비교하지 않는다.

## Why This Matters

- **추측으로 고치면 시간을 버린다.** (A)는 그럴듯하지만, `Map` 환경 위에서는 남는 비용이 그대로라서 효과가 없었다.
- **계산 순서가 바뀔 수 있다.** `f a b`를 "인자 둘을 다 계산한 뒤 한 번에 부르기"로 바꾸면 결과가 달라질 수 있다. `f`가 인자를 하나만 받는 함수이고 부수 효과가 있으면 순서가 바뀐다.

  ```
  let f x = (print x; fun y -> x + y) in f 1 (print 2; 10)    (* 반드시 "12" *)
  ```

- **오류의 시점이 바뀔 수 있다.** 훑는 단계에서 이름을 찾지 못했다고 바로 오류를 내면 안 된다. `if false then undefined else 1`처럼 실행되지 않는 곳의 오류까지 나 버린다(타입 검사를 건너뛴 경우).
- **함수를 값에 넣으면 F# equality가 사라진다.** `Value` union에 `Value -> Value` 필드를 넣으면 테스트의 `Expect.equal`이 컴파일되지 않는다.

  ```
  error FS0001: The type 'V' does not support the 'equality' constraint because it is a record, union or struct
  with one or more structural element types which do not support the 'equality' constraint.
  ```

## Recognition Pattern

- 트리를 그대로 훑는 인터프리터가 이름 → 값 사전(Map, Dictionary)을 환경으로 쓸 때
- 함수 호출이 많은 프로그램(표준 라이브러리를 언어로 쓴 경우)이 느릴 때
- 최적화 후보가 여럿이고 어느 것이 효과가 있을지 모를 때

## The Approach

### Step 1: 실행 중 구조: 호출마다 칸 배열 하나

```fsharp
and [<AllowNullLiteral>] Frame(size: int, parent: Frame) =
    member val Slots: Value[] = Array.zeroCreate size
    member _.Parent = parent            // 이 함수를 만든 곳의 Frame (정적 범위)

and Closure(arity: int, size: int, parent: Frame, code: Frame -> Value, bound: Value list) = ...
```

`Closure`를 클래스로 두면 참조로 비교된다. 그래서 `Value`가 equality를 잃지 않는다. 함수끼리 `=`는 원래 실행 중 오류라서 비교 방법은 상관없다.

### Step 2: 훑는 단계: 변수 → (함수 깊이, 칸)

```fsharp
type private Scope =
    { Vars: Map<string, int * int>      // 이름 → (정의한 함수의 깊이, 칸). 훑을 때만 씀
      Depth: int
      Slots: SlotCount                  // 지금 함수의 칸 수 (훑으면서 늘어남)
      Consts: Env }                     // 내장 함수, 생성자, REPL 의 앞 입력 값

| Var name ->
    match Map.tryFind name scope.Vars with
    | Some (depth, slot) -> access (scope.Depth - depth) slot         // 부모를 몇 번 따라가고 칸 하나
    | None ->
        match Map.tryFind name scope.Consts with
        | Some v -> fun _ -> v
        | None -> fun _ -> failwithf "Unbound variable: %s" name      // 오류는 실행할 때
| Let (name, value, body) ->
    let value = c value
    let slot, inner = bindSlot scope name                             // 지금 함수에 칸 하나
    let body = compile inner body
    fun fr -> fr.Slots[slot] <- value fr; body fr
```

- **`Map`은 훑는 동안에만 쓴다.** 실행 중에는 쓰지 않는다.
- **칸은 이름마다 새로 정한다.** `let`이나 패턴이 이름을 묶을 때마다 지금 함수에 칸을 하나 더한다. 같은 이름을 다시 묶어도 새 칸이다. 그래서 칸을 다시 쓰지 않고, 클로저가 Frame을 그대로 가리켜도 안전하다.
- **칸 수는 다 훑은 뒤 정해진다.** 칸 수 객체를 클로저가 잡아 두었다가 실행할 때 읽는다.
- **`let rec`:** 이름의 칸을 먼저 정하고 함수를 만든다. 실행할 때 만든 함수를 그 칸에 넣으면, 본문의 이름이 자기 자신을 가리킨다.
- **생성자 패턴의 오류도 실행할 때 낸다.** 훑을 때 찾고, 없으면 "그 패턴을 시도할 때 실패하는 코드"를 만든다. 예전처럼 그 분기를 시도할 때 `Unknown constructor` 오류가 난다.

### Step 3: tail call이 그대로 남게 만든다

```fsharp
| If (cond, t, e) ->
    let cond, t, e = c cond, c thenExpr, c elseExpr
    fun fr -> if asBool cond fr then t fr else e fr          // t fr, e fr 가 tail position
| Match ... ->
    fun fr ->
        let v = scrutinee fr
        match findArm arms v fr with                          // 맞는 분기를 루프로 찾고
        | -1 -> matchFailure v
        | i -> let _, _, body = arms[i] in body fr           // 본문은 마지막에 한 번 (tail)
```

F#의 `<Tailcalls>true</Tailcalls>`(`53-enable-fsharp-tailcalls-in-debug-for-interpreters.md`)는 tail position의 클로저 호출에 `.tail`을 붙인다. 분기 본문을 찾는 루프 안에서 부르면 tail call이 아니게 되므로, 루프는 번호만 찾는다. 깊이 100만의 tail recursion 테스트가 이것을 지킨다.

### Step 4: 여러 인자 함수와 인자가 다 모인 호출

```fsharp
// fun a -> fun b -> body  ==>  인자 2개의 함수 하나, 칸 0 과 1 에 인자
| App _ ->
    let head, args = spine expr []
    fun fr ->
        match f fr with
        | VClosure cl when cl.Arity = n && cl.Bound.IsEmpty ->    // 인자 수가 같으면 Frame 하나로 바로
            let frame = Frame(cl.Size, cl.Parent)
            for i in 0 .. n - 1 do frame.Slots[i] <- args[i] fr
            cl.Code frame
        | fv ->                                                    // 아니면 하나씩: (f a) 의 부수 효과가 b 보다 먼저
            let mutable g = fv
            for i in 0 .. n - 2 do g <- apply g (args[i] fr)
            apply g (args[n - 1] fr)
```

- **순서가 같은 이유:** 인자 수가 같은 함수의 partial application은 부수 효과가 없다. 그래서 인자를 다 계산한 뒤 한 번에 불러도 순서가 같다.
- **인자 수가 다르면:** 한 번에 부르지 않고 하나씩 적용한다. 덜 받는 함수에서는 앞 호출의 부수 효과가 뒤 인자보다 먼저 나와야 하기 때문이다.
- **인자 표기:** `let f (a: int) b`의 인자 표기는 `let a = (a : int) in`으로 풀린다. 값이 그대로이므로 사슬을 잇는다.

### Step 5: 같은 테스트로 같은 의미를 확인한다

평가기를 통째로 바꾸므로 기존 테스트 전부가 안전망이다. 결과, 오류 메시지, `print` 순서, tail call, REPL을 모두 본다. 여기서는 1166개가 그대로 통과했다. 새로 넣은 것은 순서와 partial application 테스트다.

```fsharp
evalWithOutput writer (load "let f x = (print x; fun y -> x + y) in f 1 (print 2; 10)")   // "12", 11
calc "let g a b c = a * 100 + b * 10 + c in let h = g 1 in let k = h 2 in (k 3, h 4 5, g 6 7 8)"
calc "let mk x = fun () -> x in let a = mk 1 in let b = mk 2 in (a (), b ())"              // (1, 2): 호출마다 새 Frame
```

## Example

| 예제 | 전 | 후 |
|---|---|---|
| `length (range 1 1000000)` | 0.65초 | 0.26초 |
| `sum (range 1 1000000)` | 1.46초 | 0.45초 |
| `length (map (fun x -> x * 3) (range 1 1000000))` | 2.16초 | 0.78초 |
| 100만 개 리스트 예제 전체 | 5.26초 | 1.5초 |
| tail call이 아닌 재귀의 깊이 한계 | 100만 | 300만 |

시간에는 실행 시작 비용 0.13초가 들어 있다. 재귀 깊이가 늘어난 것은 덤이다. 호출 한 번이 쓰는 .NET 스택이 큰 `evalIn` 함수 하나에서 작은 클로저 몇 개로 줄었기 때문이다.

## 체크리스트

- [ ] 고치기 전에 의심 가는 부분(환경 연산)만 같은 모양으로 따로 쟀는가?
- [ ] 후보마다 같은 예제로 재서 표로 남겼는가?
- [ ] 훑는 단계에서 찾지 못한 이름, 생성자, 필드의 오류를 실행할 때 내는가?
- [ ] `let`, 패턴의 이름마다 새 칸을 써서 클로저가 Frame을 그대로 가리켜도 되는가?
- [ ] 분기 본문, `if`의 가지, `let`의 몸이 여전히 tail position인가?
- [ ] 인자 수가 같을 때만 한 번에 부르고, 아니면 하나씩 적용하는가?
- [ ] 함수 값을 클래스로 두어 `Value`의 equality가 남아 있는가?

## 관련 문서

- `52-write-standard-library-as-prelude-in-the-language.md` - 느려진 이유와 측정 예제
- `53-enable-fsharp-tailcalls-in-debug-for-interpreters.md` - 인터프리터의 tail call
- `59-call-known-functions-directly-with-tailcc.md` - 컴파일러 쪽의 같은 문제 (direct call)
