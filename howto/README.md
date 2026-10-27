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

추천 읽는 순서: 3 → 2 → 1 (렉서 → 파서 우선순위 → 우선순위 함정), 4 → 6 (테스트), 5 (릴리스)

---
총 6개 | 업데이트: 2026-10-27
