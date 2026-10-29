# 성능 비교: Python, 인터프리터, 컴파일러

같은 일을 하는 예제 다섯 개를 Python, Calc 인터프리터, Calc 컴파일러로 실행해 출력과 시간을 비교합니다. 프로그램과 측정 스크립트는 [benchmarks/](../benchmarks/)에 있습니다.

```bash
dotnet build src/Calc -c Release
python3 benchmarks/run.py        # 예제마다 5번씩 (python3 benchmarks/run.py 10 이면 10번)
```

스크립트는 세 결과의 출력이 다르면 멈춥니다.

## 예제

| 예제 | 하는 일 | 보려는 것 |
|---|---|---|
| [fib](../benchmarks/fib.calc) | `fib 32` (호출 약 700만 번) | 함수 호출, tail call이 아닌 재귀 |
| [queens](../benchmarks/queens.calc) | 10×10 N-Queens 해의 개수 | 리스트, backtracking, 안쪽 `let rec` |
| [sieve](../benchmarks/sieve.calc) | 200만까지의 소수 개수 (에라토스테네스의 체) | 배열, `while`, `for`, `ref` |
| [msort](../benchmarks/msort.calc) | 정수 20만 개의 merge sort | 리스트 할당, 튜플 패턴 매칭 |
| [bst](../benchmarks/bst.calc) | 이진 탐색 트리에 20만 개 넣고 20만 번 찾기 | algebraic data type, 새 노드 할당 |

- **같은 출력:** Calc의 `int`는 32비트에서 한 바퀴 돌고 Python의 `int`는 크기 제한이 없습니다. 그래서 중간값이 2³¹을 넘지 않게 만들었습니다. 난수는 작은 LCG(`(x * 75 + 74) % 65537`)로 만들고, 합은 나머지 연산으로 줄입니다.
- **Python 쪽:** 같은 알고리즘을 Python답게 썼습니다.
  - queens와 sieve는 `for`와 `while`을 씁니다.
  - bst는 노드를 불변 튜플로 새로 만듭니다(Calc와 같음). 찾기는 반복문입니다.
  - msort는 연결 리스트가 아니라 Python 리스트(배열)와 slicing을 씁니다. 그래서 msort는 자료 구조까지 같은 비교는 아닙니다.

## 결과

Apple M4 Max, CPython 3.15.0(JIT 없음), `calc 0.42.0` Release, clang 22.1.7(`-O2`). 시간은 초, 한 번 데운 뒤 5번 잰 중앙값이며 프로세스 시작을 포함한 wall time입니다.

| 예제 | 출력 | Python | 인터프리터 | 실행 파일 | 컴파일 시간 |
|---|---|---|---|---|---|
| fib | `2178309` | 0.136 | 0.404 | **0.016** | 0.32 |
| queens | `724` | 0.043 | 0.376 | **0.014** | 0.29 |
| sieve | `148933` | 0.305 | 1.590 | **0.022** | 0.32 |
| msort | `442419`, `(0, 32770)` | **0.205** | 2.961 | 0.292 | 0.34 |
| bst | `(65536, 39, 130706)` | 0.351 | 2.091 | **0.215** | 0.33 |
| (아무것도 안 하는 프로그램) | | 0.014 | 0.117 | 0.005 | |

- **실행 파일:** 컴파일한 뒤 실행 파일만 실행한 시간입니다.
- **컴파일 시간:** `calc --compile`이 `mlir-opt`, `mlir-translate`, `clang`, 링크를 거치는 시간입니다. 실행 파일 시간에는 넣지 않았습니다.
- **인터프리터:** 시작 비용에 .NET 시작, 파싱, prelude를 붙인 프로그램의 타입 추론이 들어갑니다.

Python에 대한 배율:

| 예제 | 실행 파일 | 인터프리터 |
|---|---|---|
| fib | 9배 빠름 | 3.0배 느림 |
| queens | 3배 빠름 | 8.7배 느림 |
| sieve | 14배 빠름 | 5.2배 느림 |
| msort | 1.4배 느림 | 14배 느림 |
| bst | 1.6배 빠름 | 6.0배 느림 |

## 관찰

### 호출, 반복, 배열은 컴파일러가 크게 빠르다

fib, queens, sieve에서 실행 파일은 Python보다 3~14배 빠릅니다. 알려진 함수는 direct call, 꼬리 위치의 호출은 tail call이 되고(`tailcc`), 정수는 tagged 값이라 상자에 넣지 않습니다. queens는 일이 작아서 시작 비용 차이(0.014초와 0.005초)가 배율에 크게 들어갑니다.

### 할당이 많으면 Python과 비슷하다

msort와 bst는 Python과 비슷하거나 느립니다. 이유 세 가지를 보았습니다.

- **GC (확인함):** Boehm GC의 처음 힙을 키우면 시간이 줄어듭니다. msort는 GC가 65번 돌고 힙이 약 34MB까지 커집니다.

  ```bash
  calc --compile=msort benchmarks/msort.calc
  GC_PRINT_STATS=1 ./msort                    # 수거 횟수와 힙 크기
  GC_INITIAL_HEAP_SIZE=1000000000 ./msort     # msort 약 0.27초 → 0.22초, bst 0.20초 → 0.14초
  ```

- **비교 연산 (추정):** Core IR에는 타입이 없어서 `<`, `<=`, `=`는 인자가 int인지 string인지 모릅니다. 그래서 C 런타임의 polymorphic `calc_compare`를 부릅니다. 생성한 MLIR에서 msort는 4곳, bst는 7곳입니다(`calc --emit-mlir`로 확인). 호출 비용을 따로 재지는 않았습니다.
- **자료 구조:** msort의 Python 쪽은 배열이라 원소마다 블록을 할당하지 않습니다.

### 인터프리터는 CPython보다 3~14배 느리다

- **시작 비용:** 0.12초가 들어서 작은 예제(queens)에서 배율이 커집니다.
- **sieve (추정):** 배열 연산 `a.[i]`, `a.[i] <- v`, `!`, `:=`가 내장 함수라서 인자를 모은 뒤 이름으로 동작을 고르는 길을 거칩니다. 따로 재지는 않았습니다.
- **msort (추정):** `merge`가 호출마다 튜플 `(xs, ys)`를 만들고 바로 패턴으로 풉니다. 따로 재지는 않았습니다.

## 측정할 때 주의할 점

- **크기:** 처음에는 `fib 30`, queens 9로 쟀습니다. 이 크기에서는 인터프리터의 시작 비용이 시간의 대부분이었습니다. 실행 시간이 시작 비용보다 충분히 크도록 크기를 정합니다.
- **데우기:** 방금 만든 실행 파일은 처음 실행할 때 macOS가 검사하므로 느립니다. 스크립트는 한 번 실행한 뒤에 잽니다.
- **다른 기계:** 절대 시간은 기계마다 다릅니다. 배율을 봅니다.
