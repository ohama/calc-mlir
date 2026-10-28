---
created: 2026-10-28
description: 동적 평가기가 이미 있다면 그 테스트 예제를 그대로 타입 추론에 넣는다. 통과한 식은 실행 중 오류 없이 추론한 타입의 값이 되어야 하고, 거부한 식은 목록으로 고정해서 하나씩 확인한다
---

# 평가기 예제를 재사용해서 타입 추론의 soundness 테스트하기

타입 추론기를 나중에 붙인 언어에는 이미 평가기 테스트 예제가 수백 개 있다. 이것을 그대로 **타입 추론의 입력**으로 쓴다. 확인하는 성질은 둘이다.

1. **soundness**: 타입 검사를 통과한 식은 실행 중 타입 오류 없이 평가되고, 결과 값이 추론한 타입과 맞는다.
2. **completeness 점검**: 타입 검사가 거부한 식 목록을 테스트에 적어 두고, 하나하나 "정말 타입이 맞지 않는가"를 사람이 확인한다.

추론 결과를 손으로 적은 테스트는 내가 생각한 경우만 확인한다. 이 방법은 **내가 생각하지 못한 식**에서 추론기의 버그를 찾는다.

## The Insight

타입 추론의 정답은 두 방향으로 틀릴 수 있다.

| 틀림 | 증상 | 이 테스트에서 |
|---|---|---|
| 너무 너그러움 (unsound) | 통과했는데 실행 중 `Type error` | 1번이 실패 |
| 너무 엄격함 (incomplete) | 맞는 프로그램을 거부 | 2번의 목록에 이상한 식이 나타남 |

동적 평가기는 이미 **실행 중 타입 검사**를 하고 있다. 그래서 "타입 추론이 통과시켰는데 평가기가 `Type error`를 냈다"가 곧 추론기 버그의 증거다. 평가기가 정답지 역할을 한다.

## Why This Matters

level 기반 generalization에서 level을 낮추는 두 줄을 빼면 이런 식이 통과한다(`implement-hindley-milner-with-levels.md`).

```
(fun f -> let g = fun y -> f y in (g 1, g true)) (fun n -> n + 1)
  추론: 'a * 'b                 ← 통과
  실행: Type error: expected int or string but got bool
```

이런 버그는 `let id x = x in (id 1, id true)` 같은 쉬운 테스트로는 드러나지 않는다. 예제가 많을수록 걸릴 확률이 높아진다.

이 프로젝트에서 처음 돌렸을 때의 결과다.

- 평가기 예제 267개 중 263개가 타입 검사를 통과했고, 모두 실행 중 오류 없이 추론한 타입의 값이 되었다.
- 거부된 4개는 모두 정말 타입이 맞지 않는 식이었다(아래 Example).
- 처음에 "300개 이상 통과"라고 추측해서 적은 기준은 틀렸다. 실제 숫자를 보고 기준을 고쳤고, 거부 목록을 테스트에 고정했다.

## Recognition Pattern

- 동적 언어에 타입 검사를 나중에 붙일 때
- 타입 추론기를 리팩터링하거나 기능(polymorphism, 오버로딩, ADT)을 추가할 때
- 추론 테스트가 "예상 타입 문자열 비교"뿐이라 경우가 부족하다고 느낄 때

## The Approach

### Step 1: 평가기 예제 목록을 다른 테스트에서 쓸 수 있게 둔다

```fsharp
module EvalTests

let cases =
    [ "1 + 2", VInt 3
      "let id x = x in (id 1, id true)", ...
      ... ]
```

테스트 프로젝트의 컴파일 순서에서 `InferTests.fs`를 `EvalTests.fs` 뒤에 두고 `EvalTests.cases`를 참조한다.

### Step 2: 값이 타입과 맞는지 확인하는 함수를 만든다

```fsharp
/// 실행 결과 값이 추론한 타입의 모양과 맞는지 (타입 변수는 무엇이든 허용)
let rec valueMatches value t =
    match value, prune t with
    | _, TyVar _ -> true
    | VInt _, TyCon ("int", [])
    | VBool _, TyCon ("bool", [])
    | VString _, TyCon ("string", []) -> true
    | VList items, TyCon ("list", [ e ]) -> items |> List.forall (fun v -> valueMatches v e)
    | VData ("option", _, payload), TyCon ("option", [ e ]) -> payload |> Option.forall (fun v -> valueMatches v e)
    | VData (typeName, _, _), TyCon (name, _) -> name.StartsWith(typeName + "@")
    | VTuple vs, TyTuple ts -> List.length vs = List.length ts && List.forall2 valueMatches vs ts
    | (VClosure _ | VRecClosure _ | VBuiltin _ | VConstructor _), TyFun _ -> true
    | _ -> false
```

함수 값은 타입을 확인할 수 없으므로 "함수다"까지만 본다. 그래도 `int`라고 추론했는데 리스트가 나오는 버그는 잡힌다.

### Step 3: soundness 테스트

```fsharp
test "건전성: 타입이 맞는 식은 실행 중 타입 오류 없이 그 타입의 값이 됨" {
    let mutable checkedCount = 0
    for input, _ in EvalTests.cases do
        let ast = Program.parse input
        match (try Some (typecheck ast) with TypeError _ -> None) with
        | None -> ()
        | Some t ->
            checkedCount <- checkedCount + 1
            let value =
                try eval ast
                with ex -> failtestf "%s: 타입 %s 인데 실행 오류 %s" input (typeToString t) ex.Message
            Expect.isTrue (valueMatches value t) (sprintf "%s: 값 %s 이 타입 %s 와 맞지 않음" input (formatValue value) (typeToString t))
    // 대부분의 예제가 타입 검사를 통과해야 의미 있는 테스트
    Expect.isGreaterThan checkedCount 250 "타입 검사를 통과한 예제 수"
}
```

- **통과 개수에 lower bound를 둔다.** 추론기가 모든 것을 거부하면 이 테스트는 아무것도 확인하지 않고 성공한다.
- 평가기 예제 목록에는 **정상 결과가 나오는 식**만 넣는다. `1 / 0`처럼 타입과 무관한 실행 오류가 나는 예제는 오류 테스트 쪽에 둔다. 그래야 "실행 오류 = 추론기 버그"가 성립한다.

### Step 4: 거부 목록을 고정하고 하나씩 확인한다

```fsharp
test "동적 평가 예제 중 타입 검사가 거부하는 것은 정말 타입이 맞지 않는 것뿐" {
    let rejected =
        EvalTests.cases
        |> List.map fst
        |> List.filter (fun input ->
            try
                typecheck (Program.parse input) |> ignore
                false
            with TypeError _ -> true)
    Expect.equal rejected [ ... ] ""
}
```

목록이 바뀌면 테스트가 실패한다. 새로 거부된 식이 정말 타입이 맞지 않는지(평가기는 허용하던 동적 동작인지), 아니면 추론기가 너무 엄격해진 것인지 그때 판단한다.

## Example

이 프로젝트의 거부 목록이다. 모두 동적 평가에서만 의미가 있던 식이다.

```fsharp
[ "(1, 2) = (1, 2, 3)"                                                  // 원소 수가 다른 튜플
  "match \"1\" with 1 -> \"int\" | _ -> \"other\""                      // string 에 int 패턴
  "match (1, 2) with (a, b, c) -> 0 | _ -> 1"                           // 원소 수가 다른 튜플 패턴
  "type t = A in let v = A in type u = A in match v with A -> 1 | _ -> 2" ] // 다른 타입의 생성자
```

예외도 문서에 적는다. 이 프로젝트에서 `=`는 OCaml처럼 `'a -> 'a -> bool`이다. 그래서 `(fun x -> x) = (fun x -> x)`는 타입 검사를 통과하고 실행 중에 `Type error`가 난다. 이것은 의도한 설계라서 평가기 예제 목록에 넣지 않는다. 넣으면 soundness 테스트가 실패한다.

## 체크리스트

- [ ] 평가기 예제 목록이 다른 테스트 파일에서 접근 가능하다
- [ ] 값-타입 일치 함수가 있고, type variable과 함수는 느슨하게 확인한다
- [ ] 통과한 예제는 실행 오류가 없고 값이 타입과 맞는지 확인한다
- [ ] 통과 개수에 lower bound가 있다 (실제 숫자를 보고 정함)
- [ ] 거부 목록이 테스트에 고정되어 있고, 각 항목이 왜 거부되는지 주석이 있다
- [ ] 의도적으로 soundness에서 빠지는 경우(함수 `=`)를 문서에 적었다

## 관련 문서

- `implement-hindley-milner-with-levels.md` - 이 테스트가 잡아야 하는 level 버그
- `handle-overloaded-operators-in-hindley-milner.md` - allowed list 변수와, generalization 여부에 따른 차이
- `write-fscheck-properties-for-parser.md` - 생성한 입력으로 넓게 확인하는 다른 방법
