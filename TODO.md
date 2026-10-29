# TODO

나중에 할 일. 끝나면 이 목록에서 지우고 CHANGELOG에 적습니다.

## 인터프리터의 prelude 호출 비용

0.30.0에서 `map`, `fold` 같은 함수를 Calc로 쓴 prelude로 옮긴 뒤, 100만 개 리스트에 `range`, `map`, `filter`, `sum`, `exists`를 쓰는 프로그램이 인터프리터(Release)에서 0.77초에서 5.0초가 되었습니다. 컴파일러 쪽은 0.34.0의 direct call로 0.24초에서 0.12초가 되었지만, 인터프리터는 그대로입니다.

- **원인:** 원소마다 커링된 클로저를 하나씩 적용합니다(`VClosure` 생성과 `Map` 환경 복사).
- **할 일:** 인터프리터에도 인자를 모두 받는 호출을 두거나, 환경을 배열로 바꾸는 방법을 같은 측정 예제로 재 봅니다. 측정 방법은 `howto/52-write-standard-library-as-prelude-in-the-language.md`의 Step 6에 있습니다.
