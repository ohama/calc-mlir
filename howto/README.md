# Howto Documents

작업하면서 해결한 문제와 그 과정에서 배운 것을 기록합니다. 문서마다 독립적으로 읽을 수 있습니다.

| # | 문서 | 설명 | 작성일 |
|---|------|------|--------|
| 1 | [handle-fsyacc-nonassoc-conflict](handle-fsyacc-nonassoc-conflict.md) | fsyacc의 %nonassoc는 연쇄 비교를 막지 못한다. 충돌을 보고하고 조용히 오른쪽 결합으로 파싱한다 | 2026-10-27 |
| 2 | [control-let-if-scope-with-precedence](control-let-if-scope-with-precedence.md) | 규칙의 우선순위는 마지막 terminal을 따른다. IN과 ELSE를 가장 낮게 두어 본문이 오른쪽 끝까지 이어지게 한다 | 2026-10-27 |
| 3 | [order-fslex-keywords-before-identifiers](order-fslex-keywords-before-identifiers.md) | fslex는 가장 긴 일치를 먼저 보고, 길이가 같으면 앞의 규칙을 고른다. 키워드를 식별자보다 앞에 둔다 | 2026-10-27 |
| 4 | [setup-expecto-with-dotnet-test](setup-expecto-with-dotnet-test.md) | Expecto를 dotnet test로 돌리려면 GenerateProgramFile=false와 YoloDev 어댑터가 필요하다. 어댑터가 없으면 0개 실행으로 성공한다 | 2026-10-27 |
| 5 | [read-version-from-fsproj](read-version-from-fsproj.md) | fsproj <Version> 하나로 버전을 관리한다. +git 해시 접미사와 GetEntryAssembly 함정에 주의한다 | 2026-10-27 |
| 6 | [write-fscheck-properties-for-parser](write-fscheck-properties-for-parser.md) | 문자열을 거치는 속성 테스트는 렉서가 표현할 수 있는 값만 생성한다. 기본 정수 범위가 작으니 경계값은 따로 테스트한다 | 2026-10-27 |
| 7 | [parse-function-application-in-fsyacc](parse-function-application-in-fsyacc.md) | 연산자 없이 붙여 쓰는 함수 호출(f x y)은 우선순위 선언으로 표현할 수 없다. AppExpr/Atom 문법 계층으로 표현한다 | 2026-10-27 |
| 8 | [implement-closures-and-let-rec](implement-closures-and-let-rec.md) | 함수 값은 정의 시점의 환경을 저장한 클로저다. let rec은 호출할 때 자기 자신을 환경에 넣는다 | 2026-10-27 |
| 9 | [handle-deep-recursion-stack-overflow](handle-deep-recursion-stack-overflow.md) | 인터프리터의 재귀는 .NET 스택을 쓰고 StackOverflow는 잡을 수 없다. 큰 스택의 스레드에서 평가한다 | 2026-10-27 |
| 10 | [handle-nested-match-ambiguity-in-fsyacc](handle-nested-match-ambiguity-in-fsyacc.md) | 분기 수가 자유로운 match는 중첩될 때 dangling else와 같은 충돌을 만든다. 분기 수를 고정하거나 %prec로 shift를 명시한다 | 2026-10-28 |
| 11 | [lex-string-literals-with-fslex](lex-string-literals-with-fslex.md) | 문자열 리터럴은 두 번째 렉서 규칙(and readString sb = parse)으로 한 글자씩 모은다. 이스케이프 규칙 순서와 eof 처리가 핵심이다 | 2026-10-28 |
| 12 | [escape-quotes-and-braces-in-fslex-actions](escape-quotes-and-braces-in-fslex-actions.md) | fslex/fsyacc는 액션 코드를 자체 규칙으로 훑는다. 문자 리터럴 '"'와 '}'는 '\"'와 '\u007D'로 쓴다 | 2026-10-28 |
| 13 | [implement-builtins-as-data-values](implement-builtins-as-data-values.md) | 내장 함수를 F# 함수로 Value에 넣으면 equality가 깨진다. 이름과 모은 인자만 저장하는 데이터로 표현한다 | 2026-10-28 |
| 14 | [use-ordinal-string-apis-for-language-builtins](use-ordinal-string-apis-for-language-builtins.md) | .NET 문자열 API 일부는 기본이 문화권 비교다. 언어 내장 함수에는 Ordinal과 Invariant를 명시한다 | 2026-10-28 |
| 15 | [guard-fsharp-core-semantics-in-builtins](guard-fsharp-core-semantics-in-builtins.md) | 내장 함수를 FSharp.Core 함수로 바로 구현하면 오버플로 검사, 예외 감싸기, 오류 메시지가 언어로 새어 나온다. 경계를 조사해 막는다 | 2026-10-28 |
| 16 | [report-parse-error-positions-with-fsyacc](report-parse-error-positions-with-fsyacc.md) | fsyacc의 기본 오류는 위치 없는 "parse error"다. parse_error_rich로 구분되는 예외를 던지고, 위치는 lexbuf의 StartPos/EndPos에서 읽는다 | 2026-10-28 |
| 17 | [implement-pattern-matching-evaluator](implement-pattern-matching-evaluator.md) | 패턴 매칭 평가는 matchPattern: Pattern -> Value -> Env -> Env option 하나로 끝난다. 분기 선택, 가드, let 분해가 모두 이 함수를 쓴다 | 2026-10-28 |
| 18 | [distinguish-constructors-from-variables-in-patterns](distinguish-constructors-from-variables-in-patterns.md) | 패턴의 이름은 생성자일 수도 변수일 수도 있다. 대소문자로 구분하고 모르는 생성자는 오류로 만들어 오타가 catch-all이 되는 것을 막는다 | 2026-10-28 |
| 19 | [represent-adt-values-in-dynamic-interpreter](represent-adt-values-in-dynamic-interpreter.md) | ADT 값은 (타입 이름, 생성자, 인자) 데이터로, 생성자는 환경에 든 보통 값으로 표현한다. 생성자를 함수처럼 넘길 수 있다 | 2026-10-28 |
| 20 | [implement-hindley-milner-with-levels](implement-hindley-milner-with-levels.md) | HM의 let 일반화를 단계(level)로 한다. 변수를 이을 때 단계를 낮추지 않으면 잘못된 프로그램이 통과한다 | 2026-10-28 |
| 21 | [handle-overloaded-operators-in-hindley-milner](handle-overloaded-operators-in-hindley-milner.md) | int와 string 모두에 쓰는 +는 허용 목록 타입 변수로 표현한다. 일반화하지 않으면 F# 기본, 하면 F# inline과 같은 결과 | 2026-10-28 |
| 22 | [write-soundness-test-for-type-checker](write-soundness-test-for-type-checker.md) | 평가기 예제를 타입 추론에 넣어, 통과한 식은 실행 중 오류 없이 그 타입의 값이 되는지 확인하고 거부 목록을 고정한다 | 2026-10-28 |
| 23 | [emit-mlir-as-llvm-func-and-lower](emit-mlir-as-llvm-func-and-lower.md) | 함수 포인터가 필요한 언어는 MLIR 함수를 처음부터 llvm.func로 만든다. 본문의 scf/cf/arith는 정해진 순서의 패스로 llvm까지 내린다 | 2026-10-28 |
| 24 | [represent-values-as-tagged-words](represent-values-as-tagged-words.md) | 모든 값을 i64 하나로 두고 하위 3비트로 종류를 표시한다. GC 포인터는 8바이트 정렬이고, 복원은 산술 시프트다 | 2026-10-28 |
| 25 | [run-external-toolchain-from-dotnet](run-external-toolchain-from-dotnet.md) | 외부 컴파일러는 ArgumentList로 실행하고 stderr를 비동기로 읽는다(동기로 차례로 읽으면 멈춤). 도구가 없으면 테스트를 건너뛴다 | 2026-10-28 |
| 26 | [write-differential-tests-for-compiler](write-differential-tests-for-compiler.md) | 컴파일러의 정답은 인터프리터다. 컴파일할 수 있는 평가기 예제와 오류 예제를 자동으로 골라 병렬로 비교하면 기능과 함께 테스트가 늘어난다 | 2026-10-28 |
| 27 | [match-interpreter-integer-semantics-in-native-code](match-interpreter-integer-semantics-in-native-code.md) | .NET 정수 연산의 넘침과 나눗셈 오류는 LLVM/C에서 정의되지 않은 동작이다. arm64는 1/0을 조용히 0으로 낸다. 정의된 연산을 쓰거나 앞에서 검사한다 | 2026-10-28 |
| 28 | [emit-short-circuit-with-scf-if-regions](emit-short-circuit-with-scf-if-regions.md) | if, &&, ||는 값을 내는 scf.if로 만든다. 분기 코드를 영역 안에서 생성해야 단락 평가가 된다 | 2026-10-28 |
| 29 | [compile-immutable-let-to-ssa-environment](compile-immutable-let-to-ssa-environment.md) | 바꿀 수 없는 let은 코드를 만들지 않는다. 이름 → SSA 이름 Map으로 섀도잉과 범위가 해결되고, 쓰이지 않는 값은 LLVM이 순수한 것만 지운다 | 2026-10-28 |
| 30 | [implement-closure-conversion-to-top-level-functions](implement-closure-conversion-to-top-level-functions.md) | 캡처 목록은 이미 낮춘 본문의 자유 변수에서 얻는다. 함수 입구에서 캡처한 값을 환경에 넣으면 본문은 let처럼 생성되고, let rec은 클로저 자신이 된다 | 2026-10-28 |
| 31 | [link-large-native-stack-for-deep-recursion](link-large-native-stack-for-deep-recursion.md) | 기본 8MB 스택에서는 깊이 100만 재귀가 죽는다. -Wl,-stack_size로 LC_MAIN의 스택 크기를 키우고, 한계는 프레임 크기로 계산한다 | 2026-10-28 |
| 32 | [measure-fresh-binaries-on-macos](measure-fresh-binaries-on-macos.md) | macOS에서 새로 만든 실행 파일은 첫 실행에만 약 0.3초가 더 걸린다. 성능은 두 번째 실행부터 user 시간으로 잰다 | 2026-10-28 |
| 33 | [emit-string-constants-as-aligned-llvm-globals](emit-string-constants-as-aligned-llvm-globals.md) | 상수의 주소를 값으로 쓰려면 전역을 8바이트로 정렬한다. 바이트 배열 전역은 주소 하위 비트가 0이 아니다. 헤더로 시작하는 struct 전역을 초기화 영역으로 만든다 | 2026-10-28 |
| 34 | [store-strings-as-utf16-for-dotnet-compatibility](store-strings-as-utf16-for-dotnet-compatibility.md) | .NET과 같은 결과를 내려면 문자열을 UTF-16으로 저장한다(ordinal 순서가 UTF-8과 다름). 출력은 구간째 UTF-8로, 짝 없는 대리는 U+FFFD | 2026-10-28 |
| 35 | [compile-match-with-scf-if-conditions](compile-match-with-scf-if-conditions.md) | 가드가 있는 match를 scf.if로 그대로 펼치면 나머지 분기가 두 곳에 생겨 지수로 커진다. 조건을 i1로 먼저 계산하면 선형 크기가 된다 | 2026-10-28 |
| 36 | [replicate-reference-equality-order-in-runtime](replicate-reference-equality-order-in-runtime.md) | 비교할 수 없는 값이 섞이면 검사 순서에 따라 false 또는 오류가 된다. 인터프리터처럼 길이를 먼저, 처음 false에서 멈추게 옮긴다 | 2026-10-28 |

추천 읽는 순서: 3 → 11 → 12 (렉서, 문자열 리터럴, 액션 코드 함정), 2 → 1 → 7 → 10 → 16 (파서 우선순위 → 우선순위 함정 → 함수 호출 → 중첩 match → 오류 위치), 17 → 18 → 19 (패턴 매칭, 생성자, ADT), 20 → 21 → 22 (타입 추론), 8 → 9 → 13 → 14 → 15 (함수 평가, 재귀 한계, 내장 함수), 23 → 24 → 25 → 26 → 27 → 28 → 29 → 30 → 31 → 32 → 33 → 34 → 35 → 36 (네이티브 컴파일러), 4 → 6 (테스트), 5 (릴리스)

---
총 36개 | 업데이트: 2026-10-28
