---
order: 44
created: 2026-10-28
commit: 64365f6
version: 0.25.0
description: 인터프리터의 print를 병렬 테스트마다 다른 TextWriter로 보내려면 전역 변수 대신 AsyncLocal을 쓴다. AsyncLocal 값은 설정한 흐름과 그 자손에만 보이므로, 모듈 초기화에서 넣은 기본값에 기대지 말고 null을 기본값으로 읽는다
---

# 병렬 테스트마다 인터프리터 출력을 따로 받기 (AsyncLocal)

> 관련 커밋: [64365f6](https://github.com/ohama/calc-mlir/commit/64365f6) (v0.25.0)

언어에 `print`가 생기면 평가기가 표준 출력에 쓴다. 테스트는 그 출력을 확인해야 하고, 차등 테스트는 인터프리터의 출력과 컴파일한 실행 파일의 출력을 비교해야 한다. 그런데 Expecto는 테스트를 **병렬로** 돌린다. 출력 대상을 전역 변수로 바꾸면 테스트끼리 출력이 섞인다.

```fsharp
// ❌ 전역 변수: 동시에 도는 두 테스트가 서로의 writer 를 덮어씀
let mutable output : TextWriter = Console.Out
```

## The Insight

**`AsyncLocal<T>`는 "실행 흐름"마다 값을 가진다.** 한 흐름에서 설정한 값은 그 흐름과, 그 흐름 안에서 새로 시작한 작업(Task, Thread)에만 보인다. 형제 흐름이나 부모 흐름에는 보이지 않는다(직접 확인함).

```
A (설정한 흐름):            "set in A"
C (A 안에서 시작한 작업):   "set in A"
B (A 와 따로 시작한 흐름):  <null>
main (A 를 시작한 쪽):      <null>
```

이 성질 덕분에 테스트마다 출력 대상을 정할 수 있다. 같은 성질 때문에 **기본값을 모듈 초기화에서 넣으면 안 된다.** 모듈 초기화는 그 모듈을 처음 건드린 흐름에서 한 번 일어나므로, 거기서 넣은 기본값은 그 흐름의 자손에게만 보인다. 다른 테스트 흐름에서 읽으면 `null`이다.

```fsharp
// ❌ 모듈 초기화에서 기본값: 다른 흐름에서는 null
let Output =
    let output = AsyncLocal<TextWriter>()
    output.Value <- Console.Out
    output

// ✅ null 을 "설정하지 않음"으로 보고 읽을 때 기본값
let private outputSlot = AsyncLocal<TextWriter>()
let private output () =
    match outputSlot.Value with
    | null -> Console.Out
    | writer -> writer
```

## Why This Matters

- **전역 변수를 쓴 경우:** 병렬 테스트의 출력이 섞입니다. 실패는 가끔만 나서 재현하기 어렵습니다.
- **`ThreadLocal`을 쓴 경우:** 평가를 큰 스택의 새 `Thread`에서 하면(`runWithLargeStack`) 값이 넘어가지 않습니다. `AsyncLocal`은 새 `Thread`에도 실행 컨텍스트와 함께 넘어갑니다(직접 확인함: 설정한 뒤 시작한 `Thread`가 같은 값을 봄).
- **기본값을 초기화에서 넣은 경우:** 어떤 흐름이 모듈을 먼저 건드렸는지에 따라, 다른 흐름의 `print`가 `null` writer에 쓰려다 `NullReferenceException`으로 죽을 수 있습니다. 이 프로젝트에서는 처음 코드를 쓸 때 이렇게 짰다가, 테스트하기 전에 바꿨습니다.

## Recognition Pattern

- 평가기나 런타임의 "현재 출력", "현재 설정"을 테스트마다 바꿔야 할 때
- 테스트 프레임워크가 병렬로 돌 때 (Expecto, xUnit 기본)
- 값을 함수 인자로 모두 넘기기에는 호출 경로가 너무 깊을 때

## The Approach

### Step 1: 슬롯은 비공개, 읽을 때 기본값

위 코드처럼 `AsyncLocal`은 숨기고, 읽는 함수가 `null`을 기본값으로 바꾼다.

### Step 2: 설정은 "이 호출 안에서만"으로 감싼다

```fsharp
/// print, println 의 출력을 writer 로 보내면서 평가
let evalWithOutput (writer: TextWriter) expr =
    let previous = outputSlot.Value
    outputSlot.Value <- writer
    try
        eval expr
    finally
        outputSlot.Value <- previous
```

`finally`에서 되돌려야 같은 흐름에서 이어서 도는 코드가 영향을 받지 않는다.

### Step 3: 테스트는 `StringWriter`로 받는다

```fsharp
let private interpret (input: string) =
    use output = new StringWriter()
    try
        match Eval.evalWithOutput output (Program.parse input) with
        | Eval.VUnit -> output.ToString(), "", 0
        | value -> output.ToString() + Eval.formatValue value + "\n", "", 0
    with ex ->
        output.ToString(), "Error: " + ex.Message + "\n", 1     // 오류 전의 출력도 남김
```

컴파일한 실행 파일의 (표준 출력, 표준 에러, 종료 코드)와 같은 모양이라 그대로 비교할 수 있다.

### Step 4: 병렬로 돌려서 확인한다

같은 흐름에서 두 작업이 각자 값을 설정해도 서로 보이지 않는다(직접 확인함: 두 작업이 각자 `"A"`, `"B"`를 읽음). 출력을 확인하는 테스트를 여러 개 두고 병렬로 돌려서, 출력이 섞이지 않는지 본다.

## Example

```fsharp
test "print 와 println: 문자열은 따옴표 없이" {
    use writer = new StringWriter()
    let value = evalWithOutput writer (Program.parse "print \"a\"; println 1")
    Expect.equal (writer.ToString(), value) ("a1\n", VUnit) ""
}
```

## 체크리스트

- [ ] 출력 대상이 전역 변수가 아니라 흐름별(`AsyncLocal`)인가?
- [ ] 기본값을 모듈 초기화가 아니라 읽을 때 정하는가? (`null` → 기본값)
- [ ] 설정을 `try ... finally`로 감싸 되돌리는가?
- [ ] 평가를 새 `Thread`에서 해도 값이 넘어가는가? (`ThreadLocal`이 아님)
- [ ] 오류가 나도 그 전의 출력을 비교에 넣는가?

## 관련 문서

- `09-handle-deep-recursion-stack-overflow.md` - 평가를 큰 스택의 새 `Thread`에서 하는 이유
- `26-write-differential-tests-for-compiler.md` - 출력을 비교하는 차등 테스트
