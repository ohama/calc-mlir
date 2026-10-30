# Changelog

이 파일은 버전별 변경 사항을 기록합니다. 형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/)를,
버전 번호는 [Semantic Versioning](https://semver.org/lang/ko/)을 따릅니다.
버전은 `src/Calc/Calc.fsproj`의 `<Version>`에서 관리합니다.

## [0.63.0] - 2026-10-30

### 변경
- 컴파일러: 튜플 인자 펴기. 본문이 인자를 바로 `match p with (a, b) -> …`하는 이름 붙은 함수는 direct 함수가 튜플 대신 원소를 따로 받습니다.
  - 튜플 리터럴을 넘기는 호출(`go (i - 1) (b, a + b)`)은 튜플 블록을 만들지 않습니다. 다른 식을 넘기면 계산한 튜플에서 원소를 꺼내 부릅니다.
  - 인자 여럿을 차례로 `match`해도 폅니다. 분기가 둘 이상이거나, 가드가 있거나, 원소 패턴이 겹친 튜플이거나, 원소 이름이 다른 인자와 겹치면 펴지 않습니다.
  - 클로저로 부르는 쪽은 그대로 튜플을 받습니다. 편 함수의 클로저는 인자 수를 알리지 않아서, 모르는 함수로 인자 여럿을 주는 호출은 하나씩 적용합니다.
  - `go` 5천만 번이 0.27초에서 0.20초, 튜플 인자 둘인 `dist` 3천만 번이 0.28초에서 0.11초가 되었습니다.
  - 인터프리터는 바뀌지 않았습니다.
- 테스트 2개 추가 (총 1227개): 생성한 MLIR의 direct 함수 인자와 튜플 할당이 없는 재귀 호출, 컴파일 테스트에 튜플 인자 경우 14가지(원소 계산 순서, 튜플이 아닌 인자, 본문이 튜플 자체를 씀, 상호 재귀, 펴지 않는 모양, 값으로 쓴 함수)

## [0.62.0] - 2026-10-30

### 변경
- 컴파일러: 타입 추론이 string으로 정한 `+` 연쇄(`a + b + c`)와 `sprintf`, `printf`, `printfn`, `$"..."`를 펼친 식이 중간 문자열 없이 한 번에 할당합니다.
  - `Infer.stringAdds`가 string으로 정해진 `+` 노드를 내고, `Lower`가 연쇄를 펴서 조각 목록(`PConcat`)으로 만듭니다. 조각은 전처럼 왼쪽부터 계산합니다.
  - 런타임의 `calc_concat2`~`calc_concat8`이 길이를 더해 한 번 할당합니다. 조각이 8개보다 많으면 8개씩 나눠 잇습니다.
  - 일곱 조각 연결 2백만 번이 0.17초에서 0.08초, `sprintf "%d-%d:%s"` 2백만 번이 0.11초에서 0.06초가 되었습니다.
  - 인터프리터는 바뀌지 않았습니다.
- 테스트 2개 추가 (총 1225개): 생성한 MLIR의 `calc_concatN`(연쇄, 포맷, 8개가 넘는 조각, 타입 검사를 하지 않은 식), 컴파일 테스트에 문자열 연결 경우 10가지(괄호, 계산 순서, 18조각, BMP 밖의 글자, 포맷, 예외, 반복)

## [0.61.0] - 2026-10-30

### 변경
- 런타임과 prelude: 자주 쓰는 경우의 빠른 경로. 결과는 바뀌지 않습니다.
  - `toString`, `print`, `println`이 int를 `snprintf`와 버퍼 없이 바로 10진수로 씁니다. `toString i` 2백만 번이 0.09초에서 0.02초, `println i` 3백만 번이 0.25초에서 0.09초, `sprintf "%d-%d:%s"` 2백만 번이 0.28초에서 0.12초가 되었습니다.
  - `sort`가 원소가 모두 int이면 값을 그대로 비교하는 merge sort를 씁니다. int 2백만 개가 0.18초에서 0.12초가 되었습니다.
  - prelude의 `Array.init`, `map`, `copy`, `fold`가 리스트를 거치지 않고 반복문으로 채웁니다. 3백만 개의 `init`, `map`, `fold`가 실행 파일에서 0.14초(238MB)에서 0.01초(48MB), 인터프리터에서 1.39초에서 0.72초가 되었습니다.
- 테스트 1개 추가 (총 1223개): 컴파일 테스트에 int의 `toString`과 출력(Int32의 끝값), `sort`(빈 리스트, 같은 값, 10만 개), prelude `Array` 함수(빈 배열, `f`를 부르는 순서, `raise`, 수거 뒤) 10가지

## [0.60.0] - 2026-10-30

### 변경
- 컴파일러: 원소가 bool인 배열이 원소마다 바이트 하나입니다(`[헤더 9 | (n << 8), 바이트...]`).
  - 런타임의 `Array.create`, `Array.ofList`가 원소 값을 보고 만듭니다. 리터럴과 prelude의 `Array.init`, `map`, `copy`도 `Array.ofList`를 거칩니다.
  - 안에 포인터가 없어 `GC_MALLOC_ATOMIC`으로 할당하므로 GC가 내용을 훑지 않습니다.
  - 생성 코드의 `a.[i]`, `a.[i] <- v`는 헤더의 종류를 보고 바이트나 단어로 읽고 씁니다. 배열 헤더 읽기에는 따로 TBAA 태그를 붙입니다.
  - 체 1억이 0.65초에서 0.41초, 최대 메모리가 804MB에서 103MB가 되었습니다. int 배열은 그대로입니다.
  - 인터프리터는 바뀌지 않았습니다.
- 테스트 2개 추가 (총 1222개): 생성한 MLIR의 바이트 경로, 컴파일 테스트에 bool 배열 경우 8가지(읽기와 쓰기, 리터럴과 `=`, 빈 배열, prelude의 `Array` 함수, 범위 밖 읽기와 쓰기, bool이 아닌 원소, 수거 뒤)

## [0.59.0] - 2026-10-29

### 변경
- 컴파일러: 생성자 블록에 이름 칸이 없습니다. `[헤더, 이름 포인터, 인자...]`가 `[헤더, 인자...]`가 되었습니다.
  - **생성자 번호:** 이름 전역의 주소에서 `calc_name_none`의 주소를 뺀 값을 헤더의 위쪽 32비트에 넣습니다. 런타임은 번호로 이름을 찾아 출력하고, 같은 생성자인지는 번호로 비교합니다.
  - **인자 없는 생성자:** `None`, `Leaf` 같은 생성자는 전역 블록 대신 즉시값 `(번호 << 32) | 6`입니다. 패턴 검사가 메모리를 읽지 않습니다.
  - **크기:** `Node (l, v, r)`은 48바이트에서 32바이트, `Some x`는 32바이트에서 16바이트가 되었습니다(Boehm 객체 크기).
  - **효과:** bst 2백만 개가 1.83초에서 1.53초, 최대 메모리가 168MB에서 113MB가 되었습니다. option을 많이 만드는 프로그램은 -9%입니다.
  - 인터프리터는 바뀌지 않았습니다.
- 테스트 추가 (총 1220개): 생성자 표현 테스트를 새 표현으로 고침. 컴파일 테스트에 생성자 경우 9가지(즉시값과 블록의 `=`, 실행 중에 편 튜플 인자, 출력의 괄호, 겹친 패턴, 예외, 수거가 여러 번 일어나는 트리, 받지 않은 예외 세 가지)

## [0.58.0] - 2026-10-29

### 변경
- 컴파일러: 모르는 함수에 인자 여럿을 주는 호출(`fold`의 `f acc x`)이 중간 클로저를 만들지 않습니다.
  - 인자가 여럿인 함수(`let f a b`, `fun a b -> …`)의 클로저는 마지막 칸에 direct 함수 포인터를 두고, 헤더에 인자 수를 적습니다.
  - 호출하는 쪽은 헤더의 인자 수가 준 인자 수와 같으면 direct 함수를 부르고, 아니면 하나씩 적용합니다. 계산 순서는 같습니다.
  - 직접 쓴 `myfold f (f acc x) r`(3천만 개)가 0.44초에서 0.21초, 최대 메모리가 1.25GB에서 0.52GB가 되었습니다. prelude `fold`를 쓴 프로그램은 0.44초에서 0.38초입니다.
  - 인터프리터는 바뀌지 않았습니다.
- 테스트 2개 추가 (총 1219개): 생성한 MLIR의 클로저 헤더와 direct 호출, 컴파일 테스트에 경우 10가지(인자 계산 순서, 인자 하나짜리 함수, 인자를 많이·적게 줌, 캡처, 이름에 묶은 함수, `let rec` 함수, tail position, 내장 함수, 고른 함수)

## [0.57.0] - 2026-10-29

### 변경
- 컴파일러가 `while`과 `for`를 지금 함수 안의 반복(`scf.while`)으로 만듭니다. 전에는 파서가 바꾼 `$while`, `$for` 재귀 함수를 클로저로 만들었습니다.
  - `Lower`가 파서의 모양을 알아보고 Core의 `CWhile`, `CFor`로 바꿉니다.
  - `for`는 끝값 앞일 때만 `i ± 1`을 계산하므로 끝값이 int의 끝이어도 넘치지 않습니다.
  - 바깥 반복마다 안쪽 반복의 클로저를 만들던 비용이 없어져, 겹친 `for` 2만 × 2만이 0.17초에서 0.13초가 되었습니다. 다른 예제는 같습니다.
  - 인터프리터는 바뀌지 않았습니다.
- `docs/benchmarks.md`: `calc_alloc`에 `noalias`를 붙여 ref를 레지스터로 올린 실험. `while`과 ref는 12% 빨라졌지만 체와 merge sort는 느려져 넣지 않았습니다.
- 테스트 2개 추가 (총 1217개): 생성한 MLIR의 `scf.while`, 컴파일 테스트에 반복문의 경우 8가지(빈 범위, int 끝, start와 stop의 계산 순서, 몸체의 클로저, 겹친 반복, 조건의 부수 효과, 반복 안의 raise, 긴 반복)

## [0.56.0] - 2026-10-29

### 변경
- 컴파일한 코드의 int `+`, `-`가 태그를 떼지 않습니다.
  - `(8x+1) ± (8y+1) ∓ 1`을 64비트에서 계산한 뒤 35비트에서 부호를 넓힙니다(arm64에서 `sbfx` 하나). 32비트에서 한 바퀴 도는 결과는 같습니다.
  - N-Queens 14가 2.64초에서 2.50초, 에라토스테네스의 체 1억이 0.73초에서 0.66초, `benchmarks/sieve.calc`가 0.013초에서 0.011초가 되었습니다.
- 컴파일 테스트에 `+`, `-`의 한 바퀴 경계 경우 2가지 (총 1215개)

## [0.55.0] - 2026-10-29

### 변경
- 컴파일러가 LLVM에 alias 정보를 줍니다.
  - 클로저가 붙잡은 값, ref의 내용, 배열 원소에 접근할 때 서로 다른 TBAA 태그를 붙입니다.
  - 런타임의 오류 함수(`calc_error_index`, `calc_error_divide_by_zero`, `calc_error_overflow`, `calc_match_failure`)는 `noreturn`으로 선언합니다.
  - LLVM이 ref나 배열에 쓰는 반복 안에서 클로저 칸 읽기를 반복 밖으로 뺍니다. 에라토스테네스의 체 1억이 0.85초에서 0.72초, `benchmarks/sieve.calc`가 0.017초에서 0.013초가 되었습니다.
  - 결과는 같습니다.
- 테스트 1개 추가 (총 1215개): 생성한 MLIR의 TBAA 태그와 `noreturn`

## [0.54.0] - 2026-10-29

### 변경
- 컴파일러가 타입 추론 결과를 씁니다. 피연산자가 int로 정해진 `+`, `=`, `<>`, `<`, `>`, `<=`, `>=`는 태그 검사와 런타임 호출 없이 계산합니다.
  - fib 41이 0.44초에서 0.33초(같은 C 코드는 0.28초), N-Queens 13이 0.53초에서 0.41초가 되었습니다.
  - 타입 추론이 연산 노드마다 피연산자 타입을 기록하고, 추론이 끝난 뒤 int로 정해진 노드를 AST 루트에 붙여 둡니다(`Infer.intOperations`). `Lower`가 그 노드를 int 전용 연산(`PIntAdd`, `PIntLess` 등)으로 바꿉니다.
  - 다형 함수 안의 `=`, int인지 string인지 정해지지 않은 연산, 타입 검사 없이 Codegen만 부른 식은 지금처럼 태그를 봅니다.
  - 결과는 같습니다. 인터프리터는 바뀌지 않았습니다.
- 테스트 2개 추가 (총 1214개): 생성한 MLIR에 런타임 호출이 있는지 없는지, 컴파일 테스트에 int, 다형 `=`, string과 블록 비교의 경우 4가지

## [0.53.0] - 2026-10-29

### 변경
- 컴파일한 코드의 리스트 칸이 헤더 없는 `[head, tail]` 16바이트 블록입니다. 값은 블록 주소에 태그 `2`를 붙인 것입니다.
  - 전에는 `[헤더, head, tail]` 24바이트라 Boehm GC에서 32바이트를 썼습니다.
  - `calc_init`이 interior pointer를 끄고(`GC_set_all_interior_pointers(0)`) 주소 + 2를 `GC_register_displacement(2)`로 알립니다.
  - merge sort 20만(`benchmarks/msort.calc`)이 0.064초에서 0.049초(Python 0.21초), 최대 메모리가 72MB에서 38MB가 되었습니다.
  - `=`의 빠른 경로는 "태그가 홀수(int, bool, `[]`, `()`)면 비트를 비교"로 바뀌었습니다. 결과는 같습니다.
  - 인터프리터는 바뀌지 않았습니다.
- 테스트 1개 추가 (총 1212개, 컴파일 테스트에 리스트 칸의 경우 11가지: 중첩, `=`, ref와 배열, 런타임의 리스트 함수, 예외, 클로저, 여러 번의 수거를 거치는 긴 리스트)

## [0.52.0] - 2026-10-29

### 변경
- 인터프리터도 `append (reverse xs) ys`를 한 번에 처리합니다(`Eval.revAppend`). 거꾸로 만든 리스트를 다시 복사하지 않아 리스트 칸을 n개만 만듭니다. 컴파일러의 `calc_rev_append`와 같습니다.
  - 인터프리터의 merge sort 20만(`benchmarks/msort.calc`)이 0.665초에서 0.611초가 되었습니다.
  - `append`와 `reverse`가 사용자 정의로 가려지지 않았을 때만 씁니다. 계산 순서와 오류(`reverse`의 인자를 먼저 확인)는 같습니다.
- 테스트 2개 추가 (총 1211개)

## [0.51.0] - 2026-10-29

### 변경
- 컴파일한 merge sort 20만(`benchmarks/msort.calc`)이 0.097초에서 0.066초(Python 0.21초)가 되었습니다.
  - `match (a, b) with (p, q) -> …`처럼 분기가 모두 원소 수가 같은 튜플 패턴이나 `_`면, 튜플 블록을 만들지 않고 원소마다 패턴을 맞춥니다. 맞는 분기가 없을 때만 튜플을 만들어 `Match failure`를 냅니다.
  - `append (reverse xs) ys`는 런타임의 `calc_rev_append` 하나로 부릅니다. 리스트 칸을 2n개가 아니라 n개만 만듭니다.
  - 결과, 오류 메시지, 계산 순서는 같습니다. 인터프리터는 바뀌지 않았습니다.
- 테스트 1개 추가 (총 1209개, 컴파일 테스트에 튜플 match와 `rev_append`의 경우 9가지)

## [0.50.0] - 2026-10-29

### 변경
- 컴파일한 실행 파일이 더 빨라졌습니다(`benchmarks/run.py`). BST 20만은 0.131초에서 0.064초(Python 0.35초), 에라토스테네스의 체 3천만은 0.32초에서 0.27초가 되었습니다.
  - 생성자의 튜플 인자를 블록에 폅니다. `Node (l, v, r)`은 data 블록과 튜플 블록 두 개가 아니라 `[헤더, 이름, l, v, r]` 블록 하나입니다.
    - 단어 수 2는 튜플이 아닌 인자 하나, 3 이상은 편 원소들입니다.
    - 인자 전체를 묶는 패턴(`Node p`)은 런타임의 `calc_data_payload`가 튜플을 다시 만듭니다.
    - 실행 중에 튜플인 인자(`map Node xs`)는 `calc_make_data`가 폅니다.
    - 출력과 `=`는 모든 칸을 봅니다.
  - 배열의 `a.[i]`, `a.[i] <- v`는 생성 코드가 범위를 확인하고 바로 읽고 씁니다. 범위 밖이면 새 런타임 함수 `calc_error_index`가 인터프리터와 같은 문장으로 끝냅니다.
- 테스트 1개 추가 (총 1208개, 컴파일 테스트에 튜플 인자를 편 블록의 경우 13가지)

## [0.49.0] - 2026-10-29

### 변경
- 컴파일한 실행 파일이 빨라졌습니다(`benchmarks/run.py`). merge sort 20만은 0.17초에서 0.097초(Python 0.21초), BST 20만은 0.22초에서 0.13초(Python 0.35초), fib 41은 0.86초에서 0.42초가 되었습니다.
  - `+`, `<`, `>`, `<=`, `>=`는 생성 코드가 왼쪽 값의 태그를 보고 int면 그 자리에서 계산합니다. `=`는 즉시값(int, bool, `[]`, `()`)이면 비트를 비교합니다. string과 블록은 전처럼 런타임(`calc_add`, `calc_compare`, `calc_eq`)이 합니다.
  - 런타임의 `calc_alloc`은 8단어까지의 블록을 `GC_malloc_many`로 한꺼번에 받아 두고 나눠 줍니다. 런타임의 `calc_cons`도 이 경로를 씁니다.
  - `calc_init`이 `GC_set_free_space_divisor(1)`로 수거를 드물게 합니다. 할당이 많은 프로그램의 최대 메모리가 2배 안쪽으로 늘어납니다(merge sort 38MB → 72MB).
- 테스트 2개 추가 (총 1207개)

## [0.48.0] - 2026-10-29

### 변경
- `reverse`, `append`, `range`가 다시 내장 함수입니다(인터프리터는 F#, 컴파일러는 C 런타임). 함수 인자 없이 리스트를 만들기만 하는 함수라, Calc 코드로 원소마다 실행할 까닭이 없습니다.
  - merge sort 20만(`benchmarks/msort.calc`)이 인터프리터에서 1.00초에서 0.67초, 컴파일한 실행 파일에서 0.20초에서 0.17초(Python 0.21초)가 되었습니다.
  - 결과와 타입은 같습니다. `range`는 끝값이 `int`의 끝이어도 멈춥니다.
  - `--no-typecheck`로 잘못 쓰면 내장 함수의 오류가 납니다(`range 1 true`는 `Type error: expected int but got bool`).
  - prelude에서 `map`과 `filter`는 이제 다른 prelude 정의를 붙이지 않습니다.

### 추가
- 문서 `docs/build.md`의 "빠른 실행 파일 (ReadyToRun)": `dotnet publish -r <RID> -p:PublishReadyToRun=true`로 JIT을 미리 해 두면 빈 프로그램이 0.071초에서 0.035초, fib 32가 0.15초에서 0.11초가 됩니다. `benchmarks/run.py`는 `CALC=dist/calc`로 그 결과를 잽니다. `dist/`는 `.gitignore`에 넣었습니다.
- 테스트 4개 추가 (총 1205개)

## [0.47.0] - 2026-10-29

### 변경
- 인터프리터의 값 표현을 바꿔 GC가 줄었습니다(Release, `benchmarks/run.py`). BST 20만은 0.96초에서 0.76초, merge sort 20만은 1.04초에서 1.00초가 되었습니다.
  - 인자 없는 생성자(`None`, `Red`)는 `VData0 (타입, 생성자)`, 인자 있는 생성자는 `VData (타입, 생성자, 인자)`입니다. 전에는 인자를 `Value option`으로 들고 있어서 값마다 `Some`을 하나 더 만들었습니다.
  - 튜플은 `VTuple of Value[]`입니다(전에는 `Value list`). BST의 트리 노드 하나가 6객체 184바이트에서 3객체 112바이트가 되었습니다.
  - `match (a, b) with (p, q) -> …`처럼 분기가 모두 같은 길이의 튜플 패턴이나 `_`면 튜플을 만들지 않고 원소마다 패턴을 맞춥니다.
  - 결과, 오류 메시지, 계산 순서는 같습니다.
- 테스트 3개 추가 (총 1201개)

## [0.46.0] - 2026-10-29

### 변경
- 인터프리터가 빨라졌습니다(Release, `benchmarks/run.py`). merge sort 20만은 1.61초에서 1.04초, BST 20만은 1.32초에서 0.96초, N-Queens 10은 0.17초에서 0.13초, fib 32는 0.16초에서 0.145초(Python 0.136초)가 되었습니다.
  - sampling profiler로 재니 시간의 상당 부분이 .NET의 형 검사 helper(`IsInstanceOfClass`, `StelemRef`)였습니다.
  - `args[i] fr`를 `(args[i]) fr`로 씁니다. F#은 앞의 것을 배열 인덱스 함수 값과 `InvokeFast`로 컴파일해서 호출마다 형 검사를 합니다.
  - 패턴 클로저는 인자 둘을 한 번에 받는 형(`OptimizedClosures.FSharpFunc`)으로 바꿔 두고 `Invoke`로 부릅니다.
  - Frame을 struct 칸의 배열로 만들어, 칸에 쓸 때 `obj[]`의 형 검사가 없습니다.
  - 원소 둘, 셋인 튜플은 바로 만들고, `match xs with [] -> … | x :: rest -> …`(변수나 `_`만, 가드 없음)는 패턴 함수 없이 나눕니다.
  - 결과, 오류 메시지, 계산 순서, 재귀 깊이 한계는 같습니다.
- 테스트 3개 추가 (총 1198개)

## [0.45.0] - 2026-10-29

### 변경
- 인터프리터가 빨라졌습니다(Release, `benchmarks/run.py`). fib 32는 0.23초에서 0.16초, N-Queens 10은 0.22초에서 0.17초, 에라토스테네스의 체는 0.34초에서 0.29초(Python 0.30초), merge sort 20만은 1.96초에서 1.61초가 되었습니다.
  - `calc prog.calc`처럼 인자가 파일 하나뿐이면 Argu를 거치지 않고 바로 실행합니다. Argu의 초기화가 약 50ms라서, 빈 프로그램이 0.12초에서 0.067초가 되었습니다. 다른 인자는 지금처럼 Argu로 처리합니다.
  - 연산자의 피연산자가 숫자 상수나 지금 함수의 변수면 클로저를 부르지 않고 바로 읽습니다(`n - 1`, `i < n`, `x = 0`).
- prelude의 `reverse`, `append`, `map`, `filter`를 `fold`에 함수를 넘기지 않는 직접 재귀로 바꿨습니다. 결과와 `f`를 부르는 순서는 같습니다. 인터프리터의 merge sort 평가가 17% 빨라졌고, 컴파일한 merge sort는 0.29초에서 0.20초가 되었습니다(원소마다 `calc_apply`로 부르던 간접 호출이 없어짐). `map`과 `filter`는 이제 `fold`를 붙이지 않습니다.
- 테스트 9개 추가 (총 1195개)

## [0.44.0] - 2026-10-29

### 변경
- 인터프리터가 빨라졌습니다(Release, `benchmarks/run.py`). fib 32는 0.39초에서 0.23초, N-Queens 10은 0.37초에서 0.22초, merge sort 20만은 2.75초에서 1.96초, BST 20만은 1.85초에서 1.38초가 되었습니다.
  - 함수 호출 하나의 칸(`Frame`)을 객체 하나로 만듭니다. 호출마다 하던 할당이 두 번에서 한 번이 되었습니다.
  - 조건과 비교, 논리 연산은 중간 값 `VBool`을 만들지 않고 계산합니다. -256~1023의 정수와 빈 리스트는 미리 만들어 둔 값을 씁니다. 인자 하나인 호출도 바로 부릅니다.
  - .NET이 시작하고 100ms 동안 최적화 컴파일을 미루지 않게 했습니다(`Calc.fsproj`의 `CallCountingDelayMs` = 0).
  - 결과, 오류 메시지, 계산 순서, 재귀 깊이 한계는 같습니다.
- 테스트 9개 추가 (총 1186개)

## [0.43.0] - 2026-10-29

### 추가
- 성능 비교 `benchmarks/`: 예제 다섯 개(fib, queens, sieve, msort, bst)의 Calc 프로그램과 Python 프로그램, 측정 스크립트 `run.py`. 세 결과의 출력이 같은지 확인하고 시간을 표로 출력합니다.
- 문서 `docs/benchmarks.md`: 결과와 분석. 컴파일한 실행 파일은 호출, 반복, 배열 위주의 예제에서 Python보다 3~14배 빠르고, 할당이 많은 예제에서는 비슷합니다. 인터프리터는 Python보다 1.4~13배 느리고, 그 이유(시작 비용과 JIT, 내장 함수 호출 경로, 할당과 GC)를 측정해 적었습니다.

### 변경
- 인터프리터의 내장 함수 호출이 빨라졌습니다. 에라토스테네스의 체(200만, `benchmarks/sieve.calc`)가 1.59초에서 0.42초가 되었습니다(Release).
  - 인자가 다 모인 내장 함수 호출은 인자를 하나씩 모으지 않고, 모두 계산한 뒤 바로 부릅니다. `!`, `:=`, `a.[i]`, `a.[i] <- v`는 이름으로 고르지 않는 전용 코드로 실행합니다.
  - 비교와 논리 연산의 결과로 `VBool` 값을 새로 만들지 않고, 튜플 패턴에서 튜플을 배열로 복사하지 않습니다. msort는 2.96초에서 2.75초, bst는 2.09초에서 1.85초가 되었습니다.
  - 결과, 오류 메시지, 계산 순서는 같습니다.
- 테스트 7개 추가 (총 1177개)

## [0.42.0] - 2026-10-29

### 변경
- 인터프리터가 빨라졌습니다. 100만 개 리스트에 `range`, `map`, `filter`, `sum`, `exists`를 쓰는 프로그램이 5.3초에서 1.5초가 되었습니다(Release).
  - 식을 먼저 한 번 훑어서 변수마다 "몇 단계 바깥 함수의 몇 번째 칸"인지 정하고, F# 클로저로 바꾼 뒤 실행합니다. 환경을 `Map`으로 들고 다니면서 `let`과 호출마다 `Map.add`, 변수마다 문자열 비교를 하던 비용이 없어졌습니다(1.9초).
  - `fun a -> fun b -> …`를 인자 두 개의 함수 하나로 만들고, 인자가 다 모인 호출은 중간 함수 없이 바로 부릅니다(1.5초). 이것만 `Map` 환경에 넣으면 거의 빨라지지 않았습니다(5.3초 → 5.0초).
  - 결과, 오류 메시지, 계산 순서는 같습니다. tail call이 아닌 재귀의 깊이 한계가 100만에서 300만으로 늘었습니다.
- TODO의 "인터프리터의 prelude 호출 비용"을 지웠습니다.
- 테스트 4개 추가 (총 1170개)

## [0.41.0] - 2026-10-29

### 추가
- 배열(F#과 같은 문법). 인터프리터, 타입 추론, 컴파일러, REPL 모두 지원합니다.
  - `[| 1; 2 |]`, `[||]`, `a.[i]`, `a.[i] <- v`. 타입은 `'a array`이고, 출력은 `[|1; 2|]`입니다. `=`는 원소를 비교합니다.
  - 내장 함수 `Array.length`, `Array.create`, `Array.ofList`, `Array.toList`, prelude의 `module Array`에 `Array.init`, `Array.map`, `Array.iter`, `Array.fold`, `Array.copy`
  - 범위 밖은 `Index was outside the bounds of the array.`로 끝납니다(인터프리터와 컴파일러가 같은 문장).
  - 컴파일러는 배열을 블록 `[헤더, 원소...]`(종류 8)로 만듭니다.
  - 새 토큰 `[|`, `|]`, `<-`. `x<-1`은 이제 `x <- 1`입니다(`x < -1`로 띄어 씀).
- prelude에서도 모듈을 쓸 수 있습니다(파싱한 뒤 `Modules.resolve`로 펼침).
- 테스트 31개 추가 (총 1166개)

## [0.40.0] - 2026-10-29

### 추가
- 레코드 패턴 `{ x = p1; y = p2 }` (F#과 같음). 쓴 필드만 검사합니다. `match`, `function`, `try`, `let`의 왼쪽, 생성자의 인자에 씁니다. 인터프리터, 타입 추론, 컴파일러, REPL 모두 지원합니다.
  - 타입은 필드 이름이 정합니다(레코드 식과 같음). 오류: `Unknown record field: z`, `The record field y does not belong to type p`, `Duplicate field: x`
  - 빠진 분기 검사와 쓰이지 않는 분기 경고가 레코드를 생성자 하나인 타입으로 다룹니다. 빠진 값의 예는 `{ x = _; y = false }`처럼 보여 줍니다.
  - 컴파일러는 패턴을 선언한 순서의 모든 필드로 채워(`_`) 칸 번호로 읽습니다.
- 테스트 17개 추가 (총 1135개)

## [0.39.0] - 2026-10-29

### 변경
- prelude(`src/Calc/prelude.calc`)와 `examples/`를 indentation 문법으로 다시 썼습니다. prelude의 AST는 예전 verbose 버전과 같고, 예제의 출력도 같습니다.
- `docs/grammar.md`와 README의 예제를 indentation 문법으로 바꿨습니다(`;;`, `in`, `done`, `struct … end`를 쓰는 verbose syntax는 그대로 받습니다).
- `LayoutTests`의 AST 비교에서 prelude와 examples를 뺐습니다(이제 verbose syntax가 아님).

## [0.38.0] - 2026-10-29

### 추가
- indentation 문법(F# light syntax). 들여쓴 블록이 `in`, `;`, `;;`, `done`, `struct … end`를 대신합니다. verbose syntax(지금까지의 문법)도 그대로 받습니다.
  - 블록: `let … =`, `->`, `then`, `else`, `try`, `do` 다음의 식. 블록과 같은 열의 새 줄은 구분자(`in`, `;`), 0열의 새 줄은 새 최상위 항목, 왼쪽의 새 줄은 블록을 닫습니다.
  - 바깥 `match`의 열에 쓴 `|`는 안쪽 `match`를 닫습니다. 리스트와 레코드는 줄바꿈으로 원소를 구분합니다. `module M =` 다음 줄부터 들여쓴 모듈, `done` 없는 `while`/`for`를 씁니다.
  - 렉서와 파서 사이의 `LayoutFilter`가 F#의 `LexFilter`처럼 offside rule로 가상 토큰을 넣습니다. 문법에는 블록 괄호 규칙 하나만 더했습니다.
  - 오류: `Tabs are not allowed in indentation`, `The block following this let is unfinished`
  - 한 줄 코드는 뜻이 바뀌지 않습니다. 기존 예제 전부의 AST가 같은지 테스트로 확인합니다.
- 테스트 42개 추가 (총 1118개)

### 변경
- 선언 다음 줄의 0열에서 시작하는 식은 새 항목입니다(전에는 앞 선언의 식에 이어져서 `;;`가 필요했음). `while c do a`처럼 `done`을 생략할 수 있습니다.

## [0.37.0] - 2026-10-29

### 추가
- 모듈(OCaml과 같은 문법). 인터프리터, 타입 추론, 컴파일러, REPL 모두 지원합니다.
  - `module M = struct … end` (안에 `let`, `let rec`, `type`, 레코드 타입, `exception`, `module`, `open`), `M.f`, `M.C`(패턴 포함), 타입 `M.t`, 모듈 안의 모듈 `M.N.g`, `open M`
  - 파서 직후 `Modules.resolve`가 모듈을 `M.f` 같은 이름의 최상위 선언으로 펼칩니다. 타입 추론, 평가기, 컴파일러는 바뀌지 않았습니다.
  - 생성자와 타입은 전체 이름으로 출력합니다(`Stack.Empty`, `int Stack.t`). 레코드 필드 이름에는 모듈 이름을 붙이지 않습니다.
  - 새 키워드: `module`, `struct`, `end`, `open`. 새 토큰: `M.f` 같은 한정한 이름(`QIDENT`, `QCTOR`)
  - 시그니처, functor, `let module`은 아직 없습니다.
- 테스트 35개 추가 (총 1076개)

## [0.36.0] - 2026-10-29

### 추가
- 레코드(F#, OCaml과 같은 문법). 인터프리터, 타입 추론, 컴파일러, REPL 모두 지원합니다.
  - `type point = { x: int; y: int }` (type parameter와 재귀 타입 가능), `{ x = 1; y = 2 }`, `p.x`, `{ p with x = 1 }`
  - 타입은 필드 이름이 정합니다. 같은 필드 이름의 레코드 타입이 여럿이면 마지막에 선언한 타입입니다(OCaml과 같음).
  - 필드 값은 쓴 순서대로 계산하고, 결과는 선언한 순서로 `{ x = 1; y = 2 }`처럼 출력합니다. `=`는 필드를 비교합니다.
  - 오류: `Unknown record field: x`, `Missing record field: y`, `The record field y does not belong to type p`, `Duplicate field: x`
  - 컴파일러는 레코드를 블록 `[헤더, 필드 이름들, 값...]`(종류 7)으로 만들고, 필드는 번호로 읽습니다.
  - 레코드 패턴과 `mutable` 필드는 아직 없습니다.
- 테스트 40개 추가 (총 1041개)

## [0.35.0] - 2026-10-29

### 추가
- mutable reference와 반복문(OCaml, F#과 같은 문법). 인터프리터, 타입 추론, 컴파일러, REPL 모두 지원합니다.
  - `ref v : 'a ref`, `!r : 'a`, `r := v : unit`. `!`는 함수 호출보다 강하고, `:=`는 이항 연산자 가운데 가장 약하고 오른쪽 결합입니다(`r := !r + 1`). `=`는 내용을 비교하고, 결과는 `ref 1`, `ref (Some 1)`처럼 출력합니다.
  - prelude에 `incr`, `decr`를 더했습니다.
  - `while c do e done`, `for i = a to b do e done`, `downto`. 파서가 tail recursion으로 바꾸므로 반복 횟수와 상관없이 스택이 자라지 않습니다. `for`의 끝값은 한 번만 계산하고, 끝값이 `int`의 최댓값이어도 끝납니다.
  - 새 키워드: `while`, `for`, `to`, `downto`, `do`, `done`. 이 이름은 더 이상 변수로 쓸 수 없습니다.
  - 컴파일러는 `ref` 블록(종류 6)을 C 런타임 함수 없이 할당, 읽기, 쓰기로 만듭니다.

### 변경
- 타입 추론에 value restriction을 넣었습니다. `let`의 값이 함수 호출을 포함하면 generalize하지 않습니다(SML과 같은 규칙). `let r = ref [] in r := [1]; r := ["a"]`가 통과하지 않게 하기 위해서입니다. partial application의 결과(`let f = id id`)도 이제 monomorphic입니다.
- REPL이 식 입력이 정한 타입을 다음 입력에도 남기고, generalize하지 않은 type variable을 `'_a`로 보여 줍니다(`val r : '_a list ref = ref []`).
- 테스트 59개 추가 (총 1001개)

## [0.34.0] - 2026-10-29

### 변경
- 컴파일러가 알려진 함수를 direct call합니다. 이름에 묶은 함수(`let f a b = …`, `let rec`, `and`)마다 인자를 모두 받는 함수를 하나 더 만들고, 인자가 다 모인 호출(`f 1 2`, 본문의 재귀 호출)은 중간 클로저 없이 그 함수를 바로 부릅니다. partial application과 값으로 쓰는 경우는 지금처럼 클로저입니다.
  - 100만 개 리스트에 `range`, `map`, `filter`, `sum`, `exists`를 쓰는 프로그램(컴파일한 실행 파일): 0.24초 → 0.12초. 인터프리터는 바뀌지 않았습니다.
- 생성한 함수를 모두 LLVM `tailcc` calling convention으로 만들고 tail call을 `musttail` 대신 `tail`로 부릅니다. 인자 수가 다른 함수 사이의 tail call도 `-O0`에서 보장됩니다.
- 런타임이 클로저를 부르는 `calc_apply`를 C 런타임에서 생성한 모듈로 옮겼습니다(C는 `tailcc` 함수를 부를 수 없음).
- 테스트 6개 추가 (총 942개)

## [0.33.0] - 2026-10-29

### 추가
- 문자열 포맷(F#과 같은 문법). 인터프리터, 타입 추론, 컴파일러, REPL 모두 지원합니다.
  - `sprintf`, `printf`, `printfn`: 지정자 `%d`(int), `%s`(string), `%b`(bool), `%A`(아무 값, `toString`과 같은 모양), `%%`. 포맷 문자열 리터럴이 함수의 타입을 정합니다(`sprintf "%d is %s" : int -> string -> string`). 리터럴이 아니면 `The format string of sprintf must be a string literal`입니다.
  - 보간 문자열 `$"Hello {name}, {age + 1}"`: 구멍은 아무 식, `%d{e}`처럼 지정자를 붙이면 타입을 검사합니다. `{{`, `}}`는 중괄호 글자입니다. `$"a{x}"`는 `sprintf "a%A" x`와 같습니다.
  - 포맷 오류(`Unknown format specifier: %q`, `Missing format specifier after %`)는 포맷 문자열을, 인자 타입 오류는 인자를 가리킵니다. 보간 문자열 안의 오류도 원래 줄과 열을 가리킵니다.
  - 포맷의 함수는 `Format.expand`가 Calc 코드로 펼쳐서 인터프리터와 컴파일러가 같은 코드를 실행합니다. C 런타임은 바뀌지 않았습니다.
- 테스트 30개 추가 (총 936개)

### 변경
- `$"`가 보간 문자열의 시작이 되었습니다.

## [0.32.0] - 2026-10-28

### 추가
- 예외(OCaml과 같은 문법). 인터프리터, 타입 추론, 컴파일러, REPL 모두 지원합니다.
  - `exception E`, `exception E of t` 선언(최상위, `exception … in 식`). 새 타입 `exn`. 처음부터 있는 예외는 `Failure of string`, `Not_found`입니다.
  - `raise : exn -> 'a`(내장 함수), `failwith : string -> 'a`(prelude, `raise (Failure msg)`)
  - `try e with | p -> e | …`: 위에서부터 맞는 분기가 받고, 맞는 분기가 없으면 다시 raise합니다. 빠진 분기 경고는 하지 않고, 쓰이지 않는 분기 경고는 합니다.
  - 잡히지 않은 예외는 `Error: msg`(`Failure msg`) 또는 `Error: exception <값>`을 출력하고 종료 코드 1로 끝납니다.
  - 인터프리터는 .NET 예외(`Eval.CalcException`), 컴파일러는 C 런타임의 `calc_try`(`_setjmp`)와 `calc_raise`(`_longjmp`)를 씁니다. `try`의 본문과 handler는 클로저가 됩니다(`Core.CTry`).
  - 같은 이름의 예외는 프로그램에 하나만 선언할 수 있습니다(`Exception E is already defined`). 실행 중에는 예외를 이름으로 구분하므로, 인자 타입이 다른 두 예외가 섞이면 soundness가 깨지기 때문입니다.
  - 0으로 나누기, `Match failure` 같은 실행 중 오류는 예외가 아니라서 `try`로 받을 수 없습니다.
- 테스트 40개 추가, 3개 수정 (총 906개)

### 변경
- `head`, `tail`, `nth`를 내장 함수에서 prelude로 옮겼습니다. `failwith`로 실패하므로 메시지는 같고(`head: empty list`), 이제 `try … with Failure msg`로 받을 수 있습니다. 내장 함수는 18개(`raise` 포함), prelude 함수는 20개입니다.
- `try`, `exception`이 키워드가 되었습니다.

## [0.31.0] - 2026-10-28

### 추가
- 컴파일러의 tail call 보장: 함수 본문의 tail position(`if`, `match`, `let`, `;`의 마지막 식 포함)에 있는 호출을 LLVM `musttail`로 부르고 바로 `llvm.return`합니다. 최적화 수준과 상관없이 스택 프레임을 다시 써서, `let rec go n acc = … go (n - 1) (acc + 1)`, `let rec … and …`, 함수 인자로 받은 함수의 호출이 깊이에 상관없이 돕니다.
  - 지금까지는 `-O2`의 LLVM 최적화가 대부분을 tail call로 바꿨지만 보장은 아니었습니다. 같은 코드를 `-O0`으로 컴파일하면 깊이 1000만에서 stack overflow로 죽었습니다.
  - tail position의 `if`와 `match`는 `scf.if` 대신 블록과 `llvm.cond_br`로 만듭니다(`Codegen.emitTail`). tail position이 아닌 곳은 그대로 `scf.if`입니다.
  - 인터프리터는 0.30.0부터 .NET tail call로 같은 동작입니다.
- `Toolchain.Tools.OptimizationLevel`: 생성한 코드를 컴파일하는 clang의 최적화 수준(기본 `-O2`). 테스트가 `-O0`으로 tail call 보장을 확인합니다.
- 테스트 5개 추가 (총 866개)

## [0.30.0] - 2026-10-28

### 변경
- 표준 라이브러리를 Calc로 작성: `fst`, `snd`, `ignore`, `isEmpty`, `fold`, `reverse`, `append`, `range`, `sum`, `map`, `filter`, `exists`, `forall`, `tryHead`, `tryFind`, `defaultValue` 16개를 F#(`Eval.callBuiltin`)과 C(`builtins.c`) 구현에서 빼고 `src/Calc/prelude.calc`의 Calc 코드로 옮겼습니다. 인터프리터, 타입 추론, 컴파일러, REPL이 모두 이 정의 하나를 씁니다.
  - 프로그램을 파싱한 뒤 쓰는 prelude 정의만 `let … in` 사슬로 앞에 붙입니다(`Prelude.wrap`). REPL은 처음부터 모두 붙입니다. 같은 이름을 정의하면 그쪽이 가립니다.
  - 모두 tail recursion이라 긴 리스트에서도 스택이 자라지 않습니다. 타입과 결과, 함수를 부르는 순서는 예전과 같습니다.
  - `--no-typecheck`로 타입이 맞지 않게 쓰면 오류 메시지가 달라집니다. `fst 1`은 `Match failure: 1`입니다.
  - 긴 리스트에서 느려졌습니다. 100만 개 리스트 예제에서 인터프리터(Release)는 0.77초에서 5.0초, 컴파일한 실행 파일은 0.04초에서 0.23초입니다.
- 내장 함수는 20개(문자열 함수, `toString`, `toInt`, `head`, `tail`, `nth`, `length`, `sort`, `print`, `println`)입니다. 런타임의 `calc_some`도 뺐습니다.
- Debug 빌드에서도 F# tail call을 켰습니다(`<Tailcalls>true</Tailcalls>`). 인터프리터의 tail recursion이 Debug에서 깊이 100만에 stack overflow로 죽던 것이 Release와 같아졌습니다.
- 테스트 8개 추가, 6개 수정 (총 861개)

## [0.29.0] - 2026-10-28

### 추가
- 타입 표기(OCaml과 같은 문법). 타입 추론이 적은 타입과 추론한 타입을 unify합니다. 인터프리터와 컴파일러는 표기를 지우고 실행합니다.
  - 식 `(e : t)`(괄호가 필요함), 인자 `fun (x: int) (_: string) -> ...`, 반환 타입 `let f x : int = ...`, `let rec f (n: int) : int = ...`, 값 `let xs : int list = []`. 최상위 선언, `let rec … and`, REPL에서도 씁니다.
  - 타입 인자가 여럿인 타입 `(int, string) pair`를 `type` 선언과 표기에서 쓸 수 있습니다.
  - 맞지 않으면 `Type mismatch`이고 위치는 표기한 식을 가리킵니다. 모르는 타입은 `Unknown type: foo`이고 위치는 타입을 가리킵니다.
  - `'a`는 "아직 모르는 타입"이고, 한 `let` 정의 안에서 같은 이름은 같은 타입입니다. polymorphic을 강제하지 않습니다(OCaml과 같음).
- 경고: 타입 표기의 `'a`가 다른 타입으로 정해지면 `Warning: the type variable 'a has been constrained to be type int`(F# FS0064와 같음)
- 테스트 41개 추가 (총 853개)

### 변경
- `:`가 토큰(`COLON`)이 되었습니다. 예전에는 `Unexpected character: :`였습니다.

## [0.28.0] - 2026-10-28

### 추가
- 쓰이지 않는 분기 경고: 앞의 분기들이 이미 모든 경우를 덮어서 절대 선택되지 않는 분기에 `Warning: this match case is unused (line, column)`(OCaml Warning 11, F# FS0026과 같음). `match`와 `function` 모두, `calc`, `calc --compile`, REPL에서 출력합니다.
  - 빠진 분기 검사와 같은 Maranget usefulness 검사(`useful`)로 판정합니다. 가드가 있는 앞 분기는 덮는 데 세지 않습니다.
  - 위치는 분기의 패턴을 가리킵니다. 파서가 분기(패턴, 가드, 본문 튜플)에 위치를 기록해서 `_` 분기도 가리킬 수 있습니다.
- 테스트 13개 추가 (총 812개)

## [0.27.0] - 2026-10-28

### 추가
- 작은 문법 묶음(OCaml, F#과 같은 의미). 인터프리터, 타입 추론, 빠진 분기 검사, 컴파일러, REPL 모두 지원합니다.
  - `a % b`: 나머지. 부호는 `a`를 따릅니다. 0과 `Int32.MinValue % -1`은 `/`와 같은 오류입니다. `*`, `/`와 같은 우선순위입니다.
  - `x |> f`(파이프, `x`를 먼저 계산), `f >> g`(합성). 비교와 같은 우선순위에서 왼쪽부터 묶입니다.
  - `function | p -> e | …`: 인자 하나를 받아 바로 `match`하는 함수
  - or 패턴 `p1 | p2`: 왼쪽부터 처음 맞는 쪽. 패턴 안 어디서나(`Some (1 | 2)`). 두 쪽이 같은 이름을 묶지 않으면 `Variable x must occur on both sides of this | pattern`. 빠진 분기 검사가 or 패턴을 펼쳐서 판정합니다.
  - `let rec f … and g …`(mutual recursion), 최상위 선언으로도. 같은 이름을 두 번 정의하면 오류. 컴파일러는 그룹의 클로저를 모두 할당한 뒤 서로를 가리키는 칸을 채웁니다.
- 테스트 25개 추가 (총 799개)

### 변경
- `and`, `function`이 키워드가 되었습니다(변수 이름으로 쓸 수 없음).

## [0.26.0] - 2026-10-28

### 추가
- REPL: 인자 없이 터미널에서 `calc`를 실행하거나 `--repl`을 주면 뜹니다(OCaml toplevel, `dotnet fsi`처럼).
  - 입력은 줄 끝의 `;;`까지이고, 여러 줄에 걸쳐 쓸 수 있습니다. `#quit;;`나 Ctrl-D로 끝냅니다.
  - 결과는 OCaml처럼 보여 줍니다: `- : int = 3`, `val add : int -> int -> int = <fun>`, `type 'a tree`.
  - 앞에서 정의한 이름, 함수, 타입은 다음 입력에서도 보입니다. 타입은 지금까지의 선언을 앞에 붙여 다시 추론하고, 값은 이어서 써서 앞의 `print`가 되풀이되지 않습니다. 경고는 그 입력에서 새로 생긴 것만 보여 줍니다.
  - 오류는 위치와 함께 보여 주고 계속합니다.
- 파서 시작 기호 `startItems`(최상위 항목 목록), `Ast.TopItem`, `Frontend.parseItems`, `Eval.declareConstructors`, `Eval.withOutput`
- `ReplTests.fs`: 테스트 10개 (총 774개)

### 변경
- 파싱, 큰 스택 실행, 오류 위치 표시를 `Program.fs`에서 `Frontend.fs`로 옮겼습니다(`Program.parse` 등은 그대로 씀).
- 표준 입력이 터미널이고 인자가 없으면 이제 REPL을 띄웁니다. 파이프 입력(`echo ... | calc`)은 예전과 같습니다.
- `howto/` 문서 44개의 파일 이름 앞에 쓴 순서대로 번호를 붙였습니다(`01-handle-fsyacc-nonassoc-conflict.md` … `44-redirect-interpreter-output-per-test-with-asynclocal.md`). 문서 사이의 참조도 새 이름으로 고쳤습니다. 옛 이름의 링크는 더 이상 열리지 않습니다.
- 각 howto 문서에 관련 커밋을 연결했습니다. 문서가 설명하는 코드 변경(문서를 쓰기 직전의 릴리스 커밋)을 머리말의 `order`, `commit`, `version`과 제목 아래 링크로 적었습니다. `howto/README.md` 표에 "관련 커밋" 열을 더했습니다.

## [0.25.0] - 2026-10-28

### 추가
- 최상위 선언: `let x = e`, `let f x = e`, `let rec`, `let (a, b) =`, `let () =`, `let _ =`, `type`을 `in` 없이 차례로 씁니다. 선언 뒤의 식은 `;;` 다음에 씁니다(OCaml과 같음). 파서가 `let … in` 사슬로 풀어 써서 평가기, 타입 추론, 컴파일러는 그대로입니다. 식 하나만 있는 프로그램은 예전과 같은 AST입니다.
- `unit`: 값 `()`, 패턴 `()`, 매개변수 `let f () = …`(인자 타입이 `unit`으로 정해짐), 타입 `unit`. 컴파일러에서는 즉시값 `7`입니다.
- 순서 실행 `e1; e2`: `let`의 본문과 오른쪽, `fun`의 본문, `match` 분기, 괄호 안, 최상위에서 오른쪽 끝까지 이어집니다. `if`의 `else` 분기는 `;` 앞에서 끝납니다. 리스트 안의 `;`는 원소 구분자 그대로입니다.
- 내장 함수 `print`, `println`, `ignore` (`'a -> unit`): 문자열은 따옴표 없이, 나머지는 결과와 같은 모양으로 표준 출력에 씁니다. 인터프리터와 컴파일러 모두(내장 함수 36개).
- 주석: `//` 줄 주석, `(* … *)` 블록 주석(중첩 가능). 닫히지 않으면 `Unterminated comment`.
- 파일 실행 `calc prog.calc`: 프로그램이 출력한 것만 보여 주고, 마지막 값이 `()`가 아니면 그 값을 출력합니다(`ocaml`, `dotnet fsi`처럼). `calc --compile out prog.calc`로 파일을 컴파일합니다. 컴파일한 실행 파일도 결과가 `()`면 결과 줄을 출력하지 않습니다.
- 버리는 값 경고: `e1; e2`의 `e1`이 `unit`이 아니면 `Warning: this expression has type int and its value is ignored`(F# FS0020과 같음).
- `examples/hello.calc`, `examples/tree.calc`. 컴파일러 테스트가 예제를 인터프리터와 비교합니다.
- `Eval.evalWithOutput`: `print`의 출력을 받을 `TextWriter`를 정해서 평가합니다(테스트용, `AsyncLocal`이라 병렬 테스트끼리 섞이지 않음).
- 테스트 27개 추가 (총 764개).

### 변경
- 빈 입력과 `in` 없는 `let`은 이제 파스 오류가 아닙니다. 빈 프로그램의 결과는 `()`입니다.
- `toString`은 `print`와 같은 규칙(`Eval.display`)을 씁니다.
- 문서(`docs/`, `howto/`, `README.md`, `CHANGELOG.md`)의 전문용어를 영어로 바꿨습니다. 한국어로 옮긴 용어가 오히려 뜻을 흐리는 경우입니다. 예: unification, generalization, closure conversion, free variable, short-circuit evaluation, differential testing, soundness, code unit, surrogate pair, dialect, region, lowering, alignment, calling convention, entry point, level(HM).
  - 조사는 영어 발음에 맞췄습니다(`unification이`, `dialect가`, `level로`).
  - 널리 쓰는 말(타입, 함수, 클로저, 파서, 런타임, 패턴 매칭, 우선순위 등)과 정렬(sort), 영역(일반적인 뜻) 같은 다른 뜻의 말은 그대로 두었습니다. 코드 블록과 인라인 코드는 바꾸지 않았습니다.

## [0.24.0] - 2026-10-28

### 추가
- 타입 오류의 위치: OCaml처럼 문제가 된 식이나 패턴을 가리킵니다. 함수 인자, `if` 조건, `else` 분기, 연산자 피연산자, `::`의 오른쪽, 리스트 원소, 패턴, 가드, 분기 본문을 가리킵니다. 파서 오류와 같은 형식으로 `(line, column)`과 `^`를 출력합니다.
  - 파서가 식·패턴 노드의 시작 위치를 따로 기록합니다(`Errors.recordPosition`, `positionOf`). AST 모양과 `%A` 출력은 그대로입니다.
  - `Infer.TypeError`에 `position` 필드를 더했습니다. `Message`에는 위치를 넣지 않습니다.
- 빠진 `match` 분기 경고: `match`와 `let` 패턴이 모든 값을 덮지 못하면 `Warning: this match is not exhaustive. Example of a value not matched: None (line 1, column 1)`을 표준 에러로 출력합니다. 실행은 계속합니다(`calc`와 `calc --compile` 모두).
  - Maranget의 usefulness 검사로 판정하고, 같은 계산에서 값의 예(`[]`, `(false, 1)`, `[_]`, `Some None`, `2`)를 만듭니다. 가드가 있는 분기는 덮는 데 세지 않습니다.
  - `Infer.typecheckWithWarnings`: 타입과 경고 목록을 돌려줍니다. `typecheck`는 그대로입니다.
- `DiagnosticTests.fs`: 테스트 31개 추가 (총 737개). soundness 테스트: 실행 중 `Match failure`가 나는 평가 예제는 모두 경고를 받습니다.

## [0.23.0] - 2026-10-28

### 추가
- 네이티브 컴파일러 단계 7: 내장 함수 33개를 컴파일합니다. 이제 모든 기능을 컴파일하고, 타입 검사를 통과하는 평가 예제 263개 전부가 인터프리터와 같은 결과와 오류 메시지를 냅니다.
  - `runtime/builtins.c`: 문자열 14개, 리스트 14개, `fst`/`snd`, option 3개. 인자 순서, 결과, 오류 메시지가 `Eval.callBuiltin`과 같습니다.
  - 인자가 다 모인 호출은 런타임 함수를 바로 부릅니다. 값으로 쓰거나 partial application을 하면 인자를 하나씩 받는 클로저가 됩니다. `let`으로 가린 이름은 보통 변수입니다(`Lower`가 변수 범위를 따라감).
  - `calc_apply`: 런타임의 higher-order function(`map`, `filter`, `fold`, `exists`, `forall`, `tryFind`)이 사용자 클로저를 부릅니다.
  - `Some`/`None`의 이름과 `None` 값은 런타임 전역입니다. 런타임이 만든 option도 컴파일한 패턴과 맞습니다.
  - `upper`, `lower`, `trim`은 .NET에서 생성한 Unicode 표(`runtime/unicode.h`, `gen-unicode.fsx`)를 씁니다.
  - `toString`과 출력이 같은 코드(`calc_buffer_value`)를 씁니다. 오류 메시지 길이 제한을 없앴습니다(`vasprintf`).
- 컴파일러 테스트 9개 추가 (총 706개): 내장 함수 실행 표(문자열, 리스트·튜플·option, 값으로 쓰기, 오류), `Lower`의 내장 함수 처리, Unicode 표와 .NET 비교. differential testing이 비교하는 예제는 185개에서 286개로 늘었습니다.

### 변경
- 런타임 캐시는 `.c` 파일마다 `calc-<파일>-<해시>.o`이고, 해시는 런타임의 모든 `.c`·`.h` 파일로 만듭니다. 빌드 출력에는 `runtime/`의 `.c`, `.h`만 복사합니다.

## [0.22.0] - 2026-10-28

### 추가
- 네이티브 컴파일러 단계 6: ADT와 `option`을 컴파일합니다. 이제 AST의 모든 식을 컴파일하고, 남은 것은 내장 함수입니다.
  - `type` 선언(type parameter, 재귀 타입), 생성자 값과 생성자 패턴, `Some`/`None`
  - 생성자 값은 `[헤더, 생성자 이름, 인자]` 블록입니다. 인자 없는 생성자는 8-byte aligned 전역 상수 블록이라 할당이 없습니다.
  - 바로 적용한 `Some 1`은 그 자리에서 블록을 만들고, 값으로 쓴 `Some`은 클로저 `fun x -> Some x`가 됩니다(`<fun>`).
  - 출력(`Some (Some 1)`, `Some (-1)`, `Rect (2, 3)`), `=`(생성자가 다르면 인자를 보지 않음), Match failure가 인터프리터와 같습니다.
- 컴파일러 테스트 4개 추가 (총 697개). differential testing이 비교하는 예제는 155개에서 185개로 늘었습니다.

## [0.21.0] - 2026-10-28

### 추가
- `LICENSE`(MIT)를 추가했습니다. 저장소는 https://github.com/ohama/calc-mlir 에 공개되어 있습니다.
- `Codegen.emitEntries`와 `Toolchain.buildMany`: 여러 프로그램을 실행 파일 하나로 빌드합니다. 실행 파일 안의 드라이버가 프로그램마다 `fork`해서 실행합니다(테스트용).
- 컴파일러 테스트 `단독 실행 파일` 3개 추가 (총 693개): `calc --compile`과 같은 경로를 확인합니다.

### 변경
- 테스트 시간을 약 1분 30초에서 약 5초로 줄였습니다.
  - 원인: 예제마다 실행 파일을 새로 만들었습니다. macOS에서 새 실행 파일의 첫 실행에 약 0.3초가 걸리고(아무 일도 하지 않는 C 프로그램도 같음), 외부 도구 실행과 .NET 프로세스 생성 비용이 더해졌습니다.
  - 실행 테스트를 (입력, 기대값) 표로 바꾸고, differential testing 입력과 함께 실행 파일 하나로 묶어 빌드합니다. 입력은 코어 수만큼의 MLIR 모듈로 나눠 병렬로 컴파일합니다.
- C 런타임을 한 번만 컴파일해서 임시 폴더에 `calc-runtime-<해시>.o`로 캐시합니다. `--compile`은 이제 `prog.ll`을 오브젝트로 컴파일한 뒤 캐시한 런타임과 링크합니다. `--keep-temps`에는 `prog.o`도 남습니다.
- 람다 함수는 `internal` 링크로 만듭니다. 여러 프로그램을 함께 링크해도 이름이 겹치지 않습니다.

## [0.20.0] - 2026-10-28

### 추가
- 네이티브 컴파일러 단계 5: 리스트, 튜플, 패턴 매칭을 컴파일합니다.
  - `[]`, `::`, `[a; b]`, 튜플, `match`(상수·문자열·`[]`·`::`·튜플·중첩 패턴, 가드), `let (a, b) = e in body`
  - cons는 `[헤더, head, tail]`, 튜플은 `[헤더, 원소...]` 블록이고, `[]`는 immediate value `5`입니다.
  - `match`는 `scf.if`만으로 만듭니다. 분기마다 조건(패턴과 가드)을 `i1`로 먼저 계산해서, 나머지 분기는 한 번만 생성됩니다. 어떤 값과도 일치하는 분기에서는 검사 코드를 만들지 않습니다.
  - 출력, structural `=`, `Error: Match failure: <값>`이 인터프리터와 같습니다(`=`는 길이를 먼저 비교).
  - 생성자 패턴과 `Some`/`None`은 `Not supported by the compiler yet`으로 멈춥니다(단계 6).
- 컴파일러 테스트 11개 추가 (총 690개). differential testing이 비교하는 예제는 96개에서 155개로 늘었습니다.

## [0.19.0] - 2026-10-28

### 추가
- 네이티브 컴파일러 단계 4: 문자열을 컴파일합니다.
  - 문자열 리터럴, `+`(연결), `=`/`<>`, `< > <= >=`(ordinal), 함수와 함께 쓰기
  - 문자열은 `[헤더, 길이, UTF-16 코드 단위...]` 블록입니다. 상수는 8-byte aligned 전역(같은 내용은 하나), 연결 결과는 `GC_MALLOC_ATOMIC`으로 할당합니다.
  - 출력은 인터프리터와 같습니다: 따옴표, `\\ \" \n \t \r` 이스케이프, UTF-8(surrogate pair 포함, lone surrogate는 U+FFFD)
  - 비교는 `String.CompareOrdinal`과 같은 UTF-16 code unit 순서입니다(`"😀" < "\uFFFF"`가 `true`).
- 컴파일러 테스트 7개 추가 (총 679개). differential testing이 비교하는 예제는 79개에서 96개로 늘었습니다.

## [0.18.0] - 2026-10-28

### 추가
- 네이티브 컴파일러 단계 3: 함수와 클로저를 컴파일합니다.
  - `fun`, 호출, 커링(partial application), `let f x =`, `let rec`, higher-order function, lexical scope
  - `Core.fs`(IR)와 `Lower.fs`(Ast → Core: 연산을 `Prim`으로, `&&`/`||`를 `if`로, closure conversion)를 추가했습니다. `Codegen`은 이제 Core를 받습니다.
  - 클로저는 `[헤더, 코드 포인터, 캡처한 값...]` 블록이고 Boehm GC(`calc_alloc`)로 할당합니다. 호출은 `(클로저, 인자) -> 결과` indirect call 하나입니다.
  - 함수 값은 `<fun>`으로 출력하고, 함수끼리 `=` 비교는 인터프리터와 같은 타입 오류로 끝납니다.
  - 메인 스레드 스택을 512MB로 링크해서(`-Wl,-stack_size`) 재귀 깊이 100만까지 인터프리터와 같은 결과를 냅니다.
- 컴파일러 테스트 12개 추가 (총 672개): closure conversion, 클로저 실행, 깊은 재귀, 함수 값. differential testing이 비교하는 예제는 56개에서 79개로 늘었습니다.
- `docs/compiler-architecture.md`: 컴파일러 구조와 흐름. 예제 하나를 MLIR → LLVM IR → 실행 파일까지 따라가고, entry point `main`과 런타임의 배포·링크·호출, 지금 구조의 한계를 설명합니다.

## [0.17.0] - 2026-10-28

### 추가
- 네이티브 컴파일러 단계 2: `let x = e in body`와 변수를 컴파일합니다.
  - 새 코드를 만들지 않고 이름 → SSA 값 환경으로 처리합니다. 섀도잉과 `if` 분기 안의 `let`도 인터프리터와 같습니다.
  - `let`의 값은 쓰이지 않아도 계산합니다(`let x = 1 / 0 in 5`는 오류, 인터프리터와 같음).
  - 내장 함수 이름은 `Not supported by the compiler yet: built-in function <이름>`으로 멈춥니다(단계 7에서 지원).
- 컴파일러 테스트 5개 추가 (총 660개). differential testing이 비교하는 예제는 43개에서 56개로 늘었습니다.

## [0.16.0] - 2026-10-28

### 추가
- 네이티브 컴파일러 단계 1: 정수와 불리언 연산을 컴파일합니다.
  - `+ - * /`, 단항 `-`: 32비트에서 한 바퀴 돌고 나눗셈은 0 쪽으로 버립니다(인터프리터와 같음).
  - 비교(`= <> < > <= >=`), `not`, short-circuit evaluation을 하는 `&&`·`||`, `if`(`scf.if`)
  - 0으로 나누기와 `Int32.MinValue / -1`은 인터프리터와 같은 메시지(`Error: Attempted to divide by zero.`, `Error: Arithmetic operation resulted in an overflow.`)와 종료 코드 1로 끝납니다.
- 런타임 함수 `calc_add`, `calc_eq`, `calc_compare`(태그를 보고 동작을 고름), `calc_error_divide_by_zero`, `calc_error_overflow`
- 컴파일러 테스트 10개 추가 (총 655개). differential testing은 실행 중 오류 예제까지 비교하고(stderr, 종료 코드), 예제를 병렬로 컴파일합니다. 비교하는 예제는 2개에서 43개로 늘었습니다.

## [0.15.0] - 2026-10-28

### 추가
- 네이티브 컴파일러 단계 0 ([docs/compiler-design.md](docs/compiler-design.md)): 정수와 불리언 리터럴을 MLIR → LLVM → 실행 파일로 컴파일합니다.
  - `--compile <path>`: 실행 파일을 만듭니다. 실행하면 인터프리터의 `Result:`와 같은 형식으로 값을 출력합니다.
  - `--emit-mlir`: 생성한 MLIR을 출력합니다.
  - `--keep-temps`: 중간 파일(`.mlir`, `.ll`)을 남깁니다.
  - 지원하지 않는 기능은 `Not supported by the compiler yet: <기능>`으로 멈춥니다.
  - 타입 추론을 통과한 프로그램만 컴파일합니다(`--no-typecheck`와 함께 쓸 수 없음).
- `Codegen.fs`(AST → MLIR 텍스트, tagged 64비트 value representation), `Toolchain.fs`(도구 찾기와 실행), `src/Calc/runtime/`(C 런타임, Boehm GC)
- 도구 위치: `CALC_LLVM_BIN`, `CALC_GC_PREFIX`, 기본은 Homebrew 경로. 필요: `brew install llvm bdw-gc`
- 컴파일러 differential testing(`CompileTests.fs`): 실행 파일의 출력을 인터프리터 결과와 비교합니다. 도구가 없으면 건너뜁니다. 테스트 7개 추가 (총 645개)

### 변경
- `docs/build.md`, `docs/testing.md`, `README.md`에 컴파일 옵션과 준비 방법을 반영했습니다.

## [0.14.0] - 2026-10-28

### 추가
- Hindley–Milner 타입 추론 (`Infer.typecheck`). `calc`는 평가 전에 타입을 추론하고, 맞지 않으면 평가하지 않습니다.
  - let-polymorphism, `let rec`, 패턴, 튜플, 리스트, `option`, 사용자 정의 타입(`type` 선언의 `of` 타입 사용)
  - `+`, `<` 등은 `int` 끼리 또는 `string` 끼리. allowed list가 있는 type variable은 generalize하지 않고, 끝까지 정해지지 않으면 `int`
  - `=`는 OCaml처럼 `'a -> 'a -> bool`
  - 같은 이름의 다른 타입은 오류 메시지에서 `t/1`, `t/2`로 구분
- 출력에 `Type:` 줄 추가 (`AST:`와 `Result:` 사이)
- 옵션 `--no-typecheck`: 타입 추론을 건너뛰고 평가만 합니다(평가 중 동적 타입 검사는 그대로).
- 타입 오류 `Type mismatch: expected ... but got ...`, `cannot construct infinite type`, `Unknown type`, `Unbound type variable`, `Duplicate type variable`
- 테스트 85개 추가 (총 638개). soundness 테스트: 타입 검사를 통과한 평가 예제는 실행 중 타입 오류 없이 그 타입의 값이 됩니다.

### 변경
- `[1; true]`, `if true then 1 else false`, 원소 수가 다른 튜플 비교처럼 예전에 실행되던 식이 이제 타입 오류입니다. `--no-typecheck`로는 여전히 실행됩니다.
- 대부분의 실행 중 `Type error`가 평가 전의 `Type mismatch`로 바뀌었습니다.
- 타입 추론도 평가처럼 512MB 스택의 스레드에서 실행합니다.
- `docs/grammar.md`(6. 타입 추론), `docs/testing.md`, `docs/build.md`, `README.md`에 반영했습니다.

## [0.13.0] - 2026-10-28

### 추가
- algebraic data type `type 'a tree = Leaf | Node of 'a tree * 'a * 'a tree in 식`
  - type variable은 OCaml처럼 이름 앞에 씁니다(`'a tree`, `('k, 'v) entry`).
  - `of` 뒤의 타입은 검사하지 않고, 튜플 원소 수로 생성자 인자 수만 확인합니다.
  - 생성자는 값입니다. 인자 있는 생성자는 함수처럼 넘길 수 있습니다(`map Some xs`).
- 생성자 패턴 `None`, `Some x`, `Node (l, x, r)`. 모르는 생성자나 인자 유무가 다른 패턴은 오류입니다.
- `'a option = None | Some of 'a`를 선언 없이 쓸 수 있습니다.
- 내장 함수 `tryHead`, `tryFind`, `defaultValue`
- 오류 `Unknown constructor`, `Constructor X expects N fields but got ...`, `Duplicate constructor`
- 렉서: 키워드 `type`, `of`, 토큰 `CTOR`(대문자로 시작), `TYVAR`(`'a`)
- AST: `TypeExpr`, `Ctor`, `TypeDecl`, `PCtor`. 값: `VData`, `VConstructor`
- 테스트 62개 추가 (총 553개)

### 변경
- **대문자로 시작하는 이름은 이제 생성자입니다.** 변수 이름으로 쓸 수 없습니다(`let X = 1 in X`는 parse error).
- 함수를 비교할 때의 오류 메시지가 `expected int, bool, string, list, tuple or data but got function`으로 바뀌었습니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 ADT와 option을 반영했습니다.

## [0.12.0] - 2026-10-28

### 추가
- 튜플 `(1, "a", true)`. 괄호 안에 원소 2개 이상을 씁니다. 같은 원소 수끼리 `=`로 비교할 수 있습니다.
- 일반 패턴 매칭
  - 분기 수 제한 없음. 위에서부터 첫 일치 분기를 고르고, 없으면 `Match failure: <값>`
  - 패턴: 변수, `_`, 정수·문자열·불리언 리터럴, `[]`, `p :: p`, `[p; p]`, `(p, p)`, 중첩
  - `when` 가드
- 튜플 분해 `let (a, b) = e in body`
- `_` 인자: `fun _ -> 1`, `let f _ x = x`
- 내장 함수 `fst`, `snd`
- 파서 오류 `Duplicate variable in pattern: <이름> (line L, column C)`
- 렉서: 키워드 `when`, 토큰 `,`, `_`
- AST: `Pattern`(`PVar`, `PWildcard`, `PInt`, `PBool`, `PString`, `PNil`, `PCons`, `PTuple`), `Tuple`, `LetPattern`. 값: `VTuple`
- 테스트 72개 추가 (총 491개)

### 변경
- `Match`의 AST가 `Match (e, [ (pattern, guard, body) ... ])`로 바뀌었습니다. 예전의 두 분기 고정 `match`는 이 일반 형식에 포함됩니다.
- 중첩 `match`의 뒤 분기는 이제 F#처럼 **안쪽** `match`의 것입니다. 예전에는 분기 수가 두 개로 고정이라 바깥 `match`의 것이었습니다. 바깥 분기로 쓰려면 안쪽 `match`를 괄호로 감쌉니다.
- `match`에서 리스트가 아닌 값은 타입 오류 대신 `Match failure`입니다. 패턴은 타입이 달라도 불일치로 처리합니다.
- `_` 하나는 이제 변수 이름이 아닙니다. 식에 쓰면 parse error입니다.
- 함수를 비교할 때의 오류 메시지가 `expected int, bool, string, list or tuple but got function`으로 바뀌었습니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 튜플과 패턴 매칭을 반영했습니다.

## [0.11.0] - 2026-10-28

### 추가
- 렉서·파서 오류에 위치 표시. 줄과 열은 1부터 셉니다.
  - `parse error: unexpected '+' (line 1, column 5)`처럼 문법에 맞지 않는 토큰과 그 위치를 보여 줍니다.
  - 입력이 중간에 끝나면 `parse error: unexpected end of input`이고, 위치는 마지막 글자 바로 뒤입니다.
  - `Unterminated string`은 여는 `"` 위치, `Invalid escape sequence`는 `\` 위치를 보여 줍니다.
- `calc`가 오류 메시지 아래에 오류가 난 줄과 `^` 표시를 출력합니다.
- `Errors` 모듈: `SyntaxError(message, line, column)` 예외
- `Program.errorLocation`: 오류 줄과 `^` 표시 문자열을 만듭니다.
- 테스트 16개 추가 (총 419개)

### 변경
- 렉서 오류가 `Exception` 대신 `Errors.SyntaxError`입니다. `Message`에 위치가 붙습니다.
- 파서 오류 메시지가 `parse error`에서 `parse error: unexpected ... (line L, column C)`로 바뀌었습니다.
- 범위를 넘는 정수 리터럴의 오류가 .NET 메시지(`Value was either too large or too small for an Int32.`) 대신 `Integer literal out of range: <숫자>`입니다.
- `docs/grammar.md`, `docs/testing.md`, `docs/build.md`, `README.md`에 오류 위치 규칙과 메시지를 반영했습니다.

## [0.10.0] - 2026-10-28

### 추가
- 리스트 내장 함수 14개
  - 기본: `head`, `tail`, `isEmpty`, `nth`, `reverse`, `append`, `range`, `sum`, `sort`
  - higher-order function: `map`, `filter`, `fold`, `exists`, `forall`. F# `List`처럼 함수가 먼저, 리스트가 마지막입니다.
- 평가 오류 `head: empty list`, `tail: empty list`, `nth: index out of range (...)`
- `Eval.compareValues`: `<` 등과 `sort`가 함께 쓰는 순서
- 테스트 71개 추가 (총 403개)

### 변경
- `Eval.callBuiltin`이 첫 인자로 `apply`를 받습니다. 고차 내장 함수가 사용자 함수를 호출하는 데 씁니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 리스트 내장 함수를 반영했습니다.

## [0.9.0] - 2026-10-28

### 추가
- 내장 함수 14개. 처음 환경에 들어 있고, 커링되며, `let`으로 가릴 수 있습니다.
  - 문자열: `length`, `substr`, `upper`, `lower`, `trim`, `contains`, `startsWith`, `endsWith`, `indexOf`, `replace`, `split`, `join`
  - 변환: `toString`, `toInt`
  - `length`는 리스트에도 쓸 수 있습니다.
- 평가 오류 `substr: index out of range (...)`, `replace: empty search string`, `toInt: invalid integer: "..."`
- 값: `VBuiltin`. `<fun>`으로 출력되고 타입 이름은 `function`입니다.
- `Eval.builtins`(내장 함수 환경), `Eval.builtinArity`
- 테스트 66개 추가 (총 332개)

### 변경
- `Eval.eval`이 빈 환경 대신 `builtins` 환경에서 평가합니다. `evalIn Map.empty`에는 여전히 내장 함수가 없습니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 내장 함수를 반영했습니다.

## [0.8.0] - 2026-10-28

### 추가
- 문자열 리터럴 `"..."`. 이스케이프 `\"`, `\\`, `\n`, `\t`, `\r`을 지원하고, 한글 같은 유니코드 문자를 쓸 수 있습니다.
- 문자열 연결 `+`. 왼쪽 값이 int면 덧셈, string이면 연결입니다.
- 문자열 비교 `=`, `<>`, `<`, `>`, `<=`, `>=`. 순서 비교는 문자 코드 순서(ordinal)입니다.
- 렉서 오류 `Unterminated string`, `Invalid escape sequence: \<문자>`
- AST: `Str`, 값: `VString`. 리터럴 형태(`"a\nb"`)로 출력되고 타입 이름은 `string`입니다.
- 테스트 42개 추가 (총 266개)

### 변경
- `+`와 `<` `>` `<=` `>=`의 왼쪽 값이 int도 string도 아니면 오류 메시지가 `expected int or string but got <타입>`입니다. 예전에는 `expected int`였습니다.
- 함수를 비교할 때의 오류 메시지가 `expected int, bool, string or list but got function`으로 바뀌었습니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 문자열 문법과 의미를 반영했습니다.

## [0.7.0] - 2026-10-28

### 추가
- 리스트 리터럴 `[]`, `[1; 2; 3]`. 리터럴은 `::`와 `[]`로 바뀝니다.
- cons 연산자 `::`. right-associative고, 비교보다 강하고 `+ -`보다 약합니다.
- `match e with [] -> e1 | h :: t -> e2`. 두 분기는 순서와 상관없고, 첫 `|`는 생략할 수 있습니다.
- 리스트 비교 `=`, `<>`. 원소를 차례로 비교합니다.
- 렉서: 키워드 `match`, `with`, 토큰 `[`, `]`, `;`, `::`, `|`
- AST: `Nil`, `Cons`, `Match`
- 값: `VList`. `[1; 2; 3]`으로 출력되고 타입 이름은 `list`입니다.
- 테스트 53개 추가 (총 224개)

### 변경
- 함수를 비교할 때의 오류 메시지가 `expected int, bool or list but got function`으로 바뀌었습니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 리스트 문법과 의미를 반영했습니다.

## [0.6.0] - 2026-10-27

### 추가
- 람다 `fun x y -> body`. 여러 인자는 중첩 람다(커링)로 바뀝니다.
- 함수 호출 `f x y`. 붙여 쓰는 방식이고, left-associative며, 가장 높은 우선순위입니다.
- 함수 정의 syntactic sugar `let f x y = body in e`
- 재귀 함수 `let rec f x = body in e`
- 클로저(lexical scope), partial application, call-by-value
- 렉서: 키워드 `fun`, `rec`, 토큰 `->`
- AST: `Lambda`, `App`, `LetRec`
- 값: `VClosure`, `VRecClosure`. `<fun>`으로 출력되고 타입 이름은 `function`입니다.
- 테스트 46개 추가 (총 171개)

### 변경
- `calc`는 평가를 512MB 스택의 스레드에서 실행합니다. 기본 스택에서는 재귀 깊이 약 7,000에서 프로세스가 죽었지만, 이제 약 100,000까지 동작합니다.
- `docs/grammar.md`, `docs/testing.md`, `README.md`에 함수 문법과 의미를 반영했습니다.

## [0.5.0] - 2026-10-27

### 추가
- 불리언 리터럴 `true`, `false`
- 비교 연산자 `=`, `<>`, `<`, `>`, `<=`, `>=`
  - `=`와 `<>`는 int끼리 또는 bool끼리 비교합니다. 나머지는 int끼리만 비교합니다.
- 논리 연산자 `&&`, `||`(short-circuit evaluation), `not`
- `if <식> then <식> else <식>`. `else`는 생략할 수 없습니다.
- 우선순위 (낮은 것부터): `let`/`if` < `||` < `&&` < 비교 < `+ -` < `* /` < 단항 `-`, `not`
- 실행 중 타입 검사: `Type error: expected <int|bool> but got <int|bool>`
- 렉서: 키워드 `if`, `then`, `else`, `true`, `false`, `not`, 연산자 `<>`, `<`, `>`, `<=`, `>=`, `&&`, `||`
- AST: `Bool`, `If`, `Equal`, `NotEqual`, `Less`, `Greater`, `LessEqual`, `GreaterEqual`, `And`, `Or`, `Not`
- 테스트 70개 추가 (총 125개)
- `README.md`: 프로젝트 소개, 언어 기능, 시작하기, 구조, 문서 링크
- `howto/`: 개발 중 해결한 문제를 기록한 문서 6개
  - fsyacc `%nonassoc` 충돌, `let`/`if` 범위와 우선순위, fslex 키워드 순서
  - Expecto와 `dotnet test`, fsproj 버전 관리, 파서용 FsCheck property-based testing

### 변경
- 평가기를 `Program.fs`에서 새 모듈 `Eval.fs`로 옮겼습니다.
  - 평가 결과의 타입이 `int`에서 `Value`(`VInt of int | VBool of bool`)로 바뀌었습니다.
  - 환경의 타입이 `Map<string, Value>`로 바뀌었습니다.
  - `Program.eval`, `Program.evalIn` 대신 `Eval.eval`, `Eval.evalIn`을 씁니다.
- `docs/grammar.md`, `docs/testing.md`, `docs/build.md`에 새 문법과 모듈 구성을 반영했습니다.

## [0.4.0] - 2026-10-27

### 추가
- `let` 바인딩: `let <이름> = <식> in <식>`
  - 본문은 오른쪽으로 최대한 이어집니다. `let x = 1 in x + 2`는 `let x = 1 in (x + 2)`입니다.
  - 섀도잉: 안쪽 바인딩이 우선합니다. `let x = 1 in let x = 2 in x`는 `2`입니다.
- 변수 참조. 정의되지 않은 변수를 쓰면 `Unbound variable: <이름>` 오류가 납니다.
- 렉서: 키워드 `let`, `in`, 식별자(`[A-Za-z_][A-Za-z0-9_]*`), `=` 토큰
- AST: `Var of string`, `Let of string * Expr * Expr`
- 평가기: 환경(`Map<string, int>`)을 받는 `evalIn env expr`. `eval`은 빈 환경으로 `evalIn`을 호출합니다.
- 테스트 25개 추가 (총 55개)
- `docs/grammar.md`: 어휘 구조, EBNF, 우선순위, AST 대응, 평가 규칙, 오류 정리

## [0.3.0] - 2026-10-27

### 추가
- Expecto 테스트 프로젝트 `tests/Calc.Tests` (테스트 30개)
  - Lexer: 토큰 순서, 공백 무시, 오류 처리
  - Parser: 우선순위, associativity, 괄호, 단항 마이너스, 파서 오류
  - Eval: 식 계산 표, 0으로 나누기, FsCheck property-based testing
- 솔루션 파일 `Calc.slnx`: 루트에서 `dotnet build` / `dotnet test` 실행 가능

## [0.2.0] - 2026-10-27

### 추가
- [Argu](https://fsprojects.github.io/Argu/) 6.2.5로 명령줄 옵션 처리
  - `--expr <expr>` / `-e <expr>` / `--expr=<expr>`: 식 계산
  - `--version` / `-v`: 버전 출력 (`Calc.fsproj`의 `<Version>`을 어셈블리에서 읽음)
  - `--help` / `-h`: 사용법 출력
- 잘못된 옵션을 주면 오류 메시지와 사용법을 출력하고 종료 코드 1로 끝냄

### 변경
- **호환성 깨짐**: 식을 위치 인자로 받지 않습니다. `calc "1 + 2"` 대신 `calc --expr "1 + 2"`를 쓰세요.
  `--expr`가 없으면 이전처럼 표준 입력에서 읽습니다.

## [0.1.0] - 2026-10-27

fslex/fsyacc로 만든 최소 구성의 정수 사칙연산 계산기입니다.

### 추가

**어휘 (Lexer.fsl)**
- 정수 리터럴: `digit+` (음이 아닌 정수, 32비트 `int`)
- 연산자: `+` `-` `*` `/`
- 괄호: `(` `)`
- 공백, 탭, 줄바꿈은 무시

**문법 (Parser.fsy)**
- 이항 연산: `+`, `-`, `*`, `/`
- 단항 마이너스: `-expr`
- 괄호로 묶은 식: `( expr )`
- 연산자 우선순위 (높은 것부터)
  1. 단항 `-`
  2. `*` `/` (left-associative)
  3. `+` `-` (left-associative)
- 문법 충돌 없음

**AST (Ast.fs)**
- `Expr` = `Number` | `Add` | `Subtract` | `Multiply` | `Divide` | `Negate`

**평가기 / 실행 (Program.fs)**
- 트리를 순회하며 계산하는 평가기 `eval`
- 정수 나눗셈 (`7 / 2` → `3`)
- 입력: 명령줄 인자 또는 표준 입력
- 출력: AST(`%A` 형식)와 계산 결과
- 오류 처리: 렉서 오류, 파서 오류, 0으로 나누기가 나면 `Error: ...`를 표준 에러로 출력하고 종료 코드 1로 끝냄

**문서**
- `docs/build.md`: 빌드 및 실행 방법
- `docs/testing.md`: 수동 테스트 방법과 기대 결과

### 아직 지원하지 않음
- 변수, `let` 바인딩
- 불리언, 비교 연산, `if-then-else`
- 함수
- 오류 위치(줄/열) 표시
- 자동화된 테스트
