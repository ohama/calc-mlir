---
order: 65
created: 2026-10-29
commit: c2a1453
version: 0.39.0
description: 코드를 새 문법으로 옮길 때는 옛 파일을 옛 파서로, 새 파일을 새 파서로 파싱해서 AST가 같은지 본다. 새 문법의 코드는 옛 파서에서도 오류 없이 다른 뜻으로 파싱될 수 있으므로, 옮긴 파일은 두 파서를 비교하는 회귀 테스트에서 빼야 한다
---

# 새 문법으로 옮긴 코드를 AST 비교로 확인하기

> 관련 커밋: [c2a1453](https://github.com/ohama/calc-mlir/commit/c2a1453) (v0.39.0)

0.38.0에서 indentation 문법(`64-add-fsharp-light-syntax-with-token-filter.md`)을 넣은 뒤, 0.39.0에서 표준 라이브러리(`prelude.calc`)와 예제를 그 문법으로 다시 썼다.

```
// 전                                              // 후
let fst p = match p with (a, _) -> a               let fst p =
                                                       match p with
                                                       | (a, _) -> a

let range (a: int) (b: int) : int list =           let range (a: int) (b: int) : int list =
    let rec go i acc = … in                            let rec go i acc = …
    go b []                                            go b []
```

옮기는 일 자체는 쉽다. 어려운 것은 "뜻이 하나도 바뀌지 않았다"를 보이는 것이다. 이 문서는 그 확인 방법과, 옮긴 뒤 회귀 테스트를 고쳐야 하는 이유를 다룬다.

## The Insight

**뜻이 같다는 것은 AST가 같다는 것이다.** 옛 파일을 **옛 파서**로, 새 파일을 **새 파서**로 파싱해서 두 AST를 비교한다. 같으면 타입 추론, 평가, 컴파일 결과가 모두 같다. 실행 결과를 비교하는 것보다 강하다. 실행 결과는 테스트한 입력에서만 같다는 뜻이기 때문이다.

**새 문법의 코드는 옛 파서에서 오류 없이 다른 뜻이 될 수 있다.** 옮긴 파일 세 개를 옛 파서(verbose syntax)로 파싱해 봤다.

```
prelude.calc: verbose error: parse error: unexpected 'let' (line 39, column 5)
hello.calc:   verbose parses to a different AST
tree.calc:    verbose parses to a different AST
```

예제 두 개는 오류 없이 파싱되지만 AST가 다르다. 예를 들어 `| Rect (w, h) -> w * h` 다음 줄의 `println (greet "Calc")`가 옛 파서에서는 앞 분기에 이어지는 함수 호출이 된다. 그래서 "옛 파서와 새 파서가 같은 AST를 내는지"를 보는 회귀 테스트(`LayoutTests`)에서는 옮긴 파일을 빼야 한다.

## Why This Matters

- **실행 결과만 비교하면:** 테스트한 입력에서만 같다. prelude의 `nth`를 옮기다 `in`을 하나 남기거나 들여쓰기를 한 칸 틀려도, 범위 밖 인덱스를 시험하지 않으면 출력은 같다.
- **옮긴 파일을 회귀 테스트에 남기면:** 두 가지 일이 생긴다. prelude는 옛 파서에서 오류가 나서 테스트가 실패한다. 예제는 테스트가 다른 AST를 보고 실패한다. 실패 원인을 "새 문법에 버그가 있다"로 읽기 쉽다.
- **반대로 회귀 테스트를 그대로 둘 방법은 없다.** 그 테스트는 "옛 문법으로 쓴 코드는 새 파서에서도 뜻이 같다"는 약속이다. 새 문법으로 쓴 파일은 약속의 대상이 아니다.

## Recognition Pattern

- 언어에 새 문법을 넣고 표준 라이브러리, 예제, 문서를 그 문법으로 옮길 때
- 코드 formatter나 자동 변환 도구를 만들고 결과를 검증할 때
- "두 파서가 같은 AST를 낸다"를 테스트하는 회귀 테스트의 입력 목록을 관리할 때

## The Approach

### Step 1: 옮기기 전에 옛 파일을 따로 둔다

```bash
mkdir -p scratch/old && cp src/Calc/prelude.calc examples/*.calc scratch/old/
```

### Step 2: 옛 파서와 새 파서로 각각 파싱해서 비교한다

파서 두 개가 같은 프로그램 안에서 불릴 수 있어야 한다. 여기서는 `Frontend.parse`(indentation 문법)와 `Frontend.parseVerbose`(filter 없음)다.

```fsharp
// cmp.fsx
#r "src/Calc/bin/Release/net10.0/FsLexYacc.Runtime.dll"
#r "src/Calc/bin/Release/net10.0/Calc.dll"
let a = Frontend.parseVerbose (System.IO.File.ReadAllText fsi.CommandLineArgs.[1])   // 옛 파일, 옛 파서
let b = Frontend.parse (System.IO.File.ReadAllText fsi.CommandLineArgs.[2])          // 새 파일, 새 파서
printfn "%s" (if a = b then "same AST" else "DIFFERENT")
```

```
$ dotnet fsi cmp.fsx scratch/old/prelude.calc src/Calc/prelude.calc
same AST
```

- **AST가 같아야 하는 경우:** prelude처럼 줄바꿈과 들여쓰기만 바꾼 파일은 AST가 정확히 같아야 한다. F# DU의 structural equality로 비교한다.
- **위치는 비교하지 않는다:** 이 언어는 위치를 AST 밖에 기록하므로(`41-track-source-positions-without-changing-ast.md`) 줄이 바뀌어도 AST 비교에 영향이 없다.
- **prelude는 빌드에 포함된다:** `EmbeddedResource`라서 비교 전에 다시 빌드해야 새 파일이 들어간다. 다시 빌드하지 않으면 옛 prelude로 테스트하게 된다.

### Step 3: 구조를 바꾼 파일은 실행 결과로 비교한다

예제는 `let () = println …`을 0열의 `println …`으로 바꾸는 등 항목의 모양을 바꿨다. 이런 파일은 AST가 달라지므로 출력을 비교한다.

```bash
diff <(calc scratch/old/hello.calc) <(calc examples/hello.calc) && echo same output
```

### Step 4: 회귀 테스트의 입력 목록에서 옮긴 파일을 뺀다

```fsharp
// prelude 와 examples 는 0.39.0 부터 indentation 문법으로 씀 (prelude 는 verbose 로 쓴 예전 것과 AST 가 같음을 확인했음)
test "기존 입력은 indentation 문법을 켜도 AST 가 같음 (EvalTests, InferTests)" {
    let inputs = (EvalTests.cases |> List.map fst) @ (InferTests.inferred |> List.map fst)
    ...
```

뺀 이유를 주석에 남긴다. 옮긴 파일은 다른 테스트가 계속 실행한다. prelude는 모든 프로그램 앞에 붙고, 예제는 컴파일러 differential testing의 입력이다.

### Step 5: 문서의 예제는 실행해서 확인한다

문서에 넣을 예제를 파일로 만들어 실행하고, 주석에 적은 결과(`(* 55 *)`)와 맞춘다. 문서의 예제는 테스트가 보지 않으므로 옮기다 틀려도 아무도 모른다.

## Example

| 파일 | 확인 방법 | 결과 |
|---|---|---|
| `prelude.calc` (줄바꿈, 들여쓰기만) | 옛 파일/옛 파서 vs 새 파일/새 파서의 AST | 같음 |
| `hello.calc`, `tree.calc` (항목 모양을 바꿈) | 실행 출력 | 같음 |
| 문서의 예제 4개 | 실행 출력 vs 주석 | 같음 |
| 옮긴 파일을 옛 파서로 | (확인용) | prelude는 오류, 예제는 다른 AST |

## 체크리스트

- [ ] 옮기기 전 파일을 따로 두었는가?
- [ ] 줄바꿈만 바꾼 파일은 "옛 파일/옛 파서 = 새 파일/새 파서" AST 비교를 했는가?
- [ ] 리소스로 빌드에 들어가는 파일(prelude)은 다시 빌드한 뒤 비교했는가?
- [ ] 구조를 바꾼 파일은 실행 결과를 비교했는가?
- [ ] 두 파서를 비교하는 회귀 테스트에서 옮긴 파일을 빼고 이유를 남겼는가?
- [ ] 문서의 예제를 실행해서 주석의 결과와 맞췄는가?

## 관련 문서

- `64-add-fsharp-light-syntax-with-token-filter.md` - indentation 문법과 두 파서의 AST 비교 테스트
- `52-write-standard-library-as-prelude-in-the-language.md` - prelude를 빌드에 넣는 방법
- `26-write-differential-tests-for-compiler.md` - 예제를 인터프리터와 컴파일러로 비교
