---
order: 76
created: 2026-10-29
commit: ac1be61
version: 0.50.0
description: 생성자의 튜플 인자를 data 블록에 펴서 넣으면 트리 노드 하나가 블록 하나가 되어 GC가 표시할 객체가 반이 된다. 블록의 단어 수로 "인자 하나"와 "편 원소들"을 구분하면 컴파일러가 생성자의 인자 수를 몰라도 된다. 리터럴이 아닌 인자와 인자 전체를 묶는 패턴만 런타임이 펴고 다시 묶는다
---

# 생성자의 튜플 인자를 블록 크기로 구분해 펴기

> 관련 커밋: [ac1be61](https://github.com/ohama/calc-mlir/commit/ac1be61) (v0.50.0)

0.49.0의 실행 파일에서 BST 20만은 시간 대부분이 Boehm GC였다(`75-profile-native-executable-and-tune-boehm-gc.md`). GC는 수거할 때마다 살아 있는 트리 전체를 표시한다. 그런데 트리 노드 하나가 블록 두 개였다.

```
0.49.0: Node (l, v, r)  =  [헤더 5|2<<8, "Node", •] ──▶ [헤더 4|3<<8, l, v, r]      (data 블록 + 튜플 블록)
0.50.0: Node (l, v, r)  =  [헤더 5|4<<8, "Node", l, v, r]                           (블록 하나)
```

| 실행 파일 (초) | Python | 0.49.0 | 0.50.0 |
|---|---|---|---|
| BST 20만 | 0.350 | 0.131 | 0.064 |

## The Insight

**블록의 단어 수만으로 표현을 구분할 수 있으면, 컴파일러가 타입 정보를 들고 다니지 않아도 된다.**

| data 블록의 단어 수 | 뜻 |
|---|---|
| 1 | 인자 없는 생성자 (`None`, `Leaf`) |
| 2 | 튜플이 아닌 인자 하나 (`Some 1`, `C [1]`) |
| 3 이상 | 튜플 인자의 원소를 편 것 (`Rect (2, 3)`, `Node (l, v, r)`) |

원소가 하나인 튜플은 없으므로 두 경우가 겹치지 않는다. 그래서 컴파일러가 생성자 선언에서 인자 수를 읽어 와 Codegen까지 넘길 필요가 없다. 같은 생성자 이름이 다른 `type` 선언에 다시 나와도 문제가 없다.

**모든 경우를 빠르게 할 필요는 없다.** 빠른 경로가 필요한 것은 흔한 두 모양뿐이다.

- **만들기:** 튜플 리터럴을 인자로 주는 `Node (l, v, r)`
- **패턴:** 원소마다 패턴을 쓰는 `Node (l, v, r)`

나머지 경우는 실행 중에 펴고 다시 묶어도 된다.

- `map Node xs`: 생성자를 함수 값으로 쓰기
- `Node t`: 튜플 변수를 인자로
- `Node p`: 인자 전체를 묶는 패턴

## Why This Matters

- **GC 비용은 블록 수를 따라간다.** Boehm GC는 세대 구분이 없어 수거할 때마다 살아 있는 블록을 모두 표시한다. 트리 노드 하나가 블록 둘이면 표시할 것도 둘이다.
- **할당과 포인터 따라가기도 반이 된다.** 할당이 두 번에서 한 번이 되고, 패턴의 원소 읽기에서 포인터를 한 번 덜 따라간다.
- **예상보다 효과가 컸다.** 조사할 때는 GC 표시 작업만 보고 -10~15%로 짐작했다. 실제로는 -51%였다. 짐작은 재 보기 전에는 믿지 않는다.

## Recognition Pattern

- tagged 값 표현을 쓰는 컴파일러에서 ADT 생성자 값이 `[이름, 인자]`이고, 인자가 다시 튜플 블록일 때
- 트리처럼 오래 사는 ADT 데이터가 많은 프로그램에서 GC가 시간 대부분일 때
- 표현을 바꾸고 싶지만 Codegen에 타입 정보가 없을 때

## The Approach

### Step 1: 만들기: 리터럴은 바로 펴고, 나머지는 실행 중에 확인

```fsharp
| CData (ctor, Some (CTuple items)) ->                  // Node (l, v, r): 원소를 블록에 바로
    let values = items |> List.map (emitExpr fb env)
    emitBlock fb (flatDataHeader values.Length) (emitGlobalAddress fb (fb.Strings.CtorName ctor) :: values)
| CData (ctor, Some payload) ->                         // Some 2, Node t, map Node xs 의 Node
    let pv = emitExpr fb env payload
    let name = emitGlobalAddress fb (fb.Strings.CtorName ctor)
    emitIf fb (emitIsTuple fb pv)                      // 하위 3비트가 0 이고 헤더의 종류가 튜플인가
        (fun () -> emitCall fb "calc_make_data" [ name; pv ] "i64")
        (fun () -> emitBlock fb (dataHeader true) [ name; pv ])
```

- **확인이 접히는 경우:** `Some 2`처럼 인자가 상수면 clang이 확인을 접는다.
- **확인이 남는 경우:** `Some x`처럼 인자가 변수면 확인 하나(tag와 헤더 읽기)가 남는다. 그래도 튜플 블록을 하나 더 만들던 것보다 싸다.

### Step 2: 패턴: 원소 패턴은 칸을 바로, 인자 전체는 단어 수를 보고

```fsharp
| Ast.PCtor (ctor, Some (Ast.PTuple items)) ->          // Node (l, v, r): i 번째 원소가 칸 2 + i
    ... fields (items |> List.mapi (fun i p -> p, 2 + i))
| Ast.PCtor (ctor, Some p) ->                           // Some x, Node p: 인자 하나를 값으로
    ... emitTest fb p (emitPayload fb v)

/// 단어 수가 2 면 칸 2, 아니면 원소들로 튜플을 다시 만듦
let private emitPayload fb v =
    let words = (헤더 >>> 8)
    emitIfOf fb "i64" (words = 2) (fun () -> emitField fb v 2) (fun () -> emitCall fb "calc_data_payload" [ v ] "i64")
```

- **`emitTest`와 `emitBind` 둘 다:** 패턴 검사(`emitTest`)와 이름 묶기(`emitBind`)를 같은 규칙으로 고친다.
- **타입 검사가 보장하는 것:** `Node (l, v, r)` 패턴이 붙는 값은 타입 검사로 언제나 펴진 블록이다. 그래서 원소 패턴 쪽은 단어 수를 확인하지 않는다.

### Step 3: 런타임: 펴고 다시 묶는 함수, 출력과 비교

```c
value calc_make_data(value name, value tuple) {        /* 튜플을 펴서 [헤더, 이름, 원소...] */
    int64_t n = CALC_SIZE(tuple);
    value *block = calc_alloc(8 * (2 + n));
    block[0] = CALC_KIND_DATA | ((1 + n) << 8);
    block[1] = name;
    for (int64_t i = 1; i <= n; i++) block[1 + i] = CALC_FIELD(tuple, i);
    return (value)block;
}
```

- **`calc_data_payload`:** 거꾸로, 칸 2부터 원소들을 모아 튜플 블록을 만든다.
- **출력:** 단어 수가 3 이상이면 `Rect (2, 3)`처럼 원소를 괄호 안에 쉼표로 출력한다. 인자 하나를 튜플로 받던 예전 출력과 글자 하나까지 같다.
- **괄호 규칙:** 인자의 괄호 규칙(`Some (Node (…))`)은 "data 블록이고 단어 수가 2 이상"으로 넓힌다.
- **`=`:** 같은 생성자면 단어 수가 같으므로 칸 2부터 끝까지 비교한다.

### Step 4: 펴진 블록에서만 생기는 경우를 차등 테스트로

인터프리터는 표현이 다르다(`VData (t, c, VTuple [| … |])`). 그래서 같은 프로그램의 출력, 오류, 종료 코드를 비교하면 표현이 맞는지 확인할 수 있다(`26-write-differential-tests-for-compiler.md`).

| 경우 | 프로그램 |
|---|---|
| 생성자를 함수 값으로 | `let f = B in map f [(1, 2)]` |
| 인자 전체를 묶는 패턴 | `match B (1, 2) with B p -> p` |
| 튜플 변수를 인자로 | `let mk p = B p in mk (5, 6) = B (5, 6)` |
| or 패턴 | `match Some (3, 4) with Some ((1, x) \| (3, x)) -> x` |
| 예외의 튜플 인자 | `raise (E (2, "b"))` → `Error: exception E (2, "b")` |
| 안쪽 튜플 | `W ([1], (2, 3))`: 바깥만 펴고 `(2, 3)`은 블록 그대로 |
| 출력이 섞임 | `[Some (1, 2); None]`, `Some (-1)` |

새 테스트는 고치기 전 컴파일러에서도 통과해야 한다. 그래야 결과가 같다는 근거가 된다.

## Example

배열 원소 읽기와 쓰기도 같은 릴리스에서 생성 코드로 옮겼다. 원소 수는 헤더의 단어 수다.

```fsharp
let size = (헤더 >>> 8)
// 음수 인덱스는 부호 없는 수로 보면 size 보다 크므로 비교 한 번으로 범위 밖을 잡음
fb.Emit(sprintf "%s = arith.cmpi uge, %s, %s : i64" outside index size)
emitErrorIf fb outside "calc_error_index"              // "Index was outside the bounds of the array."
... llvm.getelementptr %block[index + 1]
```

| 변경 (실행 파일, 초) | sieve 3천만 | BST 20만 |
|---|---|---|
| 0.49.0 | 0.32 | 0.131 |
| 배열 읽기, 쓰기를 생성 코드에서 | 0.27 | 0.131 |
| 생성자의 튜플 인자를 폄 | 0.27 | 0.064 |

## 체크리스트

- [ ] 값 표현을 블록의 단어 수로 구분할 수 있는가? (원소가 하나인 튜플은 없음)
- [ ] 흔한 모양(리터럴로 만들기, 원소 패턴)만 빠른 경로로 두었는가?
- [ ] 리터럴이 아닌 인자와 인자 전체를 묶는 패턴을 런타임이 펴고 다시 묶는가?
- [ ] 패턴 검사와 이름 묶기를 모두 고쳤는가?
- [ ] 출력, 괄호 규칙, `=`가 펴진 블록을 예전 표현과 같게 다루는가?
- [ ] 펴진 블록에서만 생기는 경우를 차등 테스트로 넣었는가?
- [ ] 새 테스트가 고치기 전 컴파일러에서도 통과하는가?

## 관련 문서

- `75-profile-native-executable-and-tune-boehm-gc.md` - 실행 파일의 profile과 Boehm GC
- `73-reduce-long-lived-objects-not-short-lived-ones.md` - 인터프리터 쪽의 같은 생각 (오래 사는 객체 수)
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
