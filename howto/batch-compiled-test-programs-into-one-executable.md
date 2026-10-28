---
created: 2026-10-28
description: 컴파일러 테스트가 예제마다 실행 파일을 만들면 macOS의 첫 실행 검사(직렬 처리), 외부 도구 실행, 프로세스 생성 비용이 예제 수만큼 쌓인다. 예제를 모듈 몇 개로 묶어 실행 파일 하나를 만들고, 그 안의 드라이버가 예제마다 fork해서 출력과 종료 코드를 남기게 하면 90초가 5초가 된다
---

# 컴파일러 테스트 예제를 실행 파일 하나로 묶기

차등 테스트는 예제마다 "컴파일 → 링크 → 실행"을 한다. 예제가 200개를 넘자 `dotnet test`가 1분 30초 걸렸다. 그런데 CPU는 거의 놀고 있었다. 컴파일러가 느린 것이 아니라 **새 실행 파일을 만드는 일 자체**가 비쌌다. 예제를 실행 파일 하나로 묶고, 그 안에서 예제마다 `fork`하도록 바꾸니 5초가 되었다.

## The Insight

**시간이 어디서 드는지 단계별로 재면, 예상과 다른 곳이 나온다.** 직접 잰 결과는 다음과 같다.

| 단계 (예제 하나) | 시간 |
|---|---|
| `mlir-opt` | 0.03초 |
| `mlir-translate` | 0.03초 |
| `clang` (컴파일 + 링크) | 0.12초 |
| **새 실행 파일의 첫 실행** | **0.36~0.49초** |
| 같은 실행 파일의 두 번째 실행 | 0.02초 |

첫 실행 지연은 아무 일도 하지 않는 `hello`에서도 같았다. 게다가 **병렬로 실행해도 줄지 않는다.** 새 `hello` 8개를 동시에 실행하면 2.57초(≈ 8 × 0.32초)가 걸렸다. 같은 8개를 다시 실행하면 0.03초였다. macOS가 새 실행 파일을 하나씩 순서대로 검사한다는 뜻이다. 테스트가 1분 30초 걸리면서 CPU 사용률은 낮았던 이유가 이것이다.

해결책은 **새 실행 파일의 수를 줄이는 것**이다. 예제마다 프로세스가 따로 돌아야 한다는 조건(출력, 실행 중 오류, 종료 코드를 비교해야 함)은 실행 파일 안에서 `fork`로 지킨다.

## Why This Matters

- **병렬화만 늘린 경우:** 예제를 병렬로 컴파일해도 첫 실행 검사는 순서대로 처리되므로 시간이 거의 줄지 않습니다. 이 프로젝트도 `Array.Parallel.map`을 이미 쓰고 있었는데 1분 30초였습니다.
- **예제를 실행 파일 하나에 묶기만 한 경우:** 외부 도구 실행(예제당 3번)과 .NET의 프로세스 생성이 다음 병목이 됩니다. 실제로 쟀더니 예제 200개의 오브젝트를 따로 만드는 데 9.3초, .NET에서 프로세스 200개를 띄우는 데 2.8초가 걸렸습니다.
- **단독 실행 파일 경로를 테스트하지 않게 된 경우:** 사용자가 실제로 쓰는 `calc --compile`(생성한 `@main`, 링크 옵션)이 깨져도 테스트가 통과합니다.

## Recognition Pattern

- 테스트가 예제마다 바이너리를 만들고 실행하는 구조(컴파일러, 코드 생성기, 링커 테스트)일 때
- `time`에서 `real`이 `user + sys`보다 훨씬 클 때 (기다리는 시간)
- macOS에서만 유독 느린 테스트

## The Approach

### Step 1: 단계별로 잰다

외부 도구 각각, 컴파일, 첫 실행, 두 번째 실행을 따로 잰다. 그리고 "병렬로 돌려서 줄어드는가"도 잰다. 줄지 않는 단계가 직렬 병목이다.

```python
# 새 실행 파일 8개를 병렬로 → 2.57초, 다시 → 0.03초
with ThreadPoolExecutor(8) as ex: list(ex.map(lambda i: subprocess.run([f'./h{i}']), range(1, 9)))
```

### Step 2: 여러 프로그램을 MLIR 모듈 하나에 넣는다

프로그램마다 진입 함수 이름(`calc_entry_7`)을 다르게 하고, 그 프로그램의 심볼(람다, 문자열 상수) 앞에 접두어를 붙인다. 공통 선언(`llvm.func @calc_alloc(i64) ...`)은 모듈에 한 번만 둔다.

```fsharp
let emitEntries (programs: (string * Ast.Expr) list) : string =
    let sb = StringBuilder()
    declarations |> List.iter (fun s -> sb.AppendLine(s) |> ignore)
    for entry, expr in programs do
        emitBody sb (entry + "_") entry expr        // calc_entry_7_lambda_0, calc_entry_7_str_0
    sb.ToString()
```

람다 함수는 `llvm.func internal`로 만든다. 그러면 모듈 밖에서 보이지 않아서 다른 모듈의 같은 이름과 링크할 때 충돌하지 않고, LLVM이 펼치거나 지우기도 쉬워진다.

### Step 3: 모듈을 코어 수만큼으로 나눠 병렬로 컴파일한다

모듈 하나로 다 묶으면 `clang -O2` 하나가 오래 걸린다. 예제마다 모듈을 따로 만들면 외부 도구가 600번 뜬다. 그래서 **코어 수만큼의 묶음**으로 나눈다.

```fsharp
let chunkSize = max 1 ((programs.Length + Environment.ProcessorCount - 1) / Environment.ProcessorCount)
let modules = programs |> List.chunkBySize chunkSize |> List.map Codegen.emitEntries
```

### Step 4: 드라이버가 예제마다 fork하고 결과를 파일로 남긴다

C 드라이버를 생성해서 함께 링크한다. `--all <폴더>` 모드에서는 예제마다 `fork`하고, 자식은 stdout과 stderr를 파일로 돌린 뒤 그 예제만 실행한다. 부모는 종료 상태를 셸과 같은 종료 코드로 기록한다(신호로 끝나면 128 + 신호 번호).

```c
for (int i = 0; i < count; i++) {
    fflush(NULL);                              /* 부모 버퍼가 자식에 복제되지 않게 */
    pid_t pid = fork();
    if (pid == 0) {
        freopen(".../i.out", "w", stdout);
        freopen(".../i.err", "w", stderr);
        calc_init();                           /* GC 초기화는 자식에서 */
        calc_print_result(entries[i]());
        exit(0);                               /* calc_error 는 exit(1) */
    }
    int status = 0;
    waitpid(pid, &status, 0);
    int code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
    /* i.code 에 기록 */
}
```

`fork`한 자식은 메인 스레드를 그대로 이어받는다. 그래서 링크 옵션으로 키운 512MB 스택에서 깊이 100만 재귀도 그대로 통과한다. 테스트는 이 실행 파일을 **한 번** 띄우고 파일들을 읽는다. 새 실행 파일은 하나, .NET 프로세스 생성도 한 번이다.

### Step 5: 테스트를 데이터로 바꿔 입력을 미리 모은다

묶음을 만들려면 실행 전에 모든 입력을 알아야 한다. 그래서 `Expect.equal (compileAndRun "...") ...`가 흩어져 있던 테스트를 표로 바꾼다.

```fsharp
let private runCases =
    [ "나눗셈은 0 쪽으로 버림",
        [ "7 / -2", ("-3\n", "", 0), ""
          "-7 / 2", ("-3\n", "", 0), "" ]
      ... ]

let private batch = lazy (...)       // runCases 의 입력 + 차등 테스트 입력을 한 번에 빌드·실행
```

묶음에 없는 입력이 들어오면 예전처럼 따로 컴파일하도록 대체 경로를 남긴다.

### Step 6: 테스트가 여전히 실패할 수 있는지 확인한다

빨라진 테스트가 실제로 비교하고 있는지, **일부러 틀린 기대값**을 넣어 확인한다. 실행 테스트 하나와 차등 테스트 하나를 틀리게 바꿨더니 정확히 두 개가 실패했다. 확인한 뒤에는 원래대로 되돌린다.

### Step 7: 단독 실행 파일 경로를 따로 지킨다

묶음 실행 파일은 드라이버의 `main`을 쓴다. `calc --compile`이 만드는 단독 실행 파일(생성한 `@main`)을 거치는 테스트를 몇 개 남긴다. 결과 출력, 실행 중 오류, 깊은 재귀를 확인한다.

## Example

| | 전 | 후 |
|---|---|---|
| 새 실행 파일 수 | 약 220개 | 묶음 1개 + 단독 3개 |
| 외부 도구 실행 | 약 660번 | 약 50번 (코어 16개) |
| .NET 프로세스 생성 | 약 220번 | 4번 |
| `Compile` 테스트 | 45초 | 1.4초 |
| 전체 `dotnet test` | 1분 30초 | 약 5초 |

## 체크리스트

- [ ] 단계별 시간을 쟀고, 병렬로 줄지 않는 단계를 찾았는가?
- [ ] 묶은 프로그램끼리 심볼이 겹치지 않는가? (접두어, `internal`)
- [ ] 예제마다 프로세스가 따로인가? (`fork`, 출력·종료 코드 분리)
- [ ] 자식에서 런타임을 초기화하고, `fork` 전에 버퍼를 비우는가?
- [ ] 일부러 틀린 기대값으로 테스트가 여전히 실패하는지 확인했는가?
- [ ] 사용자가 쓰는 단독 실행 파일 경로를 테스트하는가?

## 관련 문서

- `measure-fresh-binaries-on-macos.md` - 새 실행 파일의 첫 실행 지연
- `write-differential-tests-for-compiler.md` - 인터프리터와 비교하는 차등 테스트
- `run-external-toolchain-from-dotnet.md` - 외부 도구 실행
