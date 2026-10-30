# 세대 구분 GC 설계

컴파일러가 만든 실행 파일의 메모리 관리를 Boehm GC에서 직접 만든 세대 구분 GC로 바꾸는 설계입니다. 조사(지금 비용의 측정, 방식별 C 모델, 제약)와 추천 설계, 단계별 계획, 1단계(C 모델에 붙인 collector)의 결과를 담습니다. 런타임에는 아직 넣지 않았습니다.

- **요약:** stack과 레지스터는 지금처럼 conservative하게 보고, 힙은 정확하게(precise) 보는 **mostly-copying 세대 구분 GC**를 추천합니다.
  - 젊은 객체는 작은 nursery에 bump 할당합니다. minor GC가 살아남은 것을 old space로 복사합니다.
  - stack이 가리키는 nursery 블록은 옮기지 않고 그 자리에서 old space로 넘깁니다(pinning).
  - old space는 옮기지 않는 mark-sweep입니다.
  - 생성 코드는 값을 바꾸는 세 곳(`:=`, 배열 쓰기, `let rec … and …`의 클로저 칸 채우기)에 write barrier만 더합니다. stack map은 필요 없습니다.
- **기대 효과:** 할당이 많은 프로그램에서 Boehm이 쓰는 시간(지금 36~79%)의 상당 부분입니다. 같은 일을 하는 C 모델에서 수거 없는 bump 할당이 상한이고, msort 모델은 0.48초에서 0.21초, 짧게 사는 할당이 많은 모델은 0.28초에서 0.07초였습니다.
- **규모:** C 런타임에 1,500~2,500줄 정도의 collector, Codegen에 write barrier, 테스트용 GC stress 모드입니다. 먼저 C 모델에 collector를 붙여 효과를 확인한 뒤(1단계) 런타임에 넣습니다.
- **1단계 결과 (11장):** 530줄 남짓한 collector(`benchmarks/gc-model/gcm.c`)를 C 모델 다섯에 붙였습니다. 다섯 모두 Boehm보다 빨랐습니다(msort 0.50초 → 0.37초, live 0.34초 → 0.12초, bst 1.06초 → 0.91초). 최대 메모리는 bst만 조금 늘었습니다. msort는 목표(0.33초)에 조금 못 미쳤습니다.

## 1. 지금 상태

### 1.1 Boehm이 쓰는 시간

할당이 많은 Calc 프로그램 넷을 컴파일해 `sample`로 3초 동안 재고, 함수 이름으로 나눴습니다(0.64.0, Apple M4 Max). mark는 `GC_mark_from`, `GC_push_*`, sweep은 `GC_reclaim_*`, `GC_build_fl`, 할당은 `calc_alloc`, `GC_allochblk*`, `GC_malloc*`입니다. `calc_alloc` 자체의 시간은 대부분 free list에서 꺼낸 블록의 첫 접근(cache miss)입니다.

| 프로그램 | 시간 | 최대 메모리 | mark | sweep | 할당 | 프로그램 자신 |
|---|---|---|---|---|---|---|
| bst: 영속 트리에 백만 개 넣고 백만 번 찾기 | 0.94초 | 108MB | 27% | 7% | 14% | 49% |
| msort: 리스트 2백만 개 merge sort | 0.53초 | 316MB | 37% | 13% | 28% | 20% |
| pipe: 백만 개 리스트에 `filter`, `map`, `fold`를 100번 | 1.05초 | 108MB | 12% | 4% | 20% | 60% |
| live: 2백만 개를 붙잡은 채 `Some (n, [n])` 2천만 번 | 0.35초 | 404MB | 49% | 14% | 16% | 16% |

- **mark가 큰 곳:** live와 msort처럼 오래 사는 데이터가 클 때입니다. Boehm은 수거할 때마다 살아 있는 힙 전체를 다시 mark합니다. 세대 구분 GC가 없애려는 비용이 이것입니다.
- **할당이 큰 곳:** msort와 pipe입니다. free list의 블록은 힙 여기저기에 흩어져 있어 처음 쓸 때 cache miss가 납니다. 작은 nursery에 bump 할당하면 같은 메모리를 다시 쓰므로 cache에 남아 있습니다.

### 1.2 C 모델로 잰 상한

같은 일을 하는 C 프로그램(`msortm`, `livem`, `bstm`)에서 할당기만 바꿨습니다. 값은 Calc와 같은 tagged 표현과 블록 크기입니다.

| C 모델 | 지금 런타임과 같은 Boehm 설정 | Boehm generational (manual VDB) | Boehm generational, 시간 제한 없음 | bump 할당, 수거 없음 (상한) |
|---|---|---|---|---|
| msortm (cons 2백만 개 정렬) | 0.48초, 298MB | 2.62초, 403MB | 0.85초, 403MB | 0.21초, 1.7GB |
| livem (2백만 개를 붙잡은 채 2천만 번 할당) | 0.28초, 420MB | 1.05초, 272MB | 0.37초, 272MB | 0.07초, 1.0GB |
| bstm (영속 트리 백만 개) | 1.07초, 108MB | 2.30초, 264MB | 1.42초, 264MB | 0.89초, 2.4GB |

- **지금 설정:** `GC_malloc_many`로 받아 둔 free list에서 나눠 주고, interior pointer를 끄고, free space divisor 1입니다(`docs/compiler-design.md` D5).
- **Boehm generational:** `GC_set_manual_vdb_allowed(1)`과 `GC_enable_incremental()`로 켰습니다. 모델에는 값을 바꾸는 곳이 없어서 write barrier가 없어도 맞습니다. 시간 제한을 풀고(`GC_set_time_limit(GC_TIME_UNLIMITED)`) free list를 써도 지금 설정보다 느렸습니다. 옮기지 않는 generational 모드는 할당이 free list 그대로이고, 수거마다 sweep도 그대로이기 때문으로 봅니다. 이 방식은 후보에서 뺍니다.
- **bump 상한:** 수거를 하지 않으므로 메모리가 계속 늘어납니다. 시간만 상한으로 봅니다. bst처럼 프로그램 자신의 시간(포인터를 따라가는 탐색)이 큰 곳은 상한도 작습니다.

## 2. GC가 알아야 하는 것

### 2.1 값과 포인터 구별

값은 64비트 하나이고 하위 3비트가 태그입니다(`docs/compiler-design.md` D3).

| 태그 | 값 | 포인터인가 |
|---|---|---|
| `1` | int `(n << 3) \| 1` | 아님 |
| `3` | bool `3`, `11` | 아님 |
| `5`, `7` | `[]`, `()` | 아님 |
| `6` | 인자 없는 생성자 `(번호 << 32) \| 6` | 아님 |
| `2` | cons: 헤더 없는 16바이트 `[head, tail]` 블록의 주소 + 2 | 포인터 |
| `0` | 그 밖의 블록: `[헤더, …]`의 주소 | 포인터 |

- **힙 안의 단어는 모두 값이거나 헤더입니다.** 그래서 힙은 정확하게 볼 수 있습니다. 태그가 0이나 2이고 GC 힙 범위 안을 가리키는 단어만 포인터입니다.
- **힙 범위 밖을 가리키는 태그 0 단어:** 클로저의 코드 포인터와 direct 함수 포인터, 레코드의 필드 이름(C 문자열 전역), 문자열 상수(전역)입니다. 범위 검사로 걸러지므로 따로 표시할 필요가 없습니다.
- **안을 보지 않는 블록:** 문자열(`2`)과 bool 배열(`9`)은 안에 포인터가 없습니다. 지금도 `GC_MALLOC_ATOMIC`입니다.
- **블록 크기:** 헤더의 단어 수로 압니다. 종류마다 읽는 법이 다릅니다. data 블록은 위쪽 32비트에 생성자 번호가 있어 `CALC_DATA_SIZE`로 가리고, bool 배열의 단어 수 자리는 바이트 수입니다. cons는 헤더가 없지만 포인터의 태그(2)가 16바이트임을 알려 줍니다. 그러므로 **포인터 하나만 보고 그 객체의 크기를 알 수 있습니다.**
- **블록을 처음부터 차례로 읽을 수는 없습니다.** cons에 헤더가 없어서, 블록 안에 cons와 다른 객체가 섞이면 다음 객체가 어디서 시작하는지 모릅니다. 그래서 cons는 따로 모읍니다(5.1).

### 2.2 root

| root | 지금 | 정확하게 볼 수 있나 |
|---|---|---|
| 생성 코드의 stack 프레임과 레지스터 | Boehm이 conservative하게 훑음 | 아니요. stack map이 없음 |
| C 런타임의 지역 변수 (`builtins.c`의 반복, `list_builder`) | 같음 | 아니요 |
| `calc_try`의 `jmp_buf` (stack 위, 레지스터를 저장) | 같음 | 아니요 |
| 런타임 전역: `calc_raised`, `calc_handlers` | Boehm이 data 영역을 훑음 | 예. 직접 등록 |
| 문자열 상수, 생성자 이름 | GC 대상 아님 | 해당 없음 |

생성 코드의 값은 SSA 값이라서 LLVM이 레지스터나 stack slot 어디에 둘지 정합니다. `tailcc` tail call과 `inttoptr`/`getelementptr`로 만든 derived pointer(배열 원소 주소)도 있습니다. stack map 없이 이 값들을 정확하게 찾을 방법은 없습니다. **stack과 레지스터는 conservative하게 보고, 가리켜진 객체는 옮기지 않는다**는 것이 설계의 출발점입니다.

### 2.3 GC가 일어나는 곳

GC는 할당할 때만 일어납니다. 할당 함수는 모두 C 런타임에 있습니다(`calc_alloc`, `calc_new_string`, `new_array`, `calc_cons`). 생성 코드는 `calc_alloc`을 부릅니다. 할당 함수 안에서 `setjmp`(또는 `__builtin_unwind_init`)로 레지스터를 stack에 내리고, 지금 stack 끝부터 stack 시작까지 훑습니다. Boehm이 하는 일과 같습니다. 프로그램은 스레드 하나입니다.

### 2.4 값을 바꾸는 곳 (write barrier가 필요한 곳)

이미 있는 객체에 포인터를 쓰는 곳입니다. 세대 구분 GC는 오래된 객체가 젊은 객체를 가리키게 되는 곳을 모두 알아야 합니다.

| 곳 | 파일 | 무엇을 씀 |
|---|---|---|
| `r := v` | `Codegen.fs` (`:=`), 런타임 없음 | ref 칸에 값 |
| `a.[i] <- v` | `Codegen.fs` (단어 배열), `runtime.c`의 `calc_array_set` | 배열 원소에 값. bool 배열은 포인터가 아니라 필요 없음 |
| `let rec f … and g …` | `Codegen.fs`의 `emitRecGroup` | 클로저를 모두 만든 뒤 서로의 칸을 채움. 그 사이의 할당에서 GC가 일어날 수 있음 |
| 리스트를 앞에서부터 만듦 | `builtins.c`의 `push`, `range`(`TAIL(last) = cell`) | 앞서 만든 칸의 tail |
| `Array.create n v` | `builtins.c` | 새 배열을 채움. 큰 배열을 old space에 바로 만들면 필요 |

새로 만든 객체를 채우는 쓰기(블록 초기화)는 GC보다 먼저 일어나므로 barrier가 필요 없습니다. 위 표 말고는 이미 있는 객체를 바꾸는 곳이 없습니다. 레코드, 튜플, 리스트, 생성자는 바꿀 수 없는 값입니다.

### 2.5 그 밖의 성질

- **깊은 재귀:** stack이 512MB까지 자랍니다(`-stack_size`). tail call이 아닌 재귀(백만 단계)에서는 minor GC마다 깊은 stack을 훑어야 합니다(8장).
- **큰 배열:** 1억 원소(800MB) 배열이 있습니다. 원소 하나를 바꿀 때 배열 전체를 다시 훑으면 안 됩니다.
- **atomic 블록:** 문자열과 bool 배열은 크기가 크고 안을 보지 않습니다.

## 3. 방식 조사

| 방식 | 생성 코드 | 런타임 | 효과 (예상) | 위험과 비용 |
|---|---|---|---|---|
| A. 정확한 copying 세대 구분 (LLVM statepoint와 stack map) | 값을 `ptr addrspace(1)`로 바꾸고 `gc "statepoint-example"`. tagged 포인터의 `ptrtoint`/`inttoptr` 연산을 RS4GC가 추적할 수 있는 모양으로 모두 바꿈 | stack map을 읽는 collector | 가장 큼. 모든 객체를 옮길 수 있음 | 생성 코드 전체를 다시 씀. cons의 `주소 + 2`와 derived pointer, `tailcc`와 statepoint의 조합을 MLIR LLVM dialect에서 검증해야 함 |
| B. 정확한 copying 세대 구분 (shadow stack) | GC가 일어날 수 있는 호출마다 살아 있는 값을 프레임의 root 배열에 쓰고 다시 읽음. liveness 분석 필요 | 간단한 copying collector | 큼 | 호출마다 저장과 읽기가 붙어 프로그램 자신이 느려짐. `scf` region 안의 liveness, tail call 전에 프레임 정리 |
| **C. mostly-copying 세대 구분 (conservative stack, precise heap, pinning)** | write barrier 세 곳 | nursery, minor GC(복사), old space(mark-sweep), pinning | 큼. 할당은 bump, mark는 젊은 객체만 | pinning으로 nursery 블록이 old space로 넘어감(조각). 런타임 C 코드는 그대로 안전함 |
| D. Boehm generational (manual VDB) | write barrier (`GC_end_stubborn_change`) | 설정만 | **나쁨** (1.2절: 0.48초 → 0.85초) | 제외 |
| E. 옮기지 않는 세대 구분 (sticky mark bits, Immix 계열) | write barrier | line 단위 bump 할당, mark bit를 남겨 젊은 객체만 mark | 중간~큼. 복사가 없음 | 객체 시작 bitmap, line mark, 조각 관리. 살아남은 객체가 모이지 않아 cache에 덜 유리 |
| F. Boehm 설정 다듬기 | 없음 | 설정 | 작음. 이미 free list, divisor 1, interior pointer 끔 | 남은 여지가 적음 |

- **A가 어려운 이유:** LLVM의 statepoint는 GC 포인터를 `addrspace(1)` 포인터 타입으로 구별합니다. 이 컴파일러의 값은 `i64`이고, 포인터와 정수를 태그로 구별합니다. `(주소 | 2)` 같은 태그 연산은 정수 연산이라 statepoint가 따라가지 못합니다. 값 표현과 Codegen을 거의 모두 바꿔야 합니다.
- **B가 비싼 이유:** 이 언어의 함수는 작고 호출이 많습니다(클로저 호출, 런타임 호출). 호출마다 살아 있는 값을 메모리에 쓰고 다시 읽으면, GC 비용을 줄인 만큼 프로그램 자신이 느려질 수 있습니다.
- **C를 고른 이유:**
  - 생성 코드를 거의 바꾸지 않습니다. stack을 지금처럼 conservative하게 보므로 LLVM이 값을 어디에 두든 상관없습니다.
  - stack이 가리키는 객체는 옮기지 않으므로, C 런타임이 블록 포인터를 지역 변수에 들고 할당해도 안전합니다(지금 Boehm과 같은 성질).
  - stack이 가리키는 nursery 객체는 minor GC 때 살아 있는 프레임이 직접 쥔 것뿐이라 적습니다. 나머지(대부분)는 옮길 수 있습니다.
  - 같은 구조의 collector가 실제로 쓰입니다. Bartlett의 mostly-copying collector와 Mono의 SGen(conservative stack, pinning, copying nursery, mark-sweep old space)이 있습니다.
- **E와 비교:** E도 좋은 후보입니다. C는 살아남은 객체를 old space에 모으고 nursery를 작게 다시 쓰므로 cache에 유리합니다. E는 복사가 없지만 line 단위 bump와 객체 시작 bitmap이 필요합니다. 1단계 C 모델에서 C의 효과가 기대에 못 미치면 E를 다시 봅니다.

## 4. 추천 설계: 구조

```
            할당 (bump)                minor GC: 살아남은 것을 복사         major GC
 생성 코드 ───────────► nursery ─────────────────────────────────► old space ◄──── mark-sweep (옮기지 않음)
 런타임               (블록 n개)    stack 이 가리키는 블록은 그 자리에서 ─┘
                                    old space 로 넘김 (pinning)
                                                                   large object space (큰 배열, 큰 문자열)
```

| 공간 | 할당 | 수거 | 객체를 옮기나 |
|---|---|---|---|
| nursery | 블록(32KB) 안에서 bump. cons 블록과 그 밖의 블록을 따로 | minor GC | 예 (pin 된 블록은 아니오) |
| old space | 크기별 블록(16, 24, 32, … 바이트)의 free list. 복사해 온 객체와 큰 블록이 아닌 것 | major GC: mark 후 sweep | 아니오 |
| large object space | 2KB(256단어)보다 큰 객체. `mmap`으로 따로 | major GC | 아니오 |
| 넘겨받은 nursery 블록 | 없음 (pin 된 채 old space가 됨) | major GC | 아니오 |

### 4.1 nursery

- **크기:** 16MB(블록 512개)입니다(1단계, 11장). 4MB보다 살아남는 비율이 낮아 복사가 줄었습니다. stack이 크면 다음 nursery를 훑은 stack의 2배로 키웁니다.
- **두 bump 영역:** cons 블록(16바이트 객체만)과 그 밖의 블록입니다. 블록 안의 객체를 차례로 읽을 수 있어야 pin 된 블록을 훑을 수 있습니다(2.1). 그 밖의 블록은 헤더로, cons 블록은 16바이트씩 읽습니다.
- **할당 경로:** `calc_alloc`이 `if (top + size <= limit) { p = top; top += size; return p; }`입니다. 블록이 차면 다음 블록, nursery가 차면 minor GC입니다. 0.53.0 조사에서 할당 함수를 부르는 비용은 병목이 아니었으므로 처음에는 생성 코드에 펼쳐 넣지 않습니다.
- **블록 표:** 블록마다 상태(nursery, pin 되어 old가 됨, 비어 있음)를 둡니다. `is_young(p)`는 "nursery 주소 범위 안이고, 그 블록의 상태가 nursery"입니다.

### 4.2 minor GC

1. **root에서 pin 할 블록 찾기:** 레지스터를 stack에 내리고, stack의 모든 단어를 봅니다. 태그와 상관없이 nursery 범위 안이면 그 블록을 pin 합니다. derived pointer와 interior pointer도 블록을 알려 주므로 괜찮습니다. 블록 끝 바로 다음을 가리키는 포인터(반복의 끝 주소)는 그 앞 블록도 pin 합니다.
2. **pin 한 블록 넘기기:** pin 한 블록은 상태를 old로 바꿉니다. 그 안의 객체는 살아 있는 것으로 보고 차례로 훑습니다(아래 4번의 회색 객체).
3. **remembered set:** write barrier가 기록한 칸(old 객체의 칸 주소)을 root로 봅니다.
4. **복사 (Cheney 방식, 회색 객체 목록):** root(remembered set, pin 한 블록의 객체, 런타임 전역)의 각 칸이 nursery 객체를 가리키면 그 객체를 old space로 복사합니다. 원래 자리에 forwarding pointer를 남기고 칸을 새 주소로 고칩니다. cons는 `새 주소 | 2`로 고칩니다. 복사한 객체는 회색 객체 목록에 넣고, 목록이 빌 때까지 그 칸들도 같은 일을 합니다.
5. **nursery 비우기:** pin 하지 않은 블록은 모두 빈 블록이 됩니다. pin 해서 넘긴 블록 수만큼 새 블록을 받아 nursery 크기를 지킵니다.

- **forwarding pointer:** 헤더가 있는 블록은 헤더 자리에, cons는 head 자리에 `새 주소 | 0xF`를 씁니다. 새 주소는 16바이트 정렬이라 하위 4비트가 비어 있습니다. 하위 4비트 `0xF`는 값(int는 `1`이나 `9`, bool은 `3`이나 `0xB`, 포인터는 `0`, `8`, `2`, `0xA`, 그 밖의 즉시값은 `5`, `6`, `7`, `0xE`)에도 헤더의 종류(1~9)에도 나오지 않습니다. 태그 `4`는 튜플 헤더(종류 4)와 겹쳐서 쓸 수 없습니다.
- **복사하는 객체의 크기:** 2.1의 규칙대로 포인터 하나로 압니다.
- **atomic 객체:** 문자열은 복사만 하고 안을 보지 않습니다.

### 4.3 old space와 major GC

- **할당:** minor GC가 복사해 오는 객체와 2KB가 넘는 객체입니다. 크기별 블록의 free list입니다.
- **major GC 시작:** old space가 지난 major GC 뒤 살아 있던 크기의 2배가 되면 시작합니다. 지금 `GC_set_free_space_divisor(1)`과 같은 정책입니다.
- **mark:** root(stack은 conservative, 런타임 전역)에서 시작해 힙을 정확하게 따라갑니다. 객체마다 mark bit를 블록의 bitmap에 둡니다. stack의 포인터가 old space 블록 가운데를 가리키면 크기별 블록은 `(p - 블록 시작) / 크기`로 객체 시작을 찾습니다. 넘겨받은 nursery 블록은 처음부터 차례로 읽어 찾습니다.
- **sweep:** mark 되지 않은 객체를 free list로 돌립니다. 다음 할당이 그 블록을 쓸 때 sweep 해도 됩니다(lazy sweep). 넘겨받은 nursery 블록은 살아 있는 객체가 하나도 없을 때만 통째로 돌려줍니다.
- **major GC 전에 minor GC:** major GC는 nursery를 비운 뒤 시작합니다. 그러면 mark가 nursery를 볼 필요가 없습니다.

### 4.4 write barrier

```c
/* 값을 바꾼 뒤: 오래된 객체의 칸에 젊은 객체를 썼으면 칸 주소를 기록 */
static inline void calc_write(value *slot, value v) {
    *slot = v;
    if (CALC_IS_POINTER(v) && calc_is_young(v) && !calc_is_young((value)slot)) calc_remember(slot);
}
```

- **빠른 길:** 쓰는 값이 int, bool 같은 즉시값이면 태그 검사 하나로 끝납니다. 체와 `incr`처럼 int를 쓰는 반복은 거의 그대로입니다. 타입 추론이 int로 정한 `:=`에는 barrier를 아예 내지 않을 수 있습니다(0.54.0의 `Infer.intOperations`와 같은 방식).
- **remembered set:** 칸 주소의 배열(sequential store buffer)입니다. 꽉 차면 minor GC를 합니다. 같은 칸이 여러 번 들어가도 결과는 같습니다.
- **큰 배열:** 칸 주소를 기록하므로 배열 전체를 훑지 않습니다.
- **Codegen:** `:=`, 단어 배열 쓰기, `emitRecGroup`의 칸 채우기에서 store 뒤에 위 검사를 펼쳐 넣습니다. 느린 길만 런타임 함수(`calc_remember`)를 부릅니다.
- **런타임:** `push`, `range`, `calc_array_set`, `Array.create`(old space에 바로 만든 큰 배열)에서 `calc_write`를 씁니다.

### 4.5 바뀌는 것과 그대로인 것

| 곳 | 바뀌는 것 |
|---|---|
| `runtime.c` | 새 collector(`gc.c`로 나눔). `calc_alloc`, `calc_new_string`, `new_array`가 새 할당기를 부름. `calc_init`이 nursery와 블록 표를 만듦 |
| `builtins.c` | `GC_MALLOC`, `GC_MALLOC_ATOMIC`를 새 할당 함수로. `push`, `range`, `calc_array_set`에 barrier |
| `Codegen.fs` | write barrier 세 곳. `CCons`가 cons 전용 할당 함수를 부름. 선언에 `calc_remember`, `calc_alloc_cons` |
| `Toolchain.fs` | `-lgc`와 Boehm 경로를 뺌. 실행 파일이 `libgc.1.dylib`에 의존하지 않게 됨(`docs/compiler-architecture.md` 5장의 한계 하나가 없어짐) |
| 값 표현 | 바뀌지 않음. cons의 헤더 없는 16바이트 블록도 그대로 |
| Lower, Infer, 인터프리터 | 바뀌지 않음 |

## 5. 세부 결정

### 5.1 cons를 따로 모으는 이유

pin 한 nursery 블록은 안의 객체를 모두 훑어야 합니다. 헤더 없는 cons가 다른 객체와 섞여 있으면 블록을 차례로 읽을 수 없습니다. 방법은 셋입니다.

| 방법 | 비용 |
|---|---|
| cons에 헤더를 다시 붙임 | 리스트 메모리가 1.5배 (0.53.0에서 없앤 것) |
| 객체 시작 bitmap을 할당마다 씀 | 할당마다 bit 쓰기 하나 |
| **cons만 담는 블록을 따로 둠** | bump 영역이 둘. 할당 함수가 크기로 고름(`calc_cons`는 따로 있음) |

세 번째를 고릅니다. cons를 만드는 곳은 런타임의 `calc_cons`와 Codegen의 `CCons` 한 곳뿐입니다. 지금 `CCons`는 다른 16바이트 블록(`Some x`)과 같은 `calc_alloc(16)`을 부르므로, cons 전용 할당 함수(`calc_alloc_cons`)를 부르게 바꿉니다.

### 5.2 conservative stack이 하는 일

- **pinning만 합니다.** stack의 단어는 절대 고치지 않습니다. int가 우연히 nursery 주소처럼 보이면 그 블록을 쓸데없이 old space로 넘길 뿐입니다(false retention과 같은 종류의 손해).
- **태그를 보지 않습니다.** derived pointer(배열 원소 주소)는 태그가 0이 아닐 수 있고, C 런타임의 `calc_string *` 지역 변수도 있습니다. 범위만 봅니다.

### 5.3 정해야 할 수

| 수 | 처음 값 | 정하는 법 |
|---|---|---|
| nursery 크기 | 16MB, stack이 크면 훑은 stack의 2배 | 1단계에서 정함 (11장) |
| nursery 블록 크기 | 32KB | pin 하나가 넘기는 양. 작으면 블록 표가 커짐 |
| large object 경계 | 2KB | old space 블록 크기의 1/16 |
| major GC 시작 | 지난 major GC 뒤 살아 있던 크기의 3배 (64MB 이상) | 1단계에서 정함 (11장) |
| remembered set 크기 | 64K 칸 | 꽉 차면 minor GC |

## 6. 검증

- **GC stress 모드:** 환경 변수(`CALC_GC_STRESS=1`)로 할당마다 minor GC, n번마다 major GC를 합니다. 전체 컴파일 테스트를 이 모드로 한 번 더 돌립니다. barrier나 root를 빠뜨리면 여기서 결과가 달라지거나 죽습니다.
- **heap verifier:** stress 모드에서 GC마다 확인합니다.
  - old 객체의 모든 칸이 old 객체나, remembered set에 있는 칸을 통해서만 nursery 객체를 가리키는지
  - 모든 포인터가 살아 있는 객체의 시작(cons는 + 2)을 가리키는지
  - forwarding 표시가 남은 객체가 없는지
- **차등 테스트:** 기존 컴파일 테스트(인터프리터의 출력과 비교)가 그대로 통과해야 합니다. 새 테스트는 값을 바꾸는 곳마다 old→young 포인터를 만드는 프로그램입니다. 예를 들면 오래 산 ref와 배열에 새 리스트 쓰기, `let rec … and …` 중간의 수거, `range`와 `split` 중간의 수거입니다.
- **벤치마크:** 1장의 프로그램 넷과 `benchmarks/` 다섯 예제, 체 1억, 깊은 재귀(백만 단계)의 시간과 최대 메모리를 Boehm과 비교합니다.

## 7. 단계별 계획

| 단계 | 할 일 | 끝나는 조건 |
|---|---|---|
| 1. C 모델 (끝남, 11장) | C 모델에 collector를 붙임(nursery, minor GC, pinning, old space, major GC, write barrier) | Boehm 대비 msortm 0.33초 이하, livem 0.12초 이하, bstm 1.0초 이하. 최대 메모리는 Boehm의 1.5배 이하. 아니면 3장의 E를 다시 봄 |
| 2. 런타임에 넣기 (끝남, 12장) | 1단계의 collector를 `runtime/gc.c`로. 할당 함수, 런타임 전역 root 등록, 런타임과 Codegen의 barrier(포인터일 때 런타임 호출). 빌드 스위치 `CALC_GC=gen`, 기본은 Boehm | 전체 테스트가 두 모드와 GC stress 모드에서 통과 |
| 3. barrier 다듬기 | 빠른 길을 생성 코드에 펼쳐 넣기, int로 정해진 `:=`와 배열 쓰기는 barrier 없이 | MLIR 테스트, 측정 |
| 4. 측정과 조정 | nursery 크기, major GC 정책, 깊은 stack(8장) | 1장의 프로그램이 Boehm보다 빠르고, `benchmarks/`가 느려지지 않음 |
| 5. 기본값으로 | Boehm 경로와 `-lgc`를 뺌. 문서(`compiler-design.md` D5, `compiler-architecture.md`) | 릴리스 |

단계마다 릴리스할 수 있게 나눴습니다. 2단계까지는 새 collector가 선택 사항이라 기존 동작에 영향이 없습니다.

## 8. 위험과 대책

| 위험 | 결과 | 대책 |
|---|---|---|
| write barrier를 빠뜨림 | 살아 있는 젊은 객체를 버림. 드물게, 늦게 드러남 | 값을 바꾸는 곳이 적음(2.4). GC stress와 heap verifier. 새로 바꾸는 곳을 만들 때의 체크리스트 |
| 깊은 stack을 minor GC마다 훑음 | tail call이 아닌 재귀 백만 단계(stack 수십 MB)에서 minor GC마다 그만큼 읽어 느려짐 | 1단계에서 확인함: 다음 nursery를 훑은 stack의 2배로 키우면 Boehm보다 빠름(11장) |
| pinning이 많음 | nursery 블록이 old space로 많이 넘어가 메모리가 늘어남 | 블록 크기를 작게. 넘겨받은 블록의 살아 있는 비율을 측정 |
| 정수가 nursery 주소처럼 보임 | 블록 하나를 쓸데없이 넘김 | stack은 태그를 보지 않으므로(5.2) 피할 수 없음. 손해는 블록 하나이고 다음 major GC에서 돌아옴 |
| C 런타임의 숨은 포인터 | `malloc`한 버퍼에 값을 넣으면 GC가 보지 못함 | 지금 `malloc` 버퍼(`calc_buffer`)에는 UTF-16만 있음. 새 코드에서 금지 |
| 복사 비용이 큼 | 살아남는 비율이 높으면(msort) 복사가 mark보다 비쌀 수 있음 | 1단계에서 측정: msort 모델이 494MB를 복사하고도 Boehm보다 26% 빠름. nursery를 키우면 줄어듦(64MB에서 0.33초) |
| pin 한 블록이 쌓임 | 블록 안의 객체가 모두 죽어야 돌려주므로, nursery가 아주 작으면 메모리가 늘어남(bst 모델, nursery 64KB에서 1.7GB) | nursery를 16MB 아래로 내리지 않음. 블록 크기를 줄이거나, 넘겨받은 블록의 빈 곳을 old space 할당에 다시 쓰는 것은 나중에 |

## 9. 정해야 할 것

- **Boehm 경로를 언제까지 남길지:** 5단계에서 빼는 것으로 적었습니다. 남기면 비교와 문제 확인에 좋지만 런타임에 두 경로가 생깁니다.
- **1단계의 끝나는 조건:** 7장의 수는 bump 상한(1.2절)과 지금 값의 중간쯤으로 잡았습니다. 더 높게 잡으면 E를 같이 시험합니다.
- **생성 코드에 할당 펼쳐 넣기:** bump 할당은 몇 줄이라 펼쳐 넣을 수 있습니다. 0.53.0에서는 효과가 없었지만, bump가 되면 다시 잴 가치가 있습니다. 4단계에서 봅니다.

## 10. 참고

- Joel F. Bartlett, "Compacting Garbage Collection with Ambiguous Roots", DEC WRL Research Report 88/2, 1988. conservative root와 pinning, 페이지 단위 promotion
- Mono SGen garbage collector 문서. conservative stack, pinning, copying nursery, mark-sweep old space
- Stephen M. Blackburn, Kathryn S. McKinley, "Immix: A Mark-Region Garbage Collector with Space Efficiency, Fast Collection, and Mutator Performance", PLDI 2008
- Rifat Shahriyar, Stephen M. Blackburn, Kathryn S. McKinley, "Fast Conservative Garbage Collection", OOPSLA 2014. conservative root에서의 Immix
- Alan Demers et al., "Combining Generational and Conservative Garbage Collection: Framework and Implementations", POPL 1990. sticky mark bits
- Fergus Henderson, "Accurate Garbage Collection in an Uncooperative Environment", ISMM 2002. shadow stack
- LLVM, "Garbage Collection Safepoints in LLVM" (statepoint, RewriteStatepointsForGC)
- Boehm GC `gc.h`: `GC_set_manual_vdb_allowed`, `GC_enable_incremental`, `GC_end_stubborn_change`
- OCaml의 minor heap과 `caml_modify` (remembered set과 write barrier의 예)

## 11. 1단계 결과: C 모델에 붙인 collector

`benchmarks/gc-model/`에 collector(`gcm.c`, 530줄 남짓)와 C 모델 다섯이 있습니다. 모델은 Calc와 같은 값 표현(tagged int, `주소 | 2`인 cons, 헤더 블록)을 쓰고, 같은 소스를 Boehm(지금 런타임과 같은 설정)과 새 collector로 각각 빌드합니다. `benchmarks/gc-model/run.sh`가 두 결과가 같은지 확인한 뒤 잽니다.

### 11.1 만든 것

- **영역:** 32GB를 `mmap`으로 잡아 두고 32KB 블록으로 나눕니다. 블록마다 상태(nursery, cons nursery, 크기별 old, cons old, 넘겨받은 nursery, 큰 객체)를 표에 둡니다.
- **nursery:** 일반 블록과 cons 블록에 따로 bump 할당합니다. 다시 쓰는 블록은 0으로 채웁니다(할당했지만 아직 쓰지 않은 칸을 GC가 포인터로 읽지 않게).
- **minor GC:** 4.2절 그대로입니다. 레지스터는 `setjmp`로 내리고, stack 시작은 `pthread_get_stackaddr_np`입니다.
- **old space:** 크기별 블록(16~2048바이트, 16바이트 단위로 올림)과 cons 블록, 큰 객체(블록 여러 개)입니다. 빈 칸의 첫 단어는 `0x7F`로 표시해서, conservative 포인터가 빈 칸을 가리켜도 객체로 보지 않습니다.
- **major GC:** stack은 conservative, 힙은 정확하게 mark하고 sweep합니다. 크기별 블록은 빈 칸으로 free list를 다시 만들고, 넘겨받은 nursery 블록은 객체가 모두 죽었을 때만 돌려줍니다.
- **write barrier:** 4.4절 그대로입니다. remembered set이 차면 쓰기 전에 minor GC를 합니다(쓰는 값이 stack에 살아 있어 pin 되도록).

### 11.2 결과

5번 잰 중앙값입니다(`run.sh`, nursery 16MB).

| C 모델 | Boehm | 새 collector | 1단계 목표 |
|---|---|---|---|
| msort: cons 2백만 개 merge sort | 0.50초, 298MB | 0.37초, 217MB | 0.33초 (못 미침) |
| live: 2백만 개를 붙잡은 채 `Some (n, [n])` 2천만 번 | 0.34초, 437MB | 0.12초, 165MB | 0.12초 (닿음) |
| bst: 영속 트리 백만 개 넣고 찾기 | 1.06초, 108MB | 0.91초, 120MB | 1.0초 (닿음) |
| mut: 큰 배열과 ref에 새 리스트를 3백만 번 씀 (write barrier) | 0.20초, 85MB | 0.08초, 73MB | |
| deep: 백만 단계 재귀 바닥에서 2천만 번 할당 | 0.25초, 364MB | 0.10초, 246MB | |

- **정확성:** 다섯 모델 모두 nursery 64KB, 1MB, 16MB에서 Boehm과 결과가 같습니다. 64KB는 블록 둘이라 GC가 아주 자주 일어나는 stress 조건입니다.
- **msort:** minor GC 108번에 494MB를 복사합니다. 리스트가 merge 한 단계 동안 살아 있어서 대부분 old space로 넘어가고, major GC가 10번 돕니다. nursery 64MB에서는 0.33초였습니다. 목표에는 조금 못 미치지만 Boehm보다 26% 빠르고 메모리도 적어서 2단계로 넘어갈 만하다고 봅니다.

### 11.3 시험하며 바꾼 것

| 바꾼 것 | 전 | 후 |
|---|---|---|
| nursery 4MB → 16MB | msort 0.51초, live 0.14초, bst 0.94초 | 0.42초, 0.12초, 0.93초. 메모리도 셋 다 줄어듦 |
| major GC 배율 2 → 3 | msort 0.42초, 170MB | 0.39초, 217MB |
| cons를 복사할 때 free list보다 bump를 먼저 | msort 0.40초 | 0.37초 (복사한 리스트가 메모리에 차례로 놓임) |
| 다음 nursery를 훑은 stack의 2배로 | deep 0.30초 (minor GC 77번마다 stack 수십 MB를 훑음, Boehm 0.25초) | 0.10초 (minor GC 8번) |

## 12. 2단계 결과: 런타임에 넣기

### 12.1 바꾼 것

- **런타임:** `runtime/gc.c`가 1단계의 collector입니다(`CALC_GC_GEN`일 때만 컴파일). 할당 함수는 `calc_alloc`, `calc_alloc_atomic`, `calc_alloc_cons`이고, `calc_write_barrier`와 `calc_gc_add_root`가 있습니다. Boehm 쪽(`runtime.c`)은 같은 이름의 함수를 지금처럼 Boehm으로 구현하고, barrier와 root 등록은 아무것도 하지 않습니다.
- **런타임 전역:** `calc_raised`(raise 한 예외 값)를 root로 등록합니다.
- **런타임의 barrier:** 리스트를 앞에서부터 만들 때 tail을 고치는 곳(`push`, `append`)과 `calc_array_set`입니다. `sort`의 임시 버퍼는 헤더가 있는 블록으로 바꿨습니다(블록을 헤더로 차례로 읽어야 하므로).
- **old space에 바로 만든 큰 객체:** 다음 minor GC가 한 번 훑습니다. 그래서 `Array.create`처럼 새 큰 배열을 채우는 쓰기에는 barrier가 없어도 됩니다.
- **Codegen:**
  - `:=`, 단어 배열 쓰기, `let rec … and …`의 클로저 칸 채우기 뒤에 barrier를 둡니다. 쓰는 값이 포인터(`(v & 5) = 0`)일 때만 `calc_write_barrier`를 부릅니다.
  - 이 함수는 `memory_effects`로 GC의 기록만 바꾼다고 알립니다. 그래서 반복 안의 다른 읽기를 LLVM이 반복 밖으로 뺄 수 있습니다. minor GC가 객체를 옮겨도, 레지스터의 값이 가리키는 객체는 pin 되어 그대로이므로 결과가 같습니다.
  - cons는 `calc_alloc_cons`로 할당합니다.
- **Toolchain:** `CALC_GC=gen`이면 런타임을 `-DCALC_GC_GEN`으로 컴파일하고 `-lgc`를 링크하지 않습니다. 런타임 오브젝트 캐시의 키에 모드를 넣었습니다.

### 12.2 런타임에 넣으면서 찾은 것

| 문제 | 증상 | 고친 것 |
|---|---|---|
| 인자가 여럿인 클로저의 헤더는 위쪽 32비트에 인자 수가 있음 (0.58.0) | 크기를 3천3백만 단어로 읽어 객체 뒤의 메모리까지 mark. 죽은 리스트가 모두 살아 있는 것으로 보여 major GC가 아무것도 거두지 못함(pipe 5.8초, 1.7GB) | data 블록처럼 클로저도 크기를 24비트로 가림. 1단계 C 모델에는 클로저가 없어서 드러나지 않았음 |
| 같은 ref에 거듭 씀 (`r := x :: !r`) | remembered set에 같은 칸이 계속 들어가 64K번마다 minor GC (0.10초 → 0.13초) | 마지막에 기록한 칸과 같으면 건너뜀 (0.10초) |
| GC stress에서 블록 하나에 모든 할당이 들어감 | stack이 그 블록을 가리켜 통째로 pin 되므로 복사를 전혀 시험하지 못함 | stress 모드에서는 블록마다 객체 4개만 넣음 |

### 12.3 테스트

- **전체 테스트:** Boehm(기본), `CALC_GC=gen`, `CALC_GC=gen CALC_GC_STRESS=10`(할당 10번마다 minor GC)에서 1241개가 모두 통과합니다.
- **새 묶음:** `세대 구분 GC (CALC_GC=gen)` 11개는 기본 설정과 상관없이 새 collector로 빌드하고, nursery 256KB와 할당 100번마다 minor GC로 실행합니다. 대상은 ref와 큰 배열에 새 리스트를 쓰기, 런타임이 앞에서부터 만드는 리스트, 30만 단계 재귀, 인자 수를 헤더에 적은 클로저, `let rec … and …`, 큰 문자열, raise 한 예외 값, `sort`, 영속 트리, 큰 bool 배열입니다.
- **MLIR 테스트:** barrier가 쓰는 곳 넷(`:=`, 배열, 클로저 칸 둘)에 있는지, cons가 `calc_alloc_cons`인지 확인합니다.
- **알려진 한계:** `CALC_GC_STRESS=3`이면 긴 리스트를 만드는 테스트 2개가 `Error: out of memory`로 끝납니다. GC마다 stack이 가리키는 블록이 old space로 넘어가는데, 그 블록에 긴 리스트의 칸 몇 개가 살아 있으면 블록 전체(32KB)를 돌려주지 못하기 때문입니다. 기본 설정(nursery 16MB)에서는 minor GC마다 2~5블록이라 1% 안쪽입니다. 넘겨받은 블록의 빈 곳을 다시 쓰는 일은 4단계에서 봅니다.

### 12.4 결과

실행 파일, 3번 잰 중앙값입니다. 결과는 두 모드에서 같습니다.

| 프로그램 | Boehm | `CALC_GC=gen` |
|---|---|---|
| bst: 영속 트리 백만 개 | 0.94초, 108MB | 0.70초, 114MB |
| msort: 2백만 개 | 0.53초, 316MB | 0.37초, 275MB |
| live: 2백만 개를 붙잡은 채 2천만 번 할당 | 0.35초, 404MB | 0.15초, 204MB |
| pipe: 백만 개에 `filter`, `map`, `fold` 100번 | 0.68초, 90MB | 0.68초, 106MB |
| `mymap` (tail call 아님) 백만 개 20번 | 0.26초, 206MB | 0.17초, 167MB |
| `sprintf` 3백만 번 | 0.10초, 4MB | 0.06초, 17MB |
| ref에 새 리스트 2천만 번 | 0.11초, 3MB | 0.10초, 20MB |
| 체 1억 (bool 배열) | 0.42초, 98MB | 0.42초, 96MB |
| int 배열 1천만 개 20번 | 0.83초, 79MB | 0.83초, 77MB |
| `benchmarks/` msort, bst | 0.044초, 0.060초 | 0.028초, 0.047초 |

- **pipe:** 리스트가 minor GC 몇 번 동안 살아 있어서(백만 개의 중간 리스트) 대부분 복사됩니다. Boehm과 같습니다.
- **작은 프로그램의 메모리:** nursery가 16MB라 할당이 적은 프로그램도 최대 메모리가 16MB쯤 됩니다.
- **다른 예제:** `benchmarks/`의 fib, queens, sieve와 배열 반복은 같습니다. barrier의 태그 검사는 int를 쓰는 반복에서 시간 차이가 없었습니다.
