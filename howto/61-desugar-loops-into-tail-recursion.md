---
order: 61
created: 2026-10-29
commit: ae29cc0
version: 0.35.0
description: while과 for는 새 AST 노드 없이 파서에서 사용자가 쓸 수 없는 이름의 let rec로 바꾼다. tail call이 보장되면 스택이 자라지 않고 백엔드는 할 일이 없다. for는 끝값을 한 번만 계산하고, i + 1을 끝에 닿기 전에만 계산해서 int 최댓값에서 무한 반복을 막는다
---

# 반복문을 tail recursion으로 바꿔 넣기

> 관련 커밋: [ae29cc0](https://github.com/ohama/calc-mlir/commit/ae29cc0) (v0.35.0)

인터프리터, 타입 추론, 컴파일러가 있는 언어에 `while`과 `for`를 넣으려면 보통 AST 노드를 더하고 각 단계마다 처리를 더한다. 이 문서는 **파서에서 `let rec`로 바꿔** 다른 단계를 하나도 고치지 않는 방법과, 그렇게 바꿀 때 놓치기 쉬운 세 가지를 다룬다.

## The Insight

**tail call이 보장되는 언어에서 반복문은 syntactic sugar다.** `while c do e done`은 "조건이 참이면 본문을 하고 자기 자신을 tail call하는 함수"와 같다. tail call이 스택을 쓰지 않으면(`54-guarantee-tail-calls-with-musttail-in-mlir.md`, `53-enable-fsharp-tailcalls-in-debug-for-interpreters.md`) 반복 횟수와 상관없이 돈다.

```
while c do e done  ==  let rec $while $u = if c then (e; $while ()) else () in $while ()
```

바꾼 식은 원래 언어의 식이므로 타입 추론, 경고, 평가, 컴파일이 모두 그대로 동작한다. 컴파일러에서는 이름에 묶은 함수라 direct call(`59-call-known-functions-directly-with-tailcc.md`)이 되고, 반복은 `tail` 호출이 된다.

## Why This Matters

`for`는 글자 그대로 옮기면 틀리는 곳이 세 군데 있다.

| 순진한 변환 | 문제 |
|---|---|
| `let rec f i = if i <= b then (e; f (i + 1)) else ()` | `b`를 반복마다 계산한다. 본문이 `b`가 읽는 ref를 바꾸면 반복 횟수가 바뀐다 |
| 같은 식, `b = 2147483647` | 마지막 반복 뒤 `i + 1`이 넘쳐서 `-2147483648`이 되고, 다시 `<= b`라서 끝나지 않는다 |
| 이름을 `for`, `stop`처럼 | 사용자의 이름과 겹치거나, 본문이 그 이름을 가린다 |

두 번째는 실제로 확인했다. 32비트 `int`가 한 바퀴 도는 언어에서 다음 식은 `int`의 최댓값을 지나 음수로 계속 간다.

```
$ calc -e 'let rec go i n = if i <= 2147483647 then (if n > 3 then (i, n) else go (i + 1) (n + 1)) else (i, n) in go 2147483646 0'
Result: (-2147483646, 4)
```

## Recognition Pattern

- tail call을 보장하는 언어에 반복문, `do` 블록, `loop` 같은 제어 구조를 넣을 때
- 새 구문이 기존 구문으로 표현되는데, 백엔드가 여럿이라 노드를 더하는 비용이 클 때
- 범위 반복(`for i = a to b`)의 끝값이 자료형의 최댓값일 수 있을 때

## The Approach

### Step 1: 사용자가 쓸 수 없는 이름을 쓴다

렉서가 만들 수 없는 이름(여기서는 `$`로 시작)을 쓴다. 사용자 이름과 겹치지 않고, 본문이 가릴 수도 없다. `x |> f`의 `$pipe`, `function`의 `$function`과 같은 방법이다.

중첩도 그대로 맞다. 안쪽 `for`의 `$for`, `$stop`은 안쪽 반복문 식 안에서만 보인다. 바깥 반복의 다음 호출(`$for (i + 1)`)은 본문 **뒤**에 있으므로 바깥 것을 가리킨다.

```
let s = ref 0 in for i = 1 to 2 do for i = 1 to 3 do s := !s + i done done; !s      (* 12 *)
```

### Step 2: `for`의 시작값과 끝값은 먼저 한 번 계산한다

```fsharp
/// for i = a to b do body done  ==
///   let $start = (a : int) in let $stop = (b : int) in
///   let rec $for i = body; if i < $stop then $for (i + 1) else () in
///   if $start <= $stop then $for $start else ()
let makeFor (var: string) (start: Expr) (up: bool) (stop: Expr) (body: Expr) =
    let tInt = TName("int", [])
    let before, step = if up then Less, Add else Greater, Subtract
    let startsBefore = if up then LessEqual(Var "$start", Var "$stop") else GreaterEqual(Var "$start", Var "$stop")
    let next = If(before(Var var, Var "$stop"), App(Var "$for", step(Var var, Number 1)), Unit)
    Let("$start", Annot(start, tInt),
        Let("$stop", Annot(stop, tInt),
            LetRec("$for", var, Seq(body, next), If(startsBefore, App(Var "$for", Var "$start"), Unit))))
```

- **계산 순서:** `$start`, `$stop` 순서로 묶어서 `a`를 먼저 계산한다(OCaml과 같음).
- **끝을 확인하는 위치:** 들어가기 전에 `$start <= $stop`을 한 번 보고, 반복 안에서는 본문 **뒤**에 `i < $stop`일 때만 `i + 1`을 계산한다. 끝에 닿으면 더하지 않으므로 넘치지 않는다. `downto`는 비교와 증감만 반대다.
- **`(a : int)` 표기:** `<`는 `int`와 `string`을 받으므로 표기가 없으면 `for i = "a" to "z"`의 오류가 엉뚱한 곳을 가리킨다. 표기를 붙이면 `a`를 가리키는 `expected int but got string`이 된다.
- **반복 변수 `i`:** `$for`의 인자라서 본문 안에서만 보이고 반복마다 새로 묶인다. 본문이 `let i = …`로 가려도 다음 비교는 인자 `i`를 본다. `Seq (body, next)`에서 본문의 `let`은 `body` 안에서 끝나기 때문이다.

### Step 3: 본문을 `e; …`로 두어 경고를 그대로 쓴다

본문을 `Seq`의 앞쪽에 두면 "버리는 값이 unit이 아님" 경고가 그대로 나온다(F#의 FS0020과 같은 동작).

```
$ calc -e 'for i = 1 to 3 do i done'
Warning: this expression has type int and its value is ignored (use ignore or let _ = ...) (line 1, column 19)
```

### Step 4: 문법은 `done`으로 닫는다

```
| WHILE SeqExpr DO SeqExpr DONE
| FOR IDENT EQUALS SeqExpr TO SeqExpr DO SeqExpr DONE
| FOR IDENT EQUALS SeqExpr DOWNTO SeqExpr DO SeqExpr DONE
```

끝 토큰이 있으므로 우선순위 선언이 필요 없고 충돌도 생기지 않는다. 본문에는 `;`가 들어갈 수 있다(`do a; b done`). 새 키워드(`while`, `for`, `to`, `downto`, `do`, `done`)는 기존 프로그램의 변수 이름을 막으므로, 바꾼 뒤 전체 테스트를 돌려 그 이름을 쓰던 예제가 없는지 확인한다.

## Example

```
$ calc -e 'let n = ref 10 in while !n > 0 do n := !n - 3 done; !n'
Result: -2
```

| 예제 | 결과 | 확인하는 것 |
|---|---|---|
| `for i = 2147483646 to 2147483647 do s := !s + 1 done` | 2번 | 최댓값에서 끝남 |
| `let b = ref 2 in for i = 1 to !b do b := 10; … done` | 2번 | 끝값은 한 번만 계산 |
| `for i = 1 to 0 do … done` | 0번 | 들어가기 전 확인 |
| `while !n < 1000000 do … done` | 인터프리터에서도 끝남 | tail call |
| `-O0`으로 컴파일한 `for` 1000만 번 | 끝남 | `tail` 호출이 보장됨 |

컴파일한 코드에서 1억 번 반복은 0.22초이고, 같은 일을 하는 `let rec go i s = …`와 같다.

## 체크리스트

- [ ] 바꾼 식의 이름이 사용자 이름과 겹치지 않는가? (렉서가 만들 수 없는 이름)
- [ ] 끝값을 반복 전에 한 번만 계산하는가?
- [ ] 끝값이 자료형의 최댓값일 때 끝나는가? (`i + 1`을 끝에 닿기 전에만 계산)
- [ ] 시작값과 끝값에 타입 표기를 붙여 오류가 그 식을 가리키는가?
- [ ] 다음 반복이 tail position에 있는가? (`-O0` 테스트로 확인)
- [ ] 새 키워드가 기존 예제의 이름을 막지 않는가?

## 관련 문서

- `54-guarantee-tail-calls-with-musttail-in-mlir.md` - 컴파일러의 tail call 보장
- `59-call-known-functions-directly-with-tailcc.md` - 반복 함수가 direct call이 되는 이유
- `60-add-value-restriction-to-hm-with-levels.md` - 같은 릴리스의 mutable reference와 value restriction
