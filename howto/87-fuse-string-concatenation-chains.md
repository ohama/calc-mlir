---
order: 87
created: 2026-10-30
commit: 843fafa
version: 0.62.0
description: a + b + c는 중간 문자열을 만들고 복사한다. 타입 추론이 string으로 정한 + 노드를 컴파일러에 넘기고, 이어진 연쇄를 조각 목록으로 펴서 런타임이 길이를 더해 한 번 할당한다. 타입 추론을 거치지 않는 펼친 코드(sprintf)는 낮출 때 표시한다. 조각 배열을 alloca로 넘기면 반복문 안에서 스택이 자라므로 조각 수마다 런타임 함수를 둔다
---

# 문자열 + 연쇄를 한 번의 할당으로 합치기

> 관련 커밋: [843fafa](https://github.com/ohama/calc-mlir/commit/843fafa) (v0.62.0)

0.61.0까지 컴파일러는 `a + b + c + d`를 `((a + b) + c) + d`대로 런타임의 `calc_add`를 세 번 불렀다. 호출마다 새 문자열을 할당하고 앞의 내용을 다시 복사한다. 조각이 k개면 할당이 k − 1번이고 앞쪽 조각은 k − 1번 복사된다. `sprintf "%d-%d:%s"`도 펼치면 `toString $f0 + "-" + toString $f1 + ":" + $f2`라 같은 비용이다.

| 프로그램 (실행 파일) | 0.61.0 | 0.62.0 |
|---|---|---|
| `"item " + toString i + " of " + …` (일곱 조각) 2백만 번 | 0.17초 | 0.08초 |
| `sprintf "%d-%d:%s"` 2백만 번 | 0.11초 | 0.06초 |
| `sprintf "%d,%s,%d,%b"` 백만 번 | 0.12초 | 0.07초 |

## The Insight

**문자열 연결은 결합 법칙이 성립하고, 조각을 계산하는 순서는 괄호와 관계없이 왼쪽부터다.** `(a + b) + c`와 `a + (b + c)`는 결과가 같고, 둘 다 `a`, `b`, `c` 순서로 계산한다. 그래서 이어진 `+`를 조각 목록 `[a; b; c]`로 펴도 결과도 부수 효과의 순서도 바뀌지 않는다. 조각을 모두 계산한 뒤 길이를 더하면 할당은 한 번, 복사는 조각마다 한 번이다.

조건은 하나다. **그 `+`가 문자열 연결이라는 것을 컴파일러가 알아야 한다.** Calc의 `+`는 int 덧셈이기도 하고, Core IR에는 타입이 없다. 타입 추론은 이미 노드마다 피연산자 타입을 알고 있으므로, int 연산을 넘긴 방식(`80-pass-inferred-int-types-to-codegen.md`)으로 string인 `+`도 넘긴다.

## Why This Matters

- **문자열을 만드는 코드는 거의 모두 연쇄다.** 출력 줄, 로그, CSV 행, `sprintf`가 모두 조각 서너 개 이상이다.
- **펼친 코드는 타입 추론을 지나가지 않는다.** `sprintf`의 타입은 포맷 문자열에서 바로 계산하고, 펼친 식(`Format.expand`)은 낮추기 직전에 만든다. 추론의 결과만 보면 포맷 안의 `+`는 빠진다. 효과가 가장 큰 곳이 빠지는 셈이다.
- **반복문 안의 `alloca`는 스택을 키운다.** 조각 배열을 `alloca`로 만들어 런타임에 넘기는 것이 가장 쉬워 보인다. 하지만 함수의 entry block이 아닌 곳(`scf.while`의 본문)의 `alloca`는 반복마다 스택을 새로 잡는다. 1억 번 도는 반복에서 스택 넘침이 된다.

## Recognition Pattern

- 런타임의 문자열 연결 함수(`calc_add`, `concat`)와 `GC_malloc`, `memmove`가 profile 위쪽에 있을 때
- 문자열 포맷이 연결 연산으로 펼쳐지는 언어
- 연산자 하나가 타입에 따라 다른 일을 하고(int 덧셈과 문자열 연결), 타입 추론은 그것을 알지만 IR은 모를 때

## The Approach

### Step 1: 효과를 먼저 잰다

같은 C 프로그램에서 연결만 두 가지로 바꾼다. 0.61.0 조사에서는 다섯 조각 2백만 번이 0.18초에서 0.14초였다. 실제로는 2배였는데, 모델은 int를 `snprintf`로 바꾸는 고정 비용이 커서 몫이 작게 보였다. 모델은 "할 만한가"를 정하는 데 쓰고, 최종 숫자는 실제 결과로 적는다.

### Step 2: 타입 추론에서 string인 `+`를 낸다

추론 중에는 `+` 노드마다 피연산자 타입(type variable일 수 있음)을 기록해 두고, 추론이 끝난 뒤에 확정된 타입을 본다.

```fsharp
for node, operand in st.Operands do
    match prune operand, node with
    | TyCon ("int", []), _ -> ints.Add node |> ignore
    | TyCon ("string", []), (:? Expr as e) when (match e with Add _ -> true | _ -> false) -> stringAdds.Add node |> ignore
    | _ -> ()
intOperationTable.AddOrUpdate(expr, ints)
stringAddTable.AddOrUpdate(expr, stringAdds)   // ConditionalWeakTable: AST 루트에 붙여 둠
```

`Infer.stringAdds expr`가 이 집합을 돌려준다. 타입 검사를 하지 않은 식이면 빈 집합이라 전처럼 동작한다.

### Step 3: 낮출 때 연쇄를 편다

```fsharp
// string 연쇄는 펴서 한 번에: a + (b + c) 도 조각은 왼쪽부터 a, b, c (계산 순서가 같음)
| Add _ when state.IsStringAdd expr ->
    let rec pieces e =
        match e with
        | Add (a, b) when state.IsStringAdd e -> pieces a @ pieces b
        | _ -> [ e ]
    prim PConcat (pieces expr)
```

펼친 포맷은 낮추기 전에 표시한다. 펼친 식의 `+`는 조각이 문자열 리터럴, `toString`의 결과, `%s` 인자라서 모두 문자열 연결이다.

```fsharp
| Printf (kind, format) ->
    let expanded = Format.expand kind pieces
    state.MarkStringAdds expanded      // 펼친 식의 모든 Add 노드를 string 으로
    lower state ctors emptyScope expanded
```

Infer의 집합은 바꾸지 않고, `State`가 복사본을 만들어 표시를 더한다.

### Step 4: 조각 수마다 런타임 함수를 둔다

```c
static value concat_parts(const value *parts, int n) {
    int64_t length = 0;
    for (int i = 0; i < n; i++) length += CALC_STRING(parts[i])->length;
    calc_string *r = calc_new_string(length);
    ...                                   /* 차례로 memcpy */
}
value calc_concat3(value a, value b, value c) { return concat_parts((value[]){a, b, c}, 3); }
```

`calc_concat2`부터 `calc_concat8`까지 둔다. 조각 배열은 C 함수 안의 지역 배열이라 C 컴파일러가 스택 프레임에 한 번 잡는다. Codegen은 조각이 8개보다 많으면 앞 8개를 먼저 잇고, 그 결과를 첫 조각으로 다시 센다.

```fsharp
let rec concat (values: string list) =
    if values.Length <= maxConcat then emitCall fb (sprintf "calc_concat%d" values.Length) values "i64"
    else concat (emitCall fb (sprintf "calc_concat%d" maxConcat) (List.take maxConcat values) "i64" :: List.skip maxConcat values)
```

### Step 5: 계산 순서와 경계를 차등 테스트한다

| 경우 | 프로그램 |
|---|---|
| 계산 순서 | `(print "a"; "x") + (print "b"; "y") + ((print "c"; "z") + (print "d"; "w"))` → `abcdxyzw` |
| 오른쪽 괄호, 빈 문자열 | `"x" + ("y" + "z")`, `"" + "" + ""` |
| 8개를 넘는 조각 | `"1" + "2" + … + "18"` |
| 포맷 | `sprintf "%d-%s-%b-%A"`, 조각 하나(`sprintf "%s"`), 지정자 없음 |
| BMP 밖의 글자 | `"😀" + "é" + "\n"` (code unit 단위 복사) |

MLIR 테스트에서는 호출 수를 센다. **인자가 여럿인 함수의 본문은 클로저와 direct 함수 두 곳에 생성된다.** 함수 안의 연쇄를 세면 2가 나오므로, 수를 세는 테스트는 최상위 식으로 쓰고 함수 안의 것은 있는지만 본다.

## Example

```
let a = toString 1 in let b = toString 2 in a + b + "!" + (b + a)
```

```mlir
// 0.61.0: calc_add 네 번, 중간 문자열 세 개
// 0.62.0:
%v7 = llvm.call @calc_concat5(%v2, %v4, %v6, %v4, %v2) : (i64, i64, i64, i64, i64) -> i64
```

## 체크리스트

- [ ] 연결이 결합 법칙을 만족하고, 펴도 조각의 계산 순서가 같은가?
- [ ] 타입 추론이 확정한 노드만 바꾸고, 정해지지 않은 것과 타입 검사를 하지 않은 식은 그대로인가?
- [ ] 타입 추론을 거치지 않는 펼친 코드(포맷)도 표시했는가?
- [ ] 반복문 안에서 `alloca`를 만들지 않는가?
- [ ] 조각 수의 상한을 넘는 연쇄를 테스트했는가?
- [ ] MLIR 테스트의 호출 수가 함수 본문이 두 번 생성되는 것에 흔들리지 않는가?

## 관련 문서

- `80-pass-inferred-int-types-to-codegen.md` - 타입 추론의 결과를 노드 집합으로 컴파일러에 넘기기
- `86-survey-language-features-for-runtime-fast-paths.md` - 이 일을 찾은 조사
- `26-write-differential-tests-for-compiler.md` - 인터프리터와 컴파일러의 결과 비교
