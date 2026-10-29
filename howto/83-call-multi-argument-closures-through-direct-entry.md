---
order: 83
created: 2026-10-29
commit: ae8bcdc
version: 0.58.0
description: 커링하는 언어의 컴파일러에서 모르는 함수에 인자 여럿을 주면(f acc x) 원소마다 중간 클로저가 생긴다. 인자가 여럿인 함수의 클로저에 인자 수와 인자를 모두 받는 함수 포인터를 넣고, 호출하는 쪽이 인자 수가 맞을 때만 그 함수를 부르면 된다. 맞지 않으면 하나씩 적용해 계산 순서를 지킨다
---

# 인자가 여럿인 클로저를 direct 함수로 부르기

> 관련 커밋: [ae8bcdc](https://github.com/ohama/calc-mlir/commit/ae8bcdc) (v0.58.0)

Calc의 함수는 커링된다. `fun acc x -> e`는 `fun acc -> fun x -> e`다. 이름에 묶은 함수는 0.34.0부터 인자를 모두 주면 인자를 모두 받는 함수(direct 함수)를 바로 불렀다. 그러나 함수를 인자로 받은 쪽은 그 함수가 무엇인지 모른다.

```
let rec myfold f acc xs =
    match xs with
    | [] -> acc
    | x :: r -> myfold f (f acc x) r      // f 를 모름: (f acc) 가 중간 클로저를 만들고, 그것에 x 를 적용
```

| 3천만 개 리스트 (실행 파일) | 0.57.0 | 0.58.0 | 최대 메모리 |
|---|---|---|---|
| `myfold f (f acc x) r` | 0.44초 | 0.21초 | 1.25GB → 0.52GB |
| 같은 일을 하는 직접 재귀 | 0.21초 | 0.21초 | 0.52GB |

## The Insight

**호출하는 쪽이 모르는 것은 클로저가 알면 된다.** 클로저는 어떤 함수에서 만들어졌는지 안다. 클로저에 두 가지를 적어 두면 된다.

| 클로저에 적는 것 | 위치 |
|---|---|
| 인자 수 | 헤더의 32비트 위 (인자 하나짜리와 중간 클로저는 0) |
| 인자를 모두 받는 함수의 포인터 | 블록의 마지막 칸 |

호출하는 쪽은 인자를 k개 줄 때 헤더를 한 번 읽는다. 인자 수가 k면 direct 함수를 부르고, 아니면 지금처럼 하나씩 적용한다. OCaml의 `caml_apply2`, `caml_apply3`과 같은 생각이다.

**빠른 경로는 같은 결과를 낼 때만 쓴다.** `f a1 a2`를 하나씩 적용하면 `(f a1)`의 호출이 생긴다. 인자가 둘인 클로저라면 이 호출은 중간 클로저를 만들기만 한다. 그래서 건너뛰어도 결과가 같다. 함수가 첫 인자를 받자마자 무언가를 한다면(`fun a -> (print a; fun b -> …)`) 그 클로저의 인자 수는 1이고, 하나씩 적용하는 경로를 탄다.

## Why This Matters

- **profile에 원인이 이름으로 나오지 않는다.** 할당과 GC 시간만 늘어난다. 직접 재귀로 같은 일을 한 프로그램과 비교해야 차이가 보인다(시간 2배, 메모리 +730MB).
- **계산 순서를 바꾸면 결과가 바뀐다.** 하나씩 적용할 때의 순서는 `f`, `a1`, `(f a1)`, `a2`다. 모든 인자를 먼저 계산하면 `(f a1)`이 부수 효과를 내는 경우 출력 순서가 달라진다.

## Recognition Pattern

- 커링하는 언어의 컴파일러에서 고차 함수(`fold`, 사용자 함수)가 원소마다 인자 여럿으로 함수 인자를 부를 때
- 같은 일을 하는 직접 재귀보다 고차 함수 쪽이 크게 느리고 메모리를 많이 쓸 때
- 알려진 함수의 direct call은 이미 있는데, 함수 값으로 넘긴 경우만 느릴 때

## The Approach

### Step 1: 직접 재귀와 비교해 비용을 확인한다

```
let rec myfold f acc xs = match xs with [] -> acc | x :: r -> myfold f (f acc x) r
println (myfold (fun acc x -> if x % 3 = 0 then acc + x * 2 else acc) 0 (range 1 30000000))

let rec go xs acc = match xs with [] -> acc | x :: r -> go r (if x % 3 = 0 then acc + x * 2 else acc)
println (go (range 1 30000000) 0)
```

```bash
/usr/bin/time -l ./hofl     # 0.44초, 1.25GB
/usr/bin/time -l ./hand     # 0.21초, 0.52GB
```

차이 730MB는 원소마다 만든 중간 클로저(3천만 개 × 24바이트)다.

### Step 2: 인자가 여럿인 함수의 바깥 클로저에 direct 함수를 기록한다

```fsharp
type CFunction =
    { ...
      /// 인자가 여럿인 함수의 바깥 클로저: 인자를 모두 받는 direct 함수와 그 인자 수
      Direct: (string * int) option
      ... }
```

- **Lower:** `AddCurried`가 바깥 클로저의 함수를 만들 때 `Direct = Some (outer + "_direct", n)`을 넣는다.
- **익명 함수:** `fun a b -> …`도 이름에 묶은 함수처럼 `AddCurried`로 낮춘다. 그래야 `fold (fun acc x -> …)`의 함수에도 direct 함수가 생긴다.

### Step 3: 클로저를 만들 때 칸 하나와 헤더 비트를 더한다

```fsharp
/// [헤더, 코드 포인터, 캡처한 값..., direct 함수 포인터]. 헤더의 32비트 위에 인자 수
let directClosureHeader (capturedCount: int) (arity: int) =
    kindClosure ||| (int64 (2 + capturedCount) <<< 8) ||| (int64 arity <<< 32)
```

- **마지막 칸에 두는 이유:** 캡처한 값의 칸 번호(2부터)가 그대로다. 그래서 바깥 클로저의 함수와 direct 함수가 캡처한 값을 꺼내는 코드를 바꾸지 않는다.
- **호출하는 쪽:** 헤더의 크기 필드로 마지막 칸을 찾는다.
- **헤더 비트를 써도 되는지 확인한다:** 런타임이 클로저 헤더에서 읽는 것은 종류(`<fun>` 출력, `=`의 타입 오류)뿐이고, 크기는 쓰지 않는다.
- **같은 코드로:** 클로저를 만드는 곳(보통 클로저와 `let rec … and …` 그룹) 두 곳이 같은 함수(`emitClosureBlock`)를 쓰게 한다.

### Step 4: 호출하는 쪽은 인자 수를 보고 고른다

`CApply`가 겹친 모양 `f a1 … ak`(k ≥ 2)를 한 번에 다룬다.

```
fv = f
header = load fv[0]
isDirect = (header >> 32) == k
v1 = a1
cur = isDirect ? fv : apply(fv, v1)          // 하나씩 적용하는 쪽만 (f a1) 을 부름
v2 = a2
...
r = isDirect ? fv[size](fv, v1, ..., vk)     // 마지막 칸의 direct 함수
             : apply(cur, vk)
```

- **계산 순서:** 인자 사이에 `(f a1)`, `(… a2)`를 끼워 둔다. 하나씩 적용하는 경로는 원래와 순서가 같다. 인자 식을 두 경로에 복사하지 않으므로 코드도 커지지 않는다.
- **tail position:** `scf.if` 안의 호출은 tail call이 될 수 없다. 그래서 블록 두 개와 `llvm.cond_br`로 나누고, 각 블록에서 `tail` 호출 뒤 `llvm.return`한다.
- **인자 수가 다를 때:** 많이 주거나(`h 1 2 3`에 인자 둘짜리 함수) 적게 주면 하나씩 적용한다. 같을 때만 빠른 경로를 쓴다.

### Step 5: 경로마다 차등 테스트를 쓴다

| 경우 | 프로그램 | 기대 |
|---|---|---|
| 인자 계산 순서 | `g (fun a b -> …)`, `h (print 1; 1) (print 2; 2)` | `12` 출력 |
| 인자 하나짜리 함수가 함수를 돌려줌 | `fun a -> (print a; fun b -> a + b)` | `(f a)`가 다음 인자보다 먼저 |
| 인자를 많이 줌 | `h 1 2 3`에 `fun a b -> … fun c -> …` | 하나씩 적용 |
| 인자를 적게 줌 | `(g (fun a b -> a + b)) 5` | 부분 적용 |
| 꼬리 위치 | `apply2 count (n - 1) (acc + 1)`을 100만 번 | 스택이 자라지 않음 |
| 이름에 묶은 함수, `let rec` 함수, 내장 함수를 값으로 | `g add`, `fold sumTo`, `apply2 append` | 인자 수 3, 2, 0 |

기대값은 인터프리터의 출력이다. 생성한 MLIR에 인자 수가 들어간 헤더와 `(!llvm.ptr, i64, i64) -> i64` 호출이 있는지도 따로 확인한다.

## Example

효과는 원소마다 이런 호출을 하는 코드에만 있다.

| 프로그램 (3천만 개) | 0.57.0 | 0.58.0 |
|---|---|---|
| 직접 쓴 `myfold` | 0.44초 | 0.21초 |
| prelude `fold` + `map` + `filter` | 0.44초 | 0.38초 (`map`, `filter`의 리스트 할당은 그대로) |
| `benchmarks/`의 다섯 예제 | | 그대로 (이런 호출이 뜨거운 곳에 없음) |

## 체크리스트

- [ ] 고차 함수 버전과 직접 재귀 버전을 시간과 메모리로 비교했는가?
- [ ] 빠른 경로가 건너뛰는 호출에 부수 효과가 없는가? (인자 하나짜리 함수는 하나씩 적용)
- [ ] 하나씩 적용하는 경로의 계산 순서가 원래와 같은가?
- [ ] 인자 식을 두 경로에 복사하지 않는가?
- [ ] tail position에서 두 경로 모두 tail 호출인가?
- [ ] 헤더에 새 비트를 쓰기 전에 런타임이 그 필드를 읽는 곳을 확인했는가?
- [ ] 클로저를 만드는 곳을 모두 같은 함수로 모았는가?

## 관련 문서

- `82-lower-desugared-loops-back-to-structured-loops.md` - 반복문을 지금 함수 안의 반복으로
- `78-fuse-library-idiom-in-both-backends.md` - 최적화가 지켜야 하는 계산 순서와 오류 순서
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
