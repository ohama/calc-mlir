---
order: 53
created: 2026-10-28
commit: 83c6071
version: 0.30.0
description: F# 컴파일러는 Release에서만 .tail 호출을 낸다. 그래서 F#으로 쓴 인터프리터의 tail recursion이 Debug에서는 깊이 100만에 stack overflow로 죽고, Release에서는 0.5초에 끝난다. 테스트는 보통 Debug로 돌므로 <Tailcalls>true</Tailcalls>로 두 구성을 맞춘다
---

# F# 인터프리터는 Debug에서도 tail call을 켠다

> 관련 커밋: [83c6071](https://github.com/ohama/calc-mlir/commit/83c6071) (v0.30.0)

같은 Calc 프로그램을 같은 인터프리터로 돌렸는데, 빌드 구성에 따라 결과가 달랐다.

```
let rec go n acc = if n = 0 then acc else go (n - 1) (acc + 1) in go 1000000 0
```

| 빌드 | 결과 | 시간 |
|---|---|---|
| Debug | `Stack overflow.` (프로세스가 죽음) | 11.7초 |
| Release | `Result: 1000000` | 0.53초 |
| Debug + `<Tailcalls>true</Tailcalls>` | `Result: 1000000` | 0.78초 |

평가기는 큰 스택(512MB) 스레드에서 돈다(`09-handle-deep-recursion-stack-overflow.md`). 그래도 Debug에서는 깊이 100만을 넘기지 못했다.

## The Insight

**인터프리터의 tail call은 호스트 언어의 tail call에 기대고 있다.** Calc의 `go (n - 1) (acc + 1)`은 평가기 안에서 이렇게 이어진다.

```fsharp
| App (f, arg) ->
    let fv = evalIn env f
    let av = evalIn env arg
    apply fv av                      // tail position
...
and apply fv av =
    match fv with
    | VRecClosure (name, param, body, closureEnv) -> evalIn env body    // tail position
```

`evalIn → apply → evalIn`이 모두 tail position이다. F# 컴파일러가 여기에 `.tail` 접두사를 붙이면 .NET이 스택 프레임을 재사용한다. 그러면 Calc의 tail recursion이 스택을 쓰지 않는다.

그런데 F#은 **Release에서만** 기본으로 `.tail`을 낸다(`--tailcalls+`). Debug는 디버거로 호출 스택을 보기 쉽도록 꺼져 있다(`--tailcalls-`). 그래서 같은 평가기 코드가 Debug에서는 Calc 호출 하나마다 .NET 프레임 여러 개를 쌓는다.

## Why This Matters

- **테스트는 보통 Debug로 돈다.** `dotnet test`의 기본 구성이 Debug다. 반면 사용자는 Release로 받는다. 테스트와 실제 실행의 한계가 달라진다.
- **언어로 쓴 라이브러리가 tail recursion에 기댄다.** `map`, `range`, `fold`를 언어 자신으로 쓰면(`52-write-standard-library-as-prelude-in-the-language.md`), 긴 리스트를 다루는 모든 프로그램이 tail call에 기대게 된다.
- **StackOverflow는 잡을 수 없다.** .NET에서 stack overflow는 `try … with`로 잡히지 않고 프로세스가 죽는다. 테스트 하나가 테스트 프로세스 전체를 죽인다.
- **느리다.** 죽기 전까지 11초가 걸렸다. 깊은 스택을 쓰는 만큼 메모리를 건드리기 때문이다.

## Recognition Pattern

- F#(또는 .NET)으로 쓴 인터프리터, 평가기, 상태 기계에서 깊은 재귀가 Debug에서만 죽을 때
- `dotnet run -c Release`는 되는데 `dotnet test`는 프로세스가 죽을 때
- 해석하는 언어가 tail call을 보장해야 할 때(Scheme, ML 계열)

## The Approach

### Step 1: 두 구성을 같은 입력으로 잰다

```bash
dotnet build src/Calc                 # Debug
dotnet build src/Calc -c Release
for cfg in Debug Release; do
  /usr/bin/time -p dotnet src/Calc/bin/$cfg/net10.0/Calc.dll \
    -e "let rec go n acc = if n = 0 then acc else go (n - 1) (acc + 1) in go 1000000 0"
done
```

Release만 통과하면 tail call 차이다.

### Step 2: 프로젝트에서 tail call을 켠다

```xml
<PropertyGroup>
  <!-- 평가기의 tail call(evalIn → apply → evalIn)이 스택을 쓰지 않도록 Debug 에서도 .tail 호출을 냄 -->
  <Tailcalls>true</Tailcalls>
</PropertyGroup>
```

구성 조건 없이 넣으면 두 구성이 같아진다. 평가기가 있는 프로젝트에만 넣으면 된다. 테스트 프로젝트가 아니라, 평가기 코드를 컴파일하는 프로젝트다.

### Step 3: tail position을 유지한다

`.tail`은 정말 tail position에 있는 호출에만 붙는다. 결과를 감싸면 tail call이 아니다.

```fsharp
| Or (a, b) -> VBool (evalBool env a || evalBool env b)   // 오른쪽 호출 뒤에 VBool 로 감쌈 → tail call 아님
| If (c, t, e) -> if evalBool env c then evalIn env t else evalIn env e   // 두 분기 모두 tail call
```

그래서 해석하는 언어 쪽에서도 `f x || g y` 대신 `if f x then true else g y`로 써야 깊은 재귀가 안전하다. 라이브러리를 쓸 때 이 점을 지킨다.

### Step 4: 테스트로 고정한다

```fsharp
test "긴 리스트도 스택이 자라지 않음 (인터프리터의 tail call)" {
    Expect.equal (EvalTests.calc "length (range 1 1000000)") (Eval.VInt 1000000) ""
}
```

이 테스트는 Debug에서 돈다. 누가 `<Tailcalls>`를 지우면 프로세스가 죽어서 바로 드러난다.

## Example

```xml
<!-- ❌ 기본값: Debug 는 --tailcalls- -->
<PropertyGroup>
  <OutputType>Exe</OutputType>
</PropertyGroup>

<!-- ✅ 두 구성에서 같은 한계 -->
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <Tailcalls>true</Tailcalls>
</PropertyGroup>
```

## 체크리스트

- [ ] 깊은 tail recursion을 Debug와 Release에서 모두 재 봤는가?
- [ ] 평가기 프로젝트에 `<Tailcalls>true</Tailcalls>`가 있는가?
- [ ] 평가기의 `evalIn → apply → evalIn` 호출이 tail position에 있는가? (결과를 감싸지 않는가?)
- [ ] 깊이 100만 이상의 tail recursion 테스트가 Debug에서 도는가?

## 관련 문서

- `09-handle-deep-recursion-stack-overflow.md` - 큰 스택 스레드에서 평가하기
- `52-write-standard-library-as-prelude-in-the-language.md` - tail recursion에 기대는 prelude
- `31-link-large-native-stack-for-deep-recursion.md` - 컴파일한 실행 파일의 스택
