---
order: 82
created: 2026-10-29
commit: 70b653a
version: 0.57.0
description: 파서가 반복문을 재귀 함수로 바꾸는 언어에서, 컴파일러는 그 모양을 다시 알아보고 지금 함수 안의 반복(scf.while)으로 만들 수 있다. 알아보는 모양을 정확히 좁히고, for의 끝에서 i + 1이 넘치지 않게 (i, 계속할지)를 넘긴다. 효과는 겹친 반복에만 있었고, ref를 레지스터로 올리는 noalias는 다른 반복을 느리게 해서 넣지 않았다
---

# 재귀 함수로 바꾼 반복문을 다시 구조적 반복으로 낮추기

> 관련 커밋: [70b653a](https://github.com/ohama/calc-mlir/commit/70b653a) (v0.57.0)

Calc의 파서는 `while`과 `for`를 `$while`, `$for`라는 `let rec` 함수로 바꾼다. 인터프리터와 타입 추론은 반복문을 따로 다루지 않아도 된다. 그런데 컴파일러는 이 함수를 클로저로 만들었고, 겹친 반복에서는 바깥 반복의 한 바퀴마다 안쪽 반복의 클로저를 새로 만들었다.

| 실행 파일 (중앙값) | 0.56.0 | 0.57.0 |
|---|---|---|
| 겹친 `for` 2만 × 2만 (안쪽에 ref) | 0.171초 | 0.128초 |
| 체 1억 | 0.664초 | 0.658초 |
| `while`과 ref, 3억 번 | 1.399초 | 1.398초 |

## The Insight

**desugaring은 앞 단계를 단순하게 하지만, 뒤 단계가 잃은 정보를 다시 찾아야 할 수 있다.** 파서가 만드는 모양은 정해져 있다. 그래서 Lower가 그 모양 그대로를 알아보면 "이것은 반복문이었다"는 사실을 되찾는다.

```
while c do b done        →  let rec $while $u = if c then (b; $while ()) else () in $while ()

for i = s to e do b done →  let $start = s in let $stop = e in
                            let rec $for i = b; if i < $stop then $for (i + 1) else ()
                            in if $start <= $stop then $for $start else ()
```

반복문으로 알아본 뒤에는 몸체를 따로 함수로 만들 필요가 없다. 지금 함수 안의 `scf.while`로 만들면 바깥 변수는 SSA 값 그대로 쓰이고, 클로저도 붙잡을 칸도 없다.

**LLVM이 이미 하던 일과 겹치는지 먼저 본다.** 하나짜리 반복은 LLVM이 꼬리 재귀를 반복으로 바꾸고 있었다. 그래서 체와 `while`은 빨라지지 않았다. 남은 비용은 클로저를 **만드는** 일이었고, 그것은 겹친 반복에서만 반복마다 생긴다.

## Why This Matters

- **모양을 넓게 알아보면 틀린다.** 사용자가 `$while`이라는 이름을 쓸 수는 없지만, 몸체의 모양까지 보지 않으면 `for`의 증가 방향이나 끝 비교를 잘못 읽을 수 있다. 맞지 않으면 원래처럼 클로저로 만든다.
- **`for`의 끝에서 넘침:** `for i = max_int - 1 to max_int`에서 마지막 바퀴 뒤에 `i + 1`을 계산하면 넘친다. 파서의 `$for`는 `i < $stop`일 때만 `i + 1`을 계산한다. 반복도 같은 규칙이어야 한다.
- **반복마다 다른 `i`:** 몸체의 클로저가 `i`를 붙잡으면, 바퀴마다 그때의 `i`를 붙잡아야 한다. 변수 하나를 고쳐 쓰는 C식 반복으로 만들면 클로저가 모두 마지막 값을 보게 될 수 있다.

## Recognition Pattern

- 앞 단계(파서, desugaring)가 특별한 구문을 일반 구문으로 바꾸는데, 뒤 단계에서 그 구문 전용의 더 좋은 코드가 있을 때
- profile이나 생성 코드에서, 반복마다 클로저 할당(`calc_alloc`)이 보일 때
- 겹친 반복이 같은 일을 하는 C 코드보다 크게 느릴 때

## The Approach

### Step 1: Core에 반복 노드를 더한다

```fsharp
/// while cond do body: 클로저를 만들지 않고 지금 함수 안의 반복으로 (결과는 ())
| CWhile of cond: CExpr * body: CExpr
/// for var = start to (또는 downto) stop do body: start 와 stop 은 이미 계산한 변수. 끝에 닿으면 var ± 1 을 계산하지 않음
| CFor of var: string * start: CExpr * stop: CExpr * up: bool * body: CExpr
```

free variable 계산도 함께 고친다. `CFor`의 몸체에서는 `var`가 묶인다.

```fsharp
| CFor (var, start, stop, _, body) -> Set.unionMany [ freeVars start; freeVars stop; Set.remove var (freeVars body) ]
```

### Step 2: Lower가 파서의 모양을 그대로 알아본다

```fsharp
| LetRec ("$while", "$u", If (cond, Seq (body, App (Var "$while", Unit)), Unit), App (Var "$while", Unit)) ->
    CWhile (lower state ctors scope cond, lower state ctors scope body)
| LetRec ("$for", var, Seq (body, If (before, App (Var "$for", step), Unit)), If (startsBefore, App (Var "$for", Var "$start"), Unit))
    when (match before, step, startsBefore with
          | Less (Var v, Var "$stop"), Add (Var w, Number 1), LessEqual (Var "$start", Var "$stop")
          | Greater (Var v, Var "$stop"), Subtract (Var w, Number 1), GreaterEqual (Var "$start", Var "$stop") -> v = var && w = var
          | _ -> false) ->
    ...
```

- **`$start`와 `$stop`:** 바깥의 `let`이 이미 한 번씩, 왼쪽부터 계산한다. `CFor`는 그 변수를 읽기만 하므로 계산 순서와 횟수가 그대로다.
- **방향:** `to`는 `<`와 `+ 1`, `downto`는 `>`와 `- 1`이다. 비교와 증가가 서로 맞을 때만 알아본다.
- **generic 경로 앞에:** 이 경우를 일반 `LetRec`보다 먼저 둔다.

### Step 3: Codegen은 `scf.while`을 만든다

`while`은 조건 영역과 몸체 영역으로 나눈다.

```mlir
scf.while : () -> () {
  scf.condition(%c)          // 조건을 계산한 i1
} do {
  ...                        // 몸체
  scf.yield
}
```

`for`는 `(i, 계속할지)` 두 값을 반복이 넘긴다.

```mlir
%go = arith.cmpi sle, %first, %last : i64
%r:2 = scf.while (%i = %first, %more = %go) : (i64, i1) -> (i64, i1) {
  scf.condition(%more) %i, %more : i64, i1
} do {
^bb0(%cur: i64, %unused: i1):
  ...                                              // 몸체: var = %cur
  %before = arith.cmpi slt, %cur, %last : i64
  %stepped = arith.addi %cur, %c8 : i64            // 태그를 붙인 int 의 + 1 은 + 8
  %next = arith.select %before, %stepped, %cur : i64
  scf.yield %next, %before : i64, i1
}
```

- **넘침:** `select` 때문에 `%stepped`는 끝값 앞일 때만 쓰인다. 끝값이 int의 끝이면 `%stepped`가 넘친 값이 되지만, 그 값은 쓰이지 않는다.
- **반복마다 다른 `i`:** `%cur`는 바퀴마다 새 SSA 값이다. 몸체의 클로저는 그 값을 칸에 복사하므로 바퀴마다 다른 `i`를 붙잡는다.

### Step 4: 생성 코드의 모양과 경계를 테스트한다

```fsharp
let mlir = Codegen.emitProgram (Program.parse "let s = ref 0 in for i = 1 to 3 do (let j = ref 0 in while !j < i do j := !j + 1 done; s := !s + !j) done; !s")
Expect.equal (mlir.Split([| "scf.while" |], System.StringSplitOptions.None).Length - 1) 2 "for 하나, while 하나"
Expect.isFalse (mlir.Contains "llvm.func internal tailcc @lambda_") "반복 몸체가 최상위 함수가 되지 않음"
```

차등 테스트에는 다음 경우를 넣는다. 기대값은 인터프리터의 출력으로 만든다.

| 경우 | 확인하는 것 |
|---|---|
| `to`, `downto`, 빈 범위 | 방향과 시작 조건 |
| 끝값이 int의 끝 | 끝에서 `i ± 1`이 넘치지 않음 |
| start와 stop에 부수 효과 | 한 번씩, 왼쪽부터 |
| 몸체에서 `fun () -> i`를 모음 | 바퀴마다 다른 `i` |
| 겹친 `for`, 겹친 `while`, 조건의 부수 효과 | 영역 안의 영역 |
| 반복 안의 `raise`와 `try` | 반복을 빠져나가는 예외 |
| 100만 번 반복 | 스택이 자라지 않음 |

### Step 5: 더 나아간 최적화는 다른 반복에서도 잰다

반복을 함수 안으로 옮기자 ref 칸이 반복 안의 메모리로 남는 것이 보였다. `calc_alloc`의 반환값에 `noalias`를 붙이면 LLVM이 ref 칸을 레지스터로 올렸다.

| 예제 | 반복만 | + `noalias` |
|---|---|---|
| `while`과 ref, 3억 번 | 1.398초 | 1.235초 |
| 체 1억 | 0.658초 | 0.719초 |
| `benchmarks/` msort | 0.049초 | 0.053초 |

체의 세는 반복(`if isPrime.[k] then incr count`)에서, LLVM이 ref를 레지스터로 올린 뒤 드물게 타는 분기를 `csel`로 바꿨다. 모든 원소에서 덧셈을 하고, 반복마다 이어지는 의존 사슬이 생겼다. 그래서 `noalias`는 넣지 않았다.

## Example

예측과 측정이 다를 때는 측정을 따른다. 처음에는 체가 C보다 느린 원인의 약 15%가 ref 칸이라고 예상했다. 반복을 옮기고 ref를 레지스터로 올려도 체는 빨라지지 않았다. 남은 차이는 다른 데서 온다.

| 원인 | 설명 |
|---|---|
| 띄엄띄엄 쓰는 메모리 | 표시 반복이 배수마다 원소를 씀 (반복 시간의 대부분) |
| 범위 검사 | 원소 접근마다 비교 한 번 |
| tag 계산 | tagged int의 덧셈과 비교 |

## 체크리스트

- [ ] 앞 단계가 만드는 모양을 정확히, 좁게 알아보는가? (맞지 않으면 원래 경로)
- [ ] 바깥에서 한 번 계산한 값(`$start`, `$stop`)을 그대로 쓰는가?
- [ ] `for`의 끝에서 `i ± 1`이 넘치지 않는가?
- [ ] 몸체의 클로저가 바퀴마다 다른 `i`를 붙잡는가?
- [ ] LLVM이 이미 하던 일(꼬리 재귀를 반복으로)과 겹치지 않는 효과가 무엇인지 쟀는가?
- [ ] 더 센 최적화(`noalias`)가 다른 반복을 느리게 하지 않는지 여러 예제로 쟀는가?
- [ ] 생성 코드에 반복용 함수가 남지 않는지 테스트하는가?

## 관련 문서

- `81-give-llvm-alias-info-with-tbaa-and-noreturn.md` - TBAA와 noreturn
- `80-pass-inferred-int-types-to-codegen.md` - 타입을 쓰는 int 연산
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
