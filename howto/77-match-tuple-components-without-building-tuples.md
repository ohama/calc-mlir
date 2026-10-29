---
order: 77
created: 2026-10-29
commit: 713fe55
version: 0.51.0
description: match (a, b) with (p, q) 는 튜플을 만들고 바로 푼다. 분기가 모두 같은 길이의 튜플 패턴이나 _ 면 원소를 따로 계산해 원소마다 맞추고, 실패할 때만 튜플을 만든다. append (reverse xs) ys 같은 흔한 모양은 Codegen이 알아보고 한 번에 처리한다. 효과가 없던 시험(병렬 표시, -O3, interior pointer)도 기록한다
---

# 튜플 match에서 튜플을 만들지 않기

> 관련 커밋: [713fe55](https://github.com/ohama/calc-mlir/commit/713fe55) (v0.51.0)

0.50.0의 실행 파일에서 merge sort 20만은 시간 대부분이 블록 할당이었다(`76-flatten-constructor-tuple-payloads-by-block-size.md`). 그 할당 가운데 두 가지는 프로그램이 쓰지 않는 값이었다.

```
let rec merge xs ys acc =
    match (xs, ys) with                          // ① 단계마다 튜플 블록을 만들고 바로 풂
    | ([], _) -> append (reverse acc) ys         // ② 거꾸로 만든 리스트를 다시 복사 (칸 2n 개)
    | (_, []) -> append (reverse acc) xs
    | (x :: xr, y :: yr) -> ...
```

| 실행 파일 (초) | Python | 0.50.0 | ② | ① + ② |
|---|---|---|---|---|
| merge sort 20만 | 0.209 | 0.097 | 0.080 | 0.066 |

## The Insight

**"만들고 바로 푸는 값"은 만들지 않아도 된다.**

- **튜플 scrutinee:** `match (a, b) with`의 튜플은 패턴이 원소를 꺼내 쓸 뿐이다. 분기가 모두 원소 패턴이면 튜플 전체가 필요한 곳은 실패 메시지(`Match failure: (1, 2)`)뿐이다.
- **같은 원리, 다른 실행기:** 인터프리터에 0.47.0에서 넣은 것과 같은 생각이다(`73-reduce-long-lived-objects-not-short-lived-ones.md`). 그러나 효과의 크기는 달랐다.
  - 인터프리터(-5%): .NET의 짧게 사는 할당이 싸다.
  - 컴파일러(-21%): Boehm GC의 할당이 비싸다.
  - 그래서 실행기마다 따로 잰다.

**흔한 모양은 알아보고 한 번에 처리한다.** `append (reverse acc) ys`는 함수형 코드의 관용구다. `reverse`가 만든 리스트를 `append`가 다시 복사하므로 칸이 2n개 생긴다. 이 둘을 `rev_append` 하나(칸 n개)로 바꿔도 결과와 계산 순서는 같다.

## Why This Matters

- **할당 하나가 단계 하나의 비용을 정한다.** merge 한 단계는 비교 한 번과 cons 하나가 본업이다. 그런데 튜플 블록(24B, Boehm에서 32B)을 하나 더 만들고 있었다.
- **표현을 바꾸지 않아도 줄일 수 있다.** 값 표현(블록 모양)은 그대로이고, Codegen이 만드는 코드만 바뀐다. 런타임, 출력, 비교는 손대지 않는다.

## Recognition Pattern

- `match (e1, e2) with`처럼 튜플 리터럴을 scrutinee로 쓰는 코드가 뜨거운 반복 안에 있을 때
- profile에서 할당이 크고, 할당한 값이 곧바로 패턴으로 풀릴 때
- 리스트 함수를 겹쳐 쓴 모양(`append (reverse xs) ys`, `length (map f xs)`)이 뜨거운 곳에 있을 때

## The Approach

### Step 1: 조건을 좁게 정한다

```fsharp
/// match 의 분기가 모두 원소 n 개인 튜플 패턴이나 _ 인가
let private tupleArms (n: int) cases =
    cases |> List.forall (fun (p, _, _) -> match p with Ast.PTuple ps -> ps.Length = n | Ast.PWildcard -> true | _ -> false)

/// 분기의 패턴과 원소 값의 짝 (_ 는 원소마다 _)
let private components pattern vs =
    match pattern with
    | Ast.PTuple ps -> List.zip ps vs
    | _ -> vs |> List.map (fun v -> Ast.PWildcard, v)
```

변수 하나가 튜플 전체를 묶는 분기(`| t -> …`)가 있으면 이 경로를 쓰지 않는다. 그 분기는 튜플이 필요하기 때문이다.

### Step 2: 원소를 계산하고 원소마다 맞춘다

```fsharp
| CMatch (CTuple items, cases) when tupleArms items.Length cases ->
    let vs = items |> List.map (emitExpr fb env)            // 튜플을 만들 때와 같이 왼쪽부터
    let rec emitCases cases =
        match cases with
        | [] ->                                               // 실패할 때만 튜플을 만들어 메시지에 씀
            fb.Emit(sprintf "llvm.call @calc_match_failure(%s) : (i64) -> ()" (emitBlock fb (tupleHeader vs.Length) vs))
            ...
        | (pattern, guard, body) :: rest ->
            let cond = emitComponentsCondition fb env (components pattern vs) guard
            emitIf fb cond (fun () -> emitExpr fb (emitBindAll fb (components pattern vs) env) body) (fun () -> emitCases rest)
```

- **원소 조건:** `emitComponentsCondition`은 원소마다 기존 `emitTest`를 부르고, 앞이 실패하면 뒤는 검사하지 않는다. 가드는 그다음이다.
- **이름 묶기:** `emitBindAll`은 원소마다 기존 `emitBind`를 부른다. 패턴 하나를 다루는 코드를 그대로 다시 쓴다.
- **두 곳 모두 고친다:** 값을 내는 위치(`emitExpr`)와 꼬리 위치(`emitTail`)의 match가 따로 있다. 꼬리 위치를 빠뜨리면 `merge`처럼 꼬리 호출로 도는 함수는 빨라지지 않는다.

### Step 3: 관용구를 알아보고 한 번에 처리한다

```fsharp
// Codegen: 내장 함수 호출의 모양으로 알아봄
| CBuiltin ("append", [ CBuiltin ("reverse", [ xs ]); ys ]) ->
    let xv = emitExpr fb env xs                             // 원래 순서: reverse 의 인자, 그다음 ys
    let yv = emitExpr fb env ys
    emitCall fb "calc_rev_append" [ xv; yv ] "i64"
```

```c
/* append (reverse xs) ys: xs 의 원소를 하나씩 ys 앞에 붙임. 칸 n 개 */
value calc_rev_append(value xs, value ys) {
    for (; xs != CALC_NIL; xs = TAIL(xs)) ys = calc_cons(HEAD(xs), ys);
    return ys;
}
```

- **같은 의미인 이유:** `reverse`와 `append`는 부수 효과가 없으므로, 인자를 같은 순서로 계산하면 의미가 같다.
- **알아보는 곳:** Lower를 거친 Core에서 알아본다. `reverse`가 사용자 정의로 가려졌으면 `CBuiltin`이 아니라 보통 호출이 되므로, 잘못 바꾸는 일이 없다.

### Step 4: 차등 테스트에 경계를 넣는다

| 경우 | 확인하는 것 |
|---|---|
| `match ((print 1; [1]), (print 2; [])) with …` | 원소를 왼쪽부터 계산 (`12` 출력) |
| `let x = 10 in (match (1, 2) with (x, 3) -> x \| (a, b) when a > b -> 0 \| _ -> x) + x` | 실패한 분기의 `x`가 바깥을 가리지 않음, 가드, `_` |
| `match (1, 2) with (0, _) -> 0 \| (_, 0) -> 1` | 실패 메시지 `Match failure: (1, 2)` |
| `(1, (2, 3))` | 안쪽 튜플은 그대로 블록 |
| `append (reverse (r := 1; [1])) (r := !r + 10; [!r])` | `rev_append`의 인자 계산 순서 (`[1; 11]`) |

새 테스트는 고치기 전 컴파일러에서도 통과해야 한다.

## Example

같은 조사에서 효과가 없던 시험도 기록했다. 다음에 같은 것을 다시 재지 않아도 된다.

| 시험 | 결과 | 이유 |
|---|---|---|
| `GC_MARKERS=2, 4, 8` | 변화 없음 | homebrew의 libgc가 병렬 표시를 쓰지 않는 것으로 보임 |
| clang `-O3`, `-mcpu=native` | 측정 오차 수준 | 뜨거운 곳이 할당과 메모리 접근 |
| `GC_set_all_interior_pointers(0)` | 변화 없음 | 크기는 맞아졌지만(16B 요청 → 16B), 이 예제의 블록(24B, 40B)은 어차피 32B, 48B로 올라감 |

Boehm의 크기 반올림은 작은 C 프로그램으로 직접 확인할 수 있다.

```c
GC_INIT();
for (int b = 8; b <= 48; b += 8) printf("request %2d -> object %zu bytes\n", b, GC_size(GC_MALLOC(b)));
// 기본 설정: 16 → 32, 32 → 48 (객체 끝을 가리키는 포인터를 위해 1바이트를 더 붙임)
```

## 체크리스트

- [ ] 만들고 바로 푸는 값(튜플 scrutinee)이 뜨거운 곳에 있는가?
- [ ] 빠른 경로의 조건을 좁게 정했는가? (튜플 전체를 묶는 분기가 있으면 쓰지 않음)
- [ ] 원소를 계산하는 순서가 튜플을 만들 때와 같은가?
- [ ] 실패할 때만 원래 값을 만들어 같은 메시지를 내는가?
- [ ] 값을 내는 위치와 꼬리 위치의 match를 모두 고쳤는가?
- [ ] 관용구를 바꿀 때 부수 효과가 없고 인자 계산 순서가 같은가?
- [ ] 같은 생각이라도 실행기마다 효과를 따로 쟀는가?
- [ ] 효과가 없던 시험도 이유와 함께 기록했는가?

## 관련 문서

- `76-flatten-constructor-tuple-payloads-by-block-size.md` - 생성자의 튜플 인자를 블록에 펴기
- `75-profile-native-executable-and-tune-boehm-gc.md` - 실행 파일의 profile과 Boehm GC
- `73-reduce-long-lived-objects-not-short-lived-ones.md` - 인터프리터의 튜플 match
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
