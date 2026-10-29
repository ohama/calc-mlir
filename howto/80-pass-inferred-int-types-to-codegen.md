---
order: 80
created: 2026-10-29
commit: 0214b14
version: 0.54.0
description: 타입 없는 IR을 쓰는 컴파일러에 타입 추론 결과를 조금만 넘겨도 int 연산의 태그 검사를 없앨 수 있다. 추론이 끝난 뒤 피연산자가 int로 정해진 노드만 모아 AST 루트에 붙여 두면 호출하는 쪽의 시그니처를 바꾸지 않는다. generalize된 변수와 정해지지 않은 변수는 int로 표시하지 않아야 안전하다
---

# 타입 추론 결과를 Codegen에 넘겨 int 연산의 태그 검사 없애기

> 관련 커밋: [0214b14](https://github.com/ohama/calc-mlir/commit/0214b14) (v0.54.0)

이 컴파일러의 Core IR에는 타입이 없다. `+`와 `<`는 int일 수도 string일 수도 있다. 그래서 0.49.0부터는 생성 코드가 왼쪽 값의 태그를 보고 int면 그 자리에서 계산하고, 아니면 런타임 함수를 불렀다(`75-profile-native-executable-and-tune-boehm-gc.md`). 이 검사와 거기서 갈라지는 경로가 계산 위주 코드에 남아 있었다.

| 실행 파일 (초) | 0.53.0 | 0.54.0 | 같은 C 코드 |
|---|---|---|---|
| fib 41 | 0.441 | 0.329 | 0.28 |
| N-Queens 13 | 0.528 | 0.412 | |

## The Insight

**타입 정보 전체를 IR에 넣지 않아도 된다. 필요한 사실 하나만 노드에 붙이면 된다.**

| 해야 할 일 | 방법 |
|---|---|
| 필요한 사실 | "이 연산의 피연산자는 int다" (노드 하나에 bool 하나) |
| 붙이는 곳 | 타입 추론이 끝난 뒤, int로 정해진 노드의 집합을 AST 루트에 붙임 |
| 쓰는 곳 | Lower가 그 노드를 int 전용 연산(`PIntAdd`, `PIntLess`, …)으로 바꿈 |

**"정해진 것"만 표시해야 안전하다.** 추론 중의 type variable은 나중에 다른 타입이 될 수 있다. 또 generalize된 변수는 쓰이는 곳마다 다른 타입이 된다. 추론이 모두 끝난 뒤 `prune`한 결과가 `int`인 것만 표시한다.

## Why This Matters

- **태그 검사는 싸지만 그 뒤가 비싸다.** 검사 자체는 `and`와 `cmp` 두 개다. 그런데 런타임 경로(`calc_compare`, `calc_add`)가 함수 안에 남으면, 호출을 대비해 레지스터를 더 저장하고 코드가 커진다. fib의 어셈블리에서 `stp`가 셋이었다.
- **잘못 표시하면 결과가 틀린다.** string `<`를 int 비교로 만들면 포인터 값을 비교한다. 다형 함수의 `=`를 int 비교로 만들면 리스트를 비트로 비교한다.

## Recognition Pattern

- 타입 추론은 있지만 Core IR이나 Codegen에 타입이 없는 컴파일러
- 생성 코드에 "값의 종류를 확인하고 갈라지는" 분기가 많고, 대부분 한쪽으로만 갈 때
- 타입 정보를 IR 전체에 넣기는 부담스러울 때

## The Approach

### Step 1: 타입 추론이 연산 노드와 피연산자 타입을 기록한다

```fsharp
// State 에 추가
Operands: List<obj * Type>

| Add (a, b) ->
    let t = sameIntOrString a b
    st.Operands.Add((box expr, t))
    t
| Less (a, b) | Greater (a, b) | LessEqual (a, b) | GreaterEqual (a, b) ->
    st.Operands.Add((box expr, sameIntOrString a b))
    tBool
| Equal (a, b) | NotEqual (a, b) -> ... st.Operands.Add((box expr, t)) ...
```

### Step 2: 추론이 끝난 뒤 int로 정해진 것만 모은다

```fsharp
let t = infer st (initialContext st) expr
applyDefaults st t
let ints = HashSet<obj>(HashIdentity.Reference)
for node, operand in st.Operands do
    match prune operand with
    | TyCon ("int", []) -> ints.Add node |> ignore
    | _ -> ()                                   // generalize 한 변수, 정해지지 않은 변수, string, list, ...
intOperationTable.AddOrUpdate(expr, ints)
```

- **왜 안전한가:** 다음 세 경우는 int로 표시되지 않고 태그 검사가 남는다.

  | 경우 | 이유 |
  |---|---|
  | `let eq x y = x = y` | `'a`가 generalize되어 쓰는 곳마다 복사된다. 노드에 기록한 원래 변수는 끝까지 변수로 남는다 |
  | `let f x y = x < y` (쓰이지 않음) | "int 또는 string"으로 제한된 변수는 generalize되지 않는다. 쓰인 곳이 타입을 정하고, 쓰이지 않으면 끝까지 변수다 |
  | `"a" < "b"` | `string` |

- **기본값 단계와 순서:** `applyDefaults`는 남은 변수를 기본 타입(int)으로 정한다. 그런데 프로그램 결과 타입에만 적용하므로, 실행 중에 그 연산이 다른 타입의 값을 받는 일은 없다.

### Step 3: 결과를 AST 루트에 붙이고, 없으면 빈 집합을 준다

```fsharp
let private intOperationTable = ConditionalWeakTable<Expr, HashSet<obj>>()

let intOperations (expr: Expr) : HashSet<obj> =
    match intOperationTable.TryGetValue expr with
    | true, ints -> ints
    | _ -> HashSet<obj>(HashIdentity.Reference)        // 타입 검사하지 않은 식: 지금처럼 태그를 봄
```

- **시그니처를 바꾸지 않는다:** 컴파일 경로(`Program.compile`)와 테스트는 모두 "같은 AST 객체를 타입 검사한 뒤 Codegen에 넘긴다". 그래서 `Lower.lowerProgram expr`가 `Infer.intOperations expr`를 읽기만 하면 된다. 타입 검사와 Codegen의 시그니처는 그대로다.
- **테스트가 새 경로를 거친다:** 차등 테스트도 실제 컴파일과 같은 경로를 지난다.
- **메모리:** `ConditionalWeakTable`이라 AST가 사라지면 표도 사라진다.
- **같은 방법:** 이 프로젝트가 오류 위치와 레코드 패턴의 필드 순서를 노드 참조로 붙이는 방법과 같다(`41-track-source-positions-without-changing-ast.md`).

### Step 4: Lower와 Codegen은 표시된 노드만 바꾼다

```fsharp
// Lower
| Add (a, b) -> prim (if state.IsInt expr then PIntAdd else PAdd) [ a; b ]
| NotEqual (a, b) -> CPrim (PNot, [ prim (if state.IsInt expr then PIntEqual else PEqual) [ a; b ] ])

// Codegen
| CPrim (PIntAdd, [ a; b ]) -> intOp "arith.addi" a b      // 태그를 떼고 i32 로 (넘침 규칙은 - 와 같음)
| CPrim (PIntLess, [ a; b ]) -> ... arith.cmpi slt, x, y : i64 ...   // 태그를 붙인 채로 비교
```

### Step 5: 생성한 코드의 모양을 테스트한다

결과가 같은지만 보면, 최적화가 실제로 적용되었는지는 모른다. 생성한 MLIR에 무엇이 남는지도 확인한다.

```fsharp
let fib = typed "let rec fib n = if n < 2 then n else fib (n - 1) + fib (n - 2) in fib 10"
for f in [ "calc_compare"; "calc_add"; "calc_eq" ] do
    Expect.isFalse (fib.Contains("llvm.call @" + f + "(")) f
Expect.isTrue ((typed "let eq x y = x = y in (eq 1 1, eq \"a\" \"b\")").Contains "llvm.call @calc_eq(") "다형 = 는 태그를 봄"
```

## Example

**함정:** "int 또는 string"으로 제한된 변수가 generalize되지 않는다는 점을 잊으면 테스트를 잘못 쓴다.

```
let lt x y = x < y in (lt 1 2, lt "b" "a")      // 타입 오류: 첫 사용이 int 로 정함
let lt x y = x < y in let lts x y = x < y in (lt 1 2, lts "b" "a")   // 둘로 나눔
```

컴파일 테스트는 모든 입력을 실행 파일 하나로 빌드한다. 그래서 타입 오류가 하나만 있어도 컴파일 테스트 전체가 실패한다.

## 체크리스트

- [ ] 추론이 모두 끝난 뒤 `prune`한 타입으로 판단하는가?
- [ ] generalize된 변수, 정해지지 않은 변수는 표시하지 않는가?
- [ ] 정보가 없으면(타입 검사하지 않은 식) 지금처럼 동작하는가?
- [ ] 컴파일 경로와 테스트가 같은 AST 객체를 타입 검사한 뒤 Codegen에 넘기는가?
- [ ] 생성 코드에 런타임 호출이 남는지 없는지 테스트하는가? (int, string, 다형 함수)
- [ ] 큰 입력으로 효과를 쟀는가? (작은 벤치마크는 시작 비용에 가려짐)

## 관련 문서

- `75-profile-native-executable-and-tune-boehm-gc.md` - 태그를 보고 고르는 int 경로 (0.49.0)
- `41-track-source-positions-without-changing-ast.md` - 노드 참조로 정보를 붙이는 방법
- `60-add-value-restriction-to-hm-with-levels.md` - generalize와 value restriction
- `20-implement-hindley-milner-with-levels.md` - level을 쓰는 HM 타입 추론
