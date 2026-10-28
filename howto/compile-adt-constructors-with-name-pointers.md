---
created: 2026-10-28
description: 타입 검사를 통과한 프로그램의 생성자 값에는 생성자 이름 포인터 하나면 충분하다. 인자 없는 생성자는 정적 전역 블록으로 두어 할당하지 않고, 인자 있는 생성자는 적용 위치에서 바로 블록을 만들며, 값으로 쓰일 때만 클로저로 바꾼다. type 선언은 코드 없이 생성자 범위만 정한다
---

# ADT 생성자를 이름 포인터로 컴파일하기

`type shape = Circle of int | Rect of int * int | Dot` 같은 대수적 데이터 타입을 네이티브로 컴파일할 때, 흔히 **생성자 설명자**(타입 이름, 생성자 번호, 이름, 인자 수)를 만들어 블록마다 가리키게 한다. 이 프로젝트도 처음에는 그렇게 설계했다. 실제로 구현해 보니 **생성자 이름을 담은 C 문자열의 포인터 하나**로 충분했다.

```
Some 2   → [헤더 5 | (2 << 8), → "Some\0", 17]      (GC 할당)
None     → [헤더 5 | (1 << 8), → "None\0"]          (전역 상수 블록, 할당 없음)
```

## The Insight

**타입 검사기가 이미 보장하는 것을 실행 중에 다시 표현하지 않는다.** 실행 중에 생성자 정보가 필요한 곳은 세 군데다.

| 쓰는 곳 | 필요한 것 | 이름 포인터로 충분한 이유 |
|---|---|---|
| 패턴 `\| Some x` | 이 값이 그 생성자인가 | 비교하는 두 값은 타입 검사 덕분에 항상 **같은 타입**이다. 같은 타입 안에서 생성자 이름은 겹치지 않는다 |
| `=` | 생성자가 같은가 | 인터프리터도 생성자를 **이름으로** 비교한다(`c1 = c2`) |
| 출력, Match failure | 생성자 이름 | 이름 그 자체 |

타입 이름이 같은 다른 선언을 섞는 것도 타입 검사기가 막는다(직접 확인함).

```
type a = X in let v = X in type b = X in v = X
→ Error: Type mismatch: expected a but got b         (실행하기 전에 거부)

type a = X of int in let v = X 1 in type b = X of int in (v, X 2)
→ (X 1, X 2)                                        (인터프리터와 컴파일한 실행 파일 모두)
```

이름 전역을 모듈 안에서 이름마다 하나만 만들면, 패턴 검사는 **포인터 비교 한 번**이다. 런타임의 `=`는 다른 모듈에서 온 값도 다룰 수 있게 `strcmp`로 비교한다.

## Why This Matters

- **설명자를 만든 경우:** 타입마다 전역 테이블, 생성자 번호 할당, 타입 이름 문자열이 생깁니다. 그런데 쓰는 곳은 위 세 군데뿐이라 대부분 쓰이지 않습니다.
- **인자 없는 생성자도 할당한 경우:** `None :: rest`, `Leaf` 같은 흔한 값마다 GC 할당이 생깁니다.
- **인자 있는 생성자를 항상 함수로 만든 경우:** `Some 1`마다 클로저 생성과 간접 호출이 생깁니다. 그렇다고 함수로 만드는 경로가 아예 없으면 `let f = Some in f 3`이나 `map Some xs`를 컴파일할 수 없습니다.

## Recognition Pattern

- 정적 타입 언어(HM 추론, ML 계열)의 ADT를 컴파일할 때
- 런타임에 "이 값의 타입이 무엇인가"를 묻는 코드가 필요한지 고민될 때 → 타입 검사가 끝난 뒤라면 대부분 필요 없다.
- 기준 구현(인터프리터)이 생성자를 어떻게 비교하고 출력하는지 맞춰야 할 때

## The Approach

### Step 1: 생성자 이름 전역과 인자 없는 생성자의 정적 블록

```mlir
llvm.mlir.global internal constant @ctor_1("None\00") {addr_space = 0 : i32}

// 인자 없는 생성자의 값: 8바이트 정렬 전역 블록. 주소가 곧 값 (하위 3비트 000)
llvm.mlir.global internal constant @unit_0() {addr_space = 0 : i32, alignment = 8 : i64}
    : !llvm.struct<(i64, ptr)> {
  %0 = llvm.mlir.undef : !llvm.struct<(i64, ptr)>
  %h = llvm.mlir.constant(261 : i64) : i64                 // 5 | (1 << 8)
  %1 = llvm.insertvalue %h, %0[0] : !llvm.struct<(i64, ptr)>
  %n = llvm.mlir.addressof @ctor_1 : !llvm.ptr             // 초기화 영역 안에서 다른 전역의 주소
  %2 = llvm.insertvalue %n, %1[1] : !llvm.struct<(i64, ptr)>
  llvm.return %2 : !llvm.struct<(i64, ptr)>
}
```

LLVM IR에서는 `@unit_0 = internal constant { i64, ptr } { i64 261, ptr @ctor_1 }, align 8`가 된다. 이름 전역은 값이 아니라 블록 칸에 들어가므로 정렬이 필요 없다.

`clang -O2` 결과에서 `None :: ...`의 `None`은 할당 없이 상수로 저장된다(직접 확인함).

```llvm
store i64 ptrtoint (ptr @unit_0 to i64), ptr %14
```

### Step 2: `Lower`가 생성자 범위를 따라간다

`type` 선언은 코드를 만들지 않는다. 생성자 이름 → 인자가 있는지를 담은 Map을 넓힐 뿐이다. 안쪽 선언이 같은 이름을 가린다.

```fsharp
| TypeDecl (_, _, declared, body) ->
    let ctors = declared |> List.fold (fun acc (name, argType) -> Map.add name (Option.isSome argType) acc) ctors
    lower state ctors body
```

`Some`과 `None`은 처음 범위에 넣는다. 인터프리터의 초기 환경과 같다.

### Step 3: 적용 위치에서는 블록, 값 위치에서는 클로저

```fsharp
// Some 1: 그 자리에서 DATA 블록
| App (Ctor name, a) when Map.tryFind name ctors = Some true -> CData (name, Some (lower state ctors a))
| Ctor name ->
    match Map.tryFind name ctors with
    | Some false -> CData (name, None)                                    // None: 전역 블록
    | Some true -> state.AddFunction("x", None, CData (name, Some (CVar "x")))   // Some 을 값으로: fun x -> Some x
```

값 위치의 클로저는 인터프리터의 `VConstructor`와 같게 행동한다. 출력은 `<fun>`이고, `=`로 비교하면 같은 타입 오류가 난다.

### Step 4: 패턴은 이름 칸의 포인터 비교

```mlir
%name = llvm.load %slot1 : !llvm.ptr -> i64          // 블록의 이름 칸
%want = llvm.ptrtoint @ctor_0 ...                    // 이 모듈의 "Some" 전역
%isSome = arith.cmpi eq, %name, %want : i64
```

인자 패턴(`Some x`의 `x`, `Rect (w, h)`의 튜플)은 생성자가 맞을 때만 2번 칸을 읽어 검사하고 묶는다.

### Step 5: 출력 규칙은 기준 구현의 괄호까지 옮긴다

인터프리터는 다시 입력했을 때 같은 값이 되도록 인자에 괄호를 친다. 인자가 "인자 있는 생성자 값"이거나 음수일 때다.

```c
int paren = (CALC_IS_KIND(p, CALC_KIND_DATA) && CALC_SIZE(p) == 2) || (CALC_IS_INT(p) && CALC_INT_VAL(p) < 0);
```

| 값 | 출력 |
|---|---|
| `Some (Some 1)` | `Some (Some 1)` |
| `Some None` | `Some None` |
| `Some (-1)` | `Some (-1)` |
| `Rect (2, 3)` | `Rect (2, 3)` (튜플은 원래 괄호가 있음) |

## Example

```
type 'a tree = Leaf | Node of 'a tree * 'a * 'a tree in
let rec insert x t = match t with
  | Leaf -> Node (Leaf, x, Leaf)
  | Node (l, v, r) -> if x < v then Node (insert x l, v, r) else Node (l, v, insert x r) in
insert 2 (insert 3 (insert 1 Leaf))

→ Node (Leaf, 1, Node (Node (Leaf, 2, Leaf), 3, Leaf))     (인터프리터와 같음)
```

`Leaf`는 모두 같은 전역 블록을 가리키고, `Node`만 할당된다.

## 체크리스트

- [ ] 실행 중에 생성자 정보가 필요한 곳을 모두 적었는가? (패턴, `=`, 출력)
- [ ] 타입 검사기가 보장하는 것을 실행 중에 다시 검사하고 있지 않은가?
- [ ] 인자 없는 생성자가 할당 없이 전역 블록인가? (8바이트 정렬)
- [ ] 적용 위치의 생성자는 클로저 없이 바로 블록을 만드는가?
- [ ] 값 위치의 생성자는 함수 값처럼 행동하는가? (`<fun>`, `=` 오류)
- [ ] 출력의 괄호 규칙이 기준 구현과 같은가?

## 관련 문서

- `represent-adt-values-in-dynamic-interpreter.md` - 인터프리터 쪽 ADT 표현 (`VData`, `VConstructor`)
- `emit-string-constants-as-aligned-llvm-globals.md` - 전역 상수 블록과 정렬
- `compile-match-with-scf-if-conditions.md` - 생성자 패턴이 들어가는 `match` 코드 생성
- `implement-closure-conversion-to-top-level-functions.md` - 값 위치의 생성자가 되는 클로저
