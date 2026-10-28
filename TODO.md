# TODO

나중에 할 일. 끝나면 이 목록에서 지우고 CHANGELOG에 적습니다.

## prelude 함수의 호출 비용 줄이기

0.30.0에서 `map`, `fold`, `range` 같은 16개 함수를 Calc로 쓴 prelude(`src/Calc/prelude.calc`)로 옮긴 뒤, 긴 리스트를 다루는 프로그램이 느려졌습니다.

| 100만 개 리스트에 `range`, `map`, `filter`, `sum`, `exists` | 0.29.0 (내장 함수) | 0.30.0 (prelude) |
|---|---|---|
| 인터프리터 (Release) | 0.77초 | 5.0초 |
| 컴파일한 실행 파일 | 0.04초 | 0.23초 |

- **원인:** 원소마다 F# `List` 함수나 C 루프 대신 클로저를 부르기 때문입니다.
- **영향:** 테스트 시간(약 5초)과 작은 프로그램에는 영향이 없었습니다.
- **할 일:** 컴파일러 쪽 비용은 알려진 함수를 direct call로 부르는 최적화로 줄일 수 있습니다(`docs/compiler-design.md` 단계 9).
  - 지금은 `fold f acc xs` 한 번이 `calc_apply`를 인자 수만큼 거칩니다(커링된 클로저를 하나씩 적용).
  - `let rec fold f acc xs = …`처럼 이름이 정해진 함수를 인자를 모두 받아 부르면, 클로저를 거치지 않고 함수를 바로 부를 수 있습니다. 같은 측정 예제로 효과를 재서 위 표에 더합니다.
- 측정 방법은 `howto/52-write-standard-library-as-prelude-in-the-language.md`의 Step 6에 있습니다.
