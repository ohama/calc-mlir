---
order: 88
created: 2026-10-30
commit: 4bd5758
version: 0.63.0
description: 본문이 인자를 바로 match (a, b) 하는 함수는 이름으로 부를 때 튜플 대신 원소를 받게 할 수 있다(worker/wrapper). 이미 있는 "클로저 쪽과 direct 함수" 구조에서 direct 함수만 인자 목록을 바꾸고, 한 번 낮춘 본문을 같이 쓴다. 호출하는 곳은 튜플 리터럴이면 원소를 그 자리에서 계산하고, 아니면 계산한 뒤 푼다. 모르는 함수로 부르는 경로는 펴지 않으므로 클로저가 인자 수를 알리지 않게 한다
---

# 튜플 인자를 worker/wrapper로 펴기

> 관련 커밋: [4bd5758](https://github.com/ohama/calc-mlir/commit/4bd5758) (v0.63.0)

Calc에는 인자 자리의 튜플 패턴(`let f (a, b) = …`)이 없어서, 튜플을 받는 함수는 본문에서 바로 `match`한다.

```
let rec go i p = match p with (a, b) -> if i = 0 then a else go (i - 1) (b, (a + b) % 1000003)
```

0.62.0까지 `go`는 부를 때마다 튜플 블록 `[헤더, b, a + b]`를 할당하고, 받자마자 풀었다. 같은 일을 인자 둘로 쓰면 할당이 없다.

| 프로그램 (실행 파일) | 0.62.0 | 0.63.0 |
|---|---|---|
| `go` 5천만 번 | 0.27초 | 0.20초 |
| 같은 일을 인자 둘로 (비교) | 0.20초 | 0.20초 |
| `dist (i, i + 1) (i % 7, 3)` 3천만 번 (튜플 인자 둘) | 0.28초 | 0.11초 |

## The Insight

**함수를 "튜플을 받는 겉(wrapper)"과 "원소를 받는 속(worker)"으로 나누면, 이름으로 부르는 곳은 속을 바로 부를 수 있다.** GHC가 쓰는 worker/wrapper 변환과 같은 생각이다.

이 컴파일러에는 이미 이 구조가 있었다. 인자가 여럿인 이름 붙은 함수는 두 가지로 만든다.

- **클로저 쪽:** 인자를 하나씩 받는 함수들. 값으로 넘길 때 쓴다.
- **direct 함수:** 인자를 모두 받는다. 이름으로 인자를 다 주는 호출이 쓴다.

두 쪽은 한 번 낮춘 본문을 같이 쓰고, 캡처한 값의 칸도 같다. 그래서 새 함수를 만들 필요가 없다. **direct 함수의 인자 목록만 바꾸면 된다.**

```
lambda_0         클로저 쪽: p 를 받아 match (a, b) 한 뒤 본문      (wrapper)
lambda_0_direct  direct:   (클로저, i, a, b) → 본문                 (worker)
```

## Why This Matters

- **튜플을 받는 함수는 흔하다.** 누산기 둘을 들고 도는 재귀, 좌표, 구간, `(key, value)` 쌍이 그렇다. 언어에 인자 자리의 패턴이 없으면 모두 이 모양이 된다.
- **할당만이 아니다.** `dist p q`는 튜플 둘을 만들고 읽는 대신 원소가 레지스터로 넘어가서 2.5배 빨라졌다. GC가 할 일도 준다.
- **잘못 펴면 결과가 바뀐다.**
  - 계산 순서: `f ((print "x"; 1), (print "y"; 2))`
  - 본문이 튜플 자체를 쓰는 경우: `(p, a + b)`
  - 분기가 여럿이거나 가드가 있는 `match`
  - 원소 이름이 다른 인자와 겹치는 경우: `let f x p = match p with (x, y) -> …`

## Recognition Pattern

- 함수가 받자마자 푸는 튜플을 호출하는 쪽이 매번 만들 때
- 컴파일러에 이미 "값으로 쓸 때의 함수"와 "이름으로 부를 때의 함수"가 따로 있을 때
- profile에서 짧게 사는 작은 블록의 할당과 GC가 보일 때

## The Approach

### Step 1: 펼 수 있는 모양을 문법으로 정한다

본문을 낮추기 전에(재귀 호출이 쓸 정보를 먼저 정해야 하므로) AST에서 모양을 본다.

```fsharp
let rec peel (flats: Map<string, Pattern list>) (body: Expr) =
    match body with
    | Match (Var p, [ PTuple items, None, rest ])           // 분기 하나, 가드 없음
        when List.contains p parameters && not (flats.ContainsKey p)
             && flatPattern items                               // 원소가 모두 이름이나 _
             && (원소 이름이 다른 인자, 이미 편 원소와 겹치지 않음) ->
        peel (Map.add p items flats) rest                       // 다음 인자도 같은 모양이면 계속
    | _ -> flats, body
```

결과는 인자마다 원소 수(`Known.Widths`)다. 재귀 호출과 이후의 호출이 이것을 본다.

### Step 2: 본문을 한 번 낮추고 두 쪽에 나눠 준다

```fsharp
let lowered = lower ... (bindAll (parameters @ elementNames) scope) body
// 클로저 쪽: 원래 모양대로 match 로 감쌈
let matched = flats |> ... |> List.fold (fun acc (p, items) -> CMatch (CVar p, [ PTuple items, None, acc ])) lowered
// direct 쪽: p 자리에 원소 이름. _ 는 쓰지 않는 이름 ($p_1)
let directParams = parameters |> List.collect (fun p -> match Map.tryFind p flats with Some items -> elementNames p items | None -> [ p ])
// 본문이 p 자체를 쓰면 다시 만듦 (튜플은 바꿀 수 없으므로 같은 값)
let directBody = if Set.contains p (freeVars lowered) then CLet (p, CTuple (...), lowered) else lowered
```

캡처한 값이 같은지 확인한다. 클로저 쪽의 자유 변수는 `freeVars(body) − 원소 이름 − 인자`이고, direct 쪽은 `freeVars(body) − (인자 − p + 원소 이름)`이다. 둘은 같다. 그래서 direct 함수가 바깥 클로저에서 캡처한 값을 꺼내는 지금의 규약을 그대로 쓴다.

### Step 3: 호출하는 곳에서 튜플을 만들지 않는다

```fsharp
// 펴는 인자가 모두 같은 크기의 튜플 리터럴: 원소를 그 자리에서 계산 (왼쪽부터, 원래와 같은 순서)
go (i - 1) (b, a + b)   ⇒  lambda_0_direct(go, i - 1, b, a + b)

// 그 밖: 인자를 왼쪽부터 $argN 에 묶고, 펴는 인자는 튜플에서 원소를 꺼냄
f x t                   ⇒  let $arg0 = x in let $arg1 = t in match $arg1 with ($arg1_0, $arg1_1) -> lambda_direct(f, $arg0, $arg1_0, $arg1_1)
```

두 번째 경우는 할당이 줄지 않지만 결과는 같다.

### Step 4: 모르는 함수로 부르는 경로를 막는다

0.58.0부터 인자가 여럿인 클로저는 헤더에 인자 수를 적고 direct 함수 포인터를 들고 있다. 모르는 함수에 인자 k개를 주면(`f acc x`) 수가 맞을 때 direct 함수를 바로 부른다. 그런데 이 경로는 튜플을 펴지 않는다. direct 함수의 인자 모양이 바뀌었으므로, **편 함수의 클로저는 인자 수를 알리지 않는다**(`Direct = None`). 그러면 모르는 함수로 부를 때는 인자를 하나씩 적용한다. 느려질 수 있지만 틀리지는 않는다.

인자가 하나인 함수는 전에는 바깥 클로저의 함수가 곧 direct 함수였다. 펴면 `_direct`를 따로 만든다.

### Step 5: 결과가 바뀔 수 있는 경우와 펴지 않는 경우를 테스트한다

| 경우 | 프로그램 |
|---|---|
| 원소 계산 순서 | `f ((print "x"; 1), (print "y"; 2))`, `f (let t = (print "z"; (3, 4)) in t)` |
| 본문이 튜플 자체를 씀 | `let f x p = match p with (a, b) -> (p, a + b + x)` |
| 값으로 쓴 함수 | `map f`, `let g = f in g (…)`, `fold (fun acc t -> acc + f t)` |
| 상호 재귀 | `let rec even p = … odd (n - 1, acc + 1) and odd p = …` |
| 펴지 않음 | 원소 이름이 인자와 겹침, 가드와 분기 둘, 겹친 튜플 패턴 |

"펴지 않음"은 출력만으로는 구분되지 않는다. 생성한 MLIR에서 direct 함수의 인자 수를 확인한다.

## Example

```mlir
llvm.func internal tailcc @lambda_0_direct(%closure: !llvm.ptr, %arg0: i64, %arg1: i64, %arg2: i64) -> i64 {
  ...
  %v45 = llvm.call tailcc tail @lambda_0_direct(%v44, %v16, %arg2, %v43) : (!llvm.ptr, i64, i64, i64) -> i64
}
```

본문에 `calc_alloc`이 없고, 재귀 호출은 그대로 tail call이다.

## 체크리스트

- [ ] 모양을 본문을 낮추기 전에 정해서, 재귀 호출도 편 함수를 부르는가?
- [ ] 분기 하나, 가드 없음, 원소가 이름이나 `_`인 경우만 펴는가?
- [ ] 원소 이름이 다른 인자와 겹치면 펴지 않는가?
- [ ] 본문이 튜플 인자 자체를 쓰면 direct 함수에서 다시 만드는가?
- [ ] 호출하는 곳에서 원소와 다른 인자의 계산 순서가 원래와 같은가?
- [ ] 인자 모양을 모르는 경로(간접 호출의 direct 포인터)가 편 함수를 부르지 않는가?
- [ ] 펴지 않는 경우를 MLIR로 확인했는가?

## 관련 문서

- `83-call-multi-argument-closures-through-direct-entry.md` - 클로저의 인자 수와 direct 함수 (Step 4에서 막는 경로)
- `86-survey-language-features-for-runtime-fast-paths.md` - 이 일을 찾은 조사
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
