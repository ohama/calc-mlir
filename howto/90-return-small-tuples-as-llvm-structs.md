---
order: 90
created: 2026-10-30
commit: 635cfb0
version: 0.64.0
description: 튜플을 돌려주는 함수는 결과를 블록으로 할당하고, 받는 쪽은 바로 푼다. 끝이 모두 튜플 리터럴인 함수는 direct 함수가 LLVM struct로 돌려주고, 받자마자 match 하는 곳은 extractvalue로 원소를 꺼낸다. 끝에서 다른 함수를 부르면 결과를 풀어야 해서 tail call이 끊기므로, 끝이 튜플 리터럴이나 자기 재귀뿐인 함수만 고른다
---

# 작은 튜플 결과를 LLVM struct로 돌려주기

> 관련 커밋: [635cfb0](https://github.com/ohama/calc-mlir/commit/635cfb0) (v0.64.0)

0.63.1까지는 튜플을 돌려주는 함수가 결과마다 블록 `[헤더, a, b]`를 할당했다. 받는 쪽은 대개 그 자리에서 푼다.

```
let divmod a b = (a / b, a % b)
match divmod i 7 with (q, r) -> …
```

| 프로그램 (실행 파일) | 0.63.1 | 0.64.0 |
|---|---|---|
| `match divmod i 7 with (q, r)` 3천만 번 | 0.18초 | 0.11초 |
| 같은 일을 튜플 없이 (비교) | 0.11초 | 0.11초 |
| `fib2` 30만 번 (재귀 결과를 `match`한 뒤 튜플) | 0.12초 | 0.08초 |
| `p := step !p` 3천만 번 (결과를 ref에 넣음) | 0.21초 | 0.21초 |

## The Insight

**LLVM 함수는 first-class aggregate(struct)를 돌려줄 수 있고, 작은 struct는 레지스터 여럿으로 돌아온다.** 튜플을 돌려주는 함수의 결과 타입을 `!llvm.struct<(i64, i64)>`로 바꾸면, 만드는 쪽은 `insertvalue`, 받는 쪽은 `extractvalue`만 남고 할당이 없다.

단, **struct를 돌려주는 함수의 끝에서 값(i64)을 돌려주는 함수를 부르면 tail call이 아니게 된다.** 받은 튜플 블록을 풀어서 struct로 바꿔야 하기 때문이다. 이 언어는 tail call로 반복을 하므로, 끝이 바뀐 함수는 깊은 재귀에서 스택이 자랄 수 있다. 그래서 고르는 조건이 곧 tail call을 지키는 조건이다.

> 끝(tail position)이 모두 같은 크기의 튜플 리터럴이거나, 자기 자신에게 인자를 모두 주는 호출.

자기 재귀는 같은 struct를 돌려주는 함수끼리의 호출이라 그대로 tail call이다.

## Why This Matters

- **튜플 결과는 거의 바로 풀린다.** `divmod`, `split`, `partition`, 상태와 결과를 함께 돌려주는 함수가 그렇다. 블록은 만들자마자 버려진다.
- **조건을 넓히면 스택이 터진다.** `let rec even n = if n = 0 then (true, 0) else odd (n - 1)`처럼 서로 부르는 함수를 struct로 바꾸면, `odd`의 결과(블록)를 풀어 struct로 바꾸는 동안 `even`의 프레임이 남는다. 백만 번이면 백만 프레임이다.
- **결과를 값으로 쓰는 곳은 이득이 없다.** `r := f x`, `[f x]`는 블록이 필요하다. 그 자리에서 만들 뿐이라 손해도 없다.

## Recognition Pattern

- 함수의 결과 튜플을 호출하는 쪽이 바로 `match`하거나 `let (a, b) =`로 풀 때
- profile에서 결과용 작은 블록의 할당이 보일 때
- 함수마다 "값으로 쓸 때"와 "이름으로 부를 때"의 두 입구가 이미 있을 때 (이름으로 부르는 쪽만 결과 모양을 바꿀 수 있다)

## The Approach

### Step 1: 끝을 따라가며 조건을 확인한다

AST에서 본문의 끝을 따라간다. `if`의 두 갈래, `match`의 분기, `let`과 `;` 뒤가 끝이다.

```fsharp
let rec ends (visible: bool) (e: Expr) =
    match e with
    | Tuple items -> widths.Add items.Length; true
    | App _ -> (match spine e with
                | Var f, args -> visible && Some f = self && args.Length = parameters.Length
                | _ -> false)
    | If (_, t, f) -> ends visible t && ends visible f
    | Match (_, cases) -> cases |> List.forall (fun (p, _, b) -> ends (under (patternVars p)) b)
    | Let (name, _, b) -> ends (under (Set.singleton name)) b
    ...
    | _ -> false                      // 변수, 다른 함수 호출, try 는 고르지 않음
```

`visible`은 자기 이름이 가려지지 않았는지다. `let f = … in f n`이나 패턴이 같은 이름을 묶으면 그 안의 `f`는 자기 자신이 아니다. 크기는 모두 같아야 하고, 확인한 크기(4)까지만 고른다.

### Step 2: 이름으로 부르는 입구만 결과 모양을 바꾼다

| 함수 | 부르는 곳 | 결과 |
|---|---|---|
| 바깥 클로저의 함수 | 값으로 쓸 때 (인자 하나씩) | 튜플 블록 |
| `_direct` | 이름으로 인자를 모두 줄 때 | struct |
| `_tupled` | 모르는 함수로 인자를 모두 줄 때 (인자가 여럿) | 튜플 블록 |

클로저와 간접 호출로 부르는 쪽은 함수를 모르므로 결과 모양을 바꿀 수 없다(`89-keep-every-call-path-when-changing-a-calling-convention.md`). 세 함수는 한 번 낮춘 본문을 같이 쓰고, Codegen이 함수마다 돌려주는 원소 수(`CFunction.Returns`)를 보고 끝을 다르게 만든다.

### Step 3: Codegen의 끝을 함수의 결과 모양에 맞춘다

```fsharp
match expr with
| CTuple items when fb.Returns = items.Length ->              // 튜플 리터럴: 블록 없이 struct
    emitReturnStruct fb (items |> List.map (emitExpr fb env))
| CCallDirect (f, c, args) when fb.Returns > 1 && ReturnsOf f = fb.Returns ->
    // 같은 크기의 struct 를 돌려주는 함수: 그대로 tail call
    fb.Emit(sprintf "llvm.return %s : %s" (emitCallDirect fb env "tail " f c args) (structType fb.Returns))
| CCallDirect (f, _, _) when ReturnsOf f > 1 ->                 // 값을 돌려주는 함수에서 struct 를 받음
    returnValue (emitExpr fb env expr)                          //   원소로 블록을 만들어 돌려줌 (tail call 아님)
```

`returnValue`는 함수가 struct를 돌려주면 블록에서 원소를 꺼내 struct로 만든다. Step 1의 조건 때문에 고른 함수에서는 쓰이지 않지만, Codegen이 Lower의 조건에 기대지 않고 항상 맞는 코드를 내게 둔다.

### Step 4: 받는 쪽은 이미 있는 "튜플 없이 맞추기"를 쓴다

`match (a, b) with (p, q) -> …`를 블록 없이 원소마다 맞추는 길이 이미 있었다. 대상이 struct를 돌려주는 direct 함수 호출일 때도 같은 길로 보낸다. 원소 값만 `extractvalue`에서 온다.

```fsharp
let private tupleScrutinee (strings: StringTable) (scrutinee: CExpr) cases =
    match scrutinee with
    | CTuple items when tupleArms items.Length cases -> Some items.Length
    | CCallDirect (f, _, _) when strings.ReturnsOf f > 1 && tupleArms (strings.ReturnsOf f) cases -> Some (strings.ReturnsOf f)
    | _ -> None
```

`let (a, b) = f x`는 Lower가 분기 하나짜리 `match`로 만들므로 따로 할 일이 없다.

### Step 5: 스택이 자라지 않는지 깊이로 확인한다

이 프로젝트의 실행 파일은 스택이 512MB라서, 3백만 번 재귀는 tail call이 아니어도 통과할 수 있다. 프레임 수가 스택을 넘을 만큼 깊게 부르고 최대 메모리를 본다.

```bash
# let rec w4 n a = if n = 0 then (a, n, a + 1, 7) else w4 (n - 1) (a + 1) in println (w4 50000000 0)
/usr/bin/time -l ./d2      # 최대 메모리 2.8MB
```

"고르지 않는 경우"는 출력이 같으므로 MLIR에서 `!llvm.struct`가 없는지 확인한다(원소 다섯, 서로 부르는 함수, 변수로 끝나는 함수).

## Example

```mlir
llvm.func internal tailcc @lambda_0_direct(%closure: !llvm.ptr, %arg0: i64, %arg1: i64) -> !llvm.struct<(i64, i64)> {
  ...
  %v39 = llvm.mlir.undef : !llvm.struct<(i64, i64)>
  %v40 = llvm.insertvalue %v19, %v39[0] : !llvm.struct<(i64, i64)>
  %v41 = llvm.insertvalue %v38, %v40[1] : !llvm.struct<(i64, i64)>
  llvm.return %v41 : !llvm.struct<(i64, i64)>
}
...
%v25 = llvm.call tailcc @lambda_0_direct(%v24, %v21, %v23) : (!llvm.ptr, i64, i64) -> !llvm.struct<(i64, i64)>
%v26 = llvm.extractvalue %v25[0] : !llvm.struct<(i64, i64)>
%v27 = llvm.extractvalue %v25[1] : !llvm.struct<(i64, i64)>
```

## 체크리스트

- [ ] 끝이 모두 튜플 리터럴이거나 자기 재귀인 함수만 고르는가?
- [ ] 자기 이름이 가려진 곳의 호출을 자기 재귀로 보지 않는가?
- [ ] 클로저와 간접 호출의 입구는 튜플 블록을 돌려주는가?
- [ ] 값을 돌려주는 함수에서 struct 함수를 부를 때 블록을 만들어 주는가?
- [ ] 결과를 바로 푸는 곳이 블록 없이 원소를 꺼내는가?
- [ ] 스택 크기를 넘는 깊이로 tail call을 확인했는가?
- [ ] 고르지 않는 경우를 MLIR로 확인했는가?

## 관련 문서

- `88-flatten-tuple-parameters-with-worker-wrapper.md` - 인자 쪽의 같은 변환
- `89-keep-every-call-path-when-changing-a-calling-convention.md` - 클로저와 간접 호출의 입구를 지키기
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
