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

추천 읽는 순서: 3 → 11 → 12 (렉서, 문자열 리터럴, 액션 코드 함정), 2 → 1 → 7 → 10 (파서 우선순위 → 우선순위 함정 → 함수 호출 → 중첩 match), 8 → 9 → 13 → 14 (함수 평가, 재귀 한계, 내장 함수), 4 → 6 (테스트), 5 (릴리스)

---
총 14개 | 업데이트: 2026-10-28
