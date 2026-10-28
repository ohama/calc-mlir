---
created: 2026-10-28
description: 인터프리터가 이미 있다면 컴파일러의 정답은 인터프리터다. 평가기 예제 중 컴파일할 수 있는 것을 자동으로 골라 두 결과를 비교하면, 컴파일러가 기능을 늘릴수록 테스트도 저절로 늘어난다
---

# 인터프리터를 정답으로 쓰는 컴파일러 differential testing

인터프리터(`Eval`)가 있는 언어에 네이티브 컴파일러를 붙일 때는 기대값을 손으로 적지 않는다. **같은 프로그램을 인터프리터로 평가한 결과**와 **컴파일한 실행 파일의 출력**을 비교한다. 입력은 이미 있는 평가기 테스트 예제다. 그중 컴파일러가 지금 지원하는 것만 골라 쓴다.

## The Insight

**컴파일러를 단계적으로 만들 때, 지원 범위는 예외로 판정하게 한다.** 컴파일러는 모르는 식을 만나면 `NotSupported` 예외를 던진다. 테스트는 각 예제를 먼저 컴파일해 보고, 예외가 나지 않은 것만 실행해서 비교한다.

```fsharp
let private compilable (input: string) =
    try
        let ast = Program.parse input
        Infer.typecheck ast |> ignore          // 타입 검사를 통과한 것만 컴파일 대상
        Codegen.emitProgram ast |> ignore
        true
    with
    | Codegen.NotSupported _
    | Infer.TypeError _ -> false
```

그러면 단계 1에서 `Add`를 지원하는 순간 `1 + 2`를 쓰는 예제 수십 개가 **테스트 코드를 고치지 않아도** 비교 대상에 들어온다. 테스트 수는 컴파일러의 진척도가 된다.

## Why This Matters

- **기대값을 손으로 적으면:** 컴파일러 테스트와 인터프리터 테스트가 같은 내용을 두 번 적게 된다. 언어 의미를 바꿀 때 한쪽만 고치기 쉽다.
- **실행 결과 형식이 다르면:** 인터프리터와 컴파일러가 "같은 언어"라는 보장이 없다. 출력 형식(`Eval.formatValue`)과 오류 형식(`Error: ...`, 종료 코드 1)까지 비교 대상으로 삼는다.
- **"지원하는 것만 고르는" 필터가 조용히 비면:** 버그가 있어도 테스트는 통과한다. 비교한 예제 수의 lower bound를 확인해야 한다.

## Recognition Pattern

- 같은 언어의 구현이 둘 있을 때(인터프리터와 컴파일러, 최적화 전과 후, 옛 버전과 새 버전)
- 새 구현이 기능을 단계적으로 늘려 갈 때
- 이미 예제가 많은 테스트 스위트가 있을 때

## The Approach

### Step 1: 지원하지 않는 기능은 이름이 있는 예외로

```fsharp
exception NotSupported of feature: string with
    override this.Message = "Not supported by the compiler yet: " + this.feature

| other -> raise (NotSupported (caseName other))   // caseName: 리플렉션으로 "Add", "Lambda" 등
```

사용자에게도 "아직 지원하지 않음"이 버그와 구분되어 보인다.

### Step 2: 평가기 예제를 공개해서 재사용

평가기 테스트의 `(입력, 기대값)` 목록을 `EvalTests.cases`로 공개한다. 기대값은 쓰지 않고 입력만 가져온다. 정답은 그 자리에서 `Eval.eval`로 계산한다.

### Step 3: 출력 전체(stdout, stderr, 종료 코드)를 비교

처음(단계 0) 모양이다. Step 4와 5에서 오류 예제와 병렬 컴파일을 더한다.

```fsharp
test "차등: 컴파일할 수 있는 평가 예제는 인터프리터와 같은 결과" {
    requireTools () |> ignore
    let inputs = EvalTests.cases |> List.map fst |> List.filter compilable
    for input in inputs do
        let expected = Eval.formatValue (Eval.eval (Program.parse input)) + "\n"
        Expect.equal (compileAndRun input) (expected, "", 0) input
    // 단계가 올라갈수록 늘어남
    Expect.isGreaterThanOrEqual inputs.Length 2 "컴파일한 예제 수"
}
```

- **메시지:** `Expect.equal`의 메시지에 `input`을 넣는다. 실패하면 어떤 프로그램인지 바로 보인다.
- **lower bound:** 마지막 줄의 lower bound는 단계를 올릴 때마다 높인다. 필터가 실수로 모든 예제를 걸러 내도 테스트가 실패하게 하는 장치다.

### Step 4: 실행 오류도 같은 형식으로 비교한다

`EvalTests.cases`에는 성공하는 예제만 있다. 실행 중 오류가 나는 예제는 목록을 따로 만들어 더한다. 정답 함수는 인터프리터가 예외를 던지면 `calc`가 출력하는 것과 같은 결과를 돌려준다.

```fsharp
let private interpret (input: string) =
    try
        Eval.formatValue (Eval.eval (Program.parse input)) + "\n", "", 0
    with ex ->
        "", "Error: " + ex.Message + "\n", 1

let private runtimeErrorCases =
    [ "1 / 0"; "10 / (5 - 5)"; "(-2147483647 - 1) / -1"; "true && 1 / 0 = 0" ]
```

인터프리터는 `1 / 0`에서 `Error: Attempted to divide by zero.`를 낸다. 컴파일한 실행 파일도 이 문장을 stderr에 쓰고 종료 코드 1로 끝나야 한다. 런타임의 `calc_error`가 `fflush(stdout)` 뒤에 `Error: %s\n`을 쓰고 `exit(1)`하는 것은 이 비교를 위해서다.

### Step 5: 예제는 실행 파일 하나로 묶어 빌드한다

처음에는 예제마다 실행 파일을 만들고 병렬로 컴파일했다(`Array.Parallel.map compileAndRun`). 예제가 200개를 넘자 테스트가 1분 30초 걸렸다. 병목은 컴파일이 아니라 macOS의 새 실행 파일 첫 실행 검사였고, 이 검사는 순서대로 처리돼서 병렬화로는 줄지 않았다.

지금은 모든 입력을 실행 파일 하나로 묶어 빌드하고, 그 안의 드라이버가 입력마다 `fork`해서 실행한다(약 5초). 방법은 `batch-compiled-test-programs-into-one-executable.md`에 있다. 입력마다 프로세스가 따로이므로 stdout, stderr, 종료 코드를 비교하는 이 문서의 방식은 그대로다.

## Example

필터 결과는 단계마다 이렇게 늘었다. 테스트 코드는 lower bound 숫자만 바꿨다.

```
EvalTests.cases 수백 개 (+ 오류 예제)
  → 타입 검사 통과 + NotSupported 없음
  → 단계 0 (리터럴):            2개  ["true"; "false"]
  → 단계 1 (정수, 불리언, if): 43개  (하한을 60으로 짐작했다가 틀려서, 실제 값 43으로 고침)
```

lower bound는 짐작하지 말고 한 번 돌려서 나온 값으로 정한다. 결과가 다르면 실패 메시지에 입력 식이 나온다. 예를 들어 곱셈에서 태그를 풀지 않고 곱했다면(`17 * 25 = 425`, 하위 비트가 1이라 정수 `53`으로 읽힘) 이렇게 보인다(설명용 예).

```
차등: ... 2 * 3
expected: ("6\n", "", 0)
  actual: ("53\n", "", 0)
```

## 체크리스트

- [ ] 지원하지 않는 기능을 이름이 있는 예외로 알리는가?
- [ ] 필터가 타입 오류 예제도 걸러 내는가?
- [ ] 기대값을 인터프리터가 그 자리에서 계산하는가? (손으로 적지 않음)
- [ ] stdout, stderr, 종료 코드를 모두 비교하는가?
- [ ] 비교한 예제 수의 lower bound를 단계마다 올리는가? (짐작하지 말고 실제 값으로)
- [ ] 실행 중 오류 예제를 따로 더했는가?
- [ ] 예제 수가 늘어도 새 실행 파일 수가 늘지 않는가? (묶음 빌드)

## 관련 문서

- `write-soundness-test-for-type-checker.md` - 같은 예제를 타입 추론에 재사용하기
- `run-external-toolchain-from-dotnet.md` - `compileAndRun`과 도구가 없을 때 건너뛰기
- `match-interpreter-integer-semantics-in-native-code.md` - 오류 예제가 잡아낸 정수 경계 문제
