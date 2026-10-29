---
order: 81
created: 2026-10-29
commit: b2831fa
version: 0.55.0
description: 생성 코드가 같은 C 코드보다 느리면 어셈블리에서 반복마다 다시 읽는 값을 찾는다. LLVM이 저장과 함수 호출이 그 메모리를 바꾸지 않는다고 증명하지 못하면 읽기를 반복 밖으로 빼지 못한다. 겹치지 않는 메모리에는 TBAA 태그를, 돌아오지 않는 오류 함수에는 noreturn을 붙이면 된다. invariant.load는 같은 효과지만 약속을 지킬 수 없어 쓰지 않는다
---

# TBAA와 noreturn으로 LLVM에 alias 정보 주기

> 관련 커밋: [b2831fa](https://github.com/ohama/calc-mlir/commit/b2831fa) (v0.55.0)

0.54.0까지 계산 위주 코드는 같은 C 코드에 가까워졌다(`80-pass-inferred-int-types-to-codegen.md`). 그런데 에라토스테네스의 체만 C보다 1.6배 느렸다.

| 큰 입력 | 실행 파일 (0.54.0) | 실행 파일 (0.55.0) | 같은 C 코드 |
|---|---|---|---|
| fib 41 | 0.33초 | 0.33초 | 0.28초 |
| N-Queens 14 | 2.62초 | | 2.44초 |
| 체 1억 | 0.85초 | 0.72초 | 0.56초 |

## The Insight

**LLVM은 증명할 수 있는 것만 최적화한다.** 체의 세는 반복은 다음과 같다.

```
let count = ref 0
for k = 0 to n do                       // $for 함수: 배열 isPrime, ref count, 끝값 n 을 클로저로 붙잡음
    if isPrime.[k] then incr count
```

LLVM이 tail call을 반복으로 바꾼 뒤에도, 반복마다 클로저 칸 세 개를 힙에서 다시 읽었다.

```
ldp	x23, x25, [x22, #16]      ; 끝값, count ref (클로저 칸)
ldr	x19, [x22, #40]           ; 배열 (클로저 칸)
```

두 가지를 증명하지 못했기 때문이다.

1. **`count := …`의 저장이 클로저 칸을 바꾸지 않는다.** LLVM이 보기에는 모두 `i64`를 담은 같은 종류의 메모리다.
2. **범위 밖 오류 호출 `calc_error_index`가 메모리를 바꾸지 않는다.** LLVM은 모르는 함수 호출이 메모리를 모두 바꿀 수 있다고 본다.

언어는 둘 다 안다. 클로저 칸은 만든 뒤 바뀌지 않고, ref 칸과 배열 원소와는 다른 블록이다. 오류 함수는 `exit`로 끝난다. 이 사실을 IR에 적어 주면 된다.

## Why This Matters

- **한쪽만 알려서는 효과가 없다.** TBAA만 붙이면 0.85초에서 0.80초였다. 반복 안의 오류 호출 경로가 여전히 메모리를 바꿀 수 있는 것으로 보였기 때문이다. `noreturn`까지 붙이자 0.72초가 되었다.
- **더 센 표시는 거짓 약속이 될 수 있다.** 클로저 칸 읽기에 `invariant`를 붙여도 0.72초였다. 그러나 LLVM의 `!invariant.load`는 "그 메모리를 읽을 수 있는 모든 시점에 값이 같다"는 뜻이다. 클로저는 할당한 직후, 칸을 채우기 전에도 읽을 수 있는 메모리다. 함수가 inline되면 LLVM이 읽기를 저장 앞으로 옮겨도 된다고 판단할 수 있다.

## Recognition Pattern

- 생성 코드가 같은 C 코드보다 느리고, 어셈블리의 반복 안에 바뀌지 않을 값을 읽는 `ldr`가 있을 때
- 반복 안에 가변 칸(ref, 배열)에 쓰는 저장이나, 드물게 타는 오류 호출이 있을 때
- 값이 모두 `i64` 하나로 표현되어 LLVM의 타입 정보가 alias 판단에 도움이 되지 않을 때

## The Approach

### Step 1: 같은 일을 하는 C 코드와 비교하고 반복의 어셈블리를 읽는다

```c
int64_t *a = malloc(sizeof(int64_t) * (n + 1));   /* Calc 처럼 원소 하나가 8바이트 (tagged bool) */
...
for (int k = 0; k <= n; k++) if (a[k] == 11) count++;
```

```bash
calc --compile=sieve100 --keep-temps sieve100.calc        # prog.ll 을 남김
clang -O2 -S -Wno-override-module -o sieve.s <임시 폴더>/prog.ll
```

반복의 머리(`tailrecurse`)부터 뒤로 가는 분기까지에서, 반복마다 같은 주소를 다시 읽는 명령을 찾는다.

### Step 2: 겹치지 않는 메모리에 서로 다른 TBAA 태그를 붙인다

```mlir
#calc_tbaa_root = #llvm.tbaa_root<id = "calc">
#calc_tbaa_captured_type = #llvm.tbaa_type_desc<id = "captured", members = {<#calc_tbaa_root, 0>}>
#calc_tbaa_ref_type = #llvm.tbaa_type_desc<id = "ref", members = {<#calc_tbaa_root, 0>}>
#calc_tbaa_array_type = #llvm.tbaa_type_desc<id = "array", members = {<#calc_tbaa_root, 0>}>
#calc_tbaa_captured = #llvm.tbaa_tag<base_type = #calc_tbaa_captured_type, access_type = #calc_tbaa_captured_type, offset = 0>
...
%v = llvm.load %slot {tbaa = [#calc_tbaa_captured]} : !llvm.ptr -> i64       // 함수 입구에서 클로저 칸
llvm.store %v, %cell {tbaa = [#calc_tbaa_ref]} : i64, !llvm.ptr              // r := v
llvm.load %elem {tbaa = [#calc_tbaa_array]} : !llvm.ptr -> i64               // a.[i]
```

- **선언 위치:** 속성 alias(`#…`)는 MLIR 모듈의 맨 앞, 연산보다 먼저 둔다.
- **ref와 배열도 나눈다:** ref와 배열 원소를 서로 다른 타입으로 둔다. 같은 타입이면 배열에 쓰는 저장 때문에 ref 읽기도 옮기지 못한다.
- **태그를 붙이지 않은 접근:** 클로저를 만들 때의 저장처럼 태그가 없는 접근은 모든 것과 겹칠 수 있는 것으로 다뤄진다. 그래서 태그를 일부에만 붙여도 안전하다. 효과는 태그를 붙인 접근끼리만 생긴다.
- **안전한 이유:** 세 종류는 서로 다른 블록이다. 같은 주소를 두 종류로 동시에 쓰는 일이 없다. GC가 거둔 블록의 메모리를 다른 종류로 다시 쓰는 것은 수명이 달라 문제가 없다(C의 strict aliasing과 같은 조건).

### Step 3: 돌아오지 않는 함수는 noreturn으로 선언한다

```mlir
llvm.func @calc_error_index() attributes {passthrough = ["noreturn", "nounwind"]}
llvm.func @calc_match_failure(i64) attributes {passthrough = ["noreturn", "nounwind"]}
```

- **먼저 확인할 것:** C 런타임에서 그 함수가 정말 돌아오지 않는지 확인한다. 이 경우 `calc_match_failure` → `calc_errorf` → `calc_error` → `exit(1)`이다.
- **얻는 것:** 오류 경로가 반복의 뒤로 가는 분기에 닿지 않으므로, 반복 안의 메모리 상태를 바꾸지 않는 것으로 다뤄진다.

### Step 4: 단계마다 재서 무엇이 필요한지 가린다

| 변경 (체 1억, 중앙값) | 시간 |
|---|---|
| 0.54.0 | 0.850초 |
| TBAA만 (클로저 칸 / ref와 배열) | 0.804초 |
| TBAA (클로저 칸, ref, 배열을 따로) + `noreturn` | 0.722초 |
| (넣지 않음) 클로저 칸 읽기에 `invariant` | 0.724초 |

이 컴퓨터에서 큰 체는 실행마다 0.78~0.95초로 흔들렸다. 그래서 11번 재서 중앙값을 비교했다.

### Step 5: 생성한 IR에 표시가 붙는지 테스트한다

```fsharp
let mlir = Codegen.emitProgram (Program.parse "let a = Array.create 3 0 in let r = ref 0 in let f i = a.[i] <- !r; r := i in f 1")
for tag in [ "#calc_tbaa_captured"; "#calc_tbaa_ref"; "#calc_tbaa_array" ] do
    Expect.isTrue (mlir.Contains("{tbaa = [" + tag + "]}")) tag
```

결과가 같은지는 기존 차등 테스트가 확인한다. 표시가 빠지면 결과는 같고 느려지기만 하므로, 표시가 붙는지는 따로 확인한다.

## Example

반복 안에 남은 차이(체 0.72초, C 0.56초)는 다른 데서 온다.

| 원인 | 설명 |
|---|---|
| tagged 정수 | `count + 1` 한 번이 태그를 떼고 32비트에서 더해 다시 붙이는 명령 네 개 |
| 범위 검사 | 원소 접근마다 비교 한 번 |
| ref 칸 | `count`가 레지스터가 아니라 힙의 칸에 있음 |

## 체크리스트

- [ ] 같은 일을 하는 C 코드와 비교해 차이가 큰 반복을 찾았는가?
- [ ] 반복 안에서 바뀌지 않을 값을 다시 읽는 명령을 어셈블리에서 찾았는가?
- [ ] 겹치지 않는 메모리 종류마다 서로 다른 TBAA 타입을 붙였는가? (가변 칸끼리도 나눔)
- [ ] 반복 안의 드문 호출(오류 함수)이 정말 돌아오지 않는지 확인하고 `noreturn`을 붙였는가?
- [ ] `invariant.load`처럼 더 센 표시가 언어의 실제 동작과 맞는지 따져 보았는가?
- [ ] 흔들리는 측정은 여러 번 재서 중앙값으로 비교했는가?
- [ ] 생성한 IR에 표시가 붙는지 테스트하는가?

## 관련 문서

- `80-pass-inferred-int-types-to-codegen.md` - 타입을 쓰는 int 연산
- `75-profile-native-executable-and-tune-boehm-gc.md` - 실행 파일의 profile
- `72-profile-fsharp-interpreter-and-read-its-il.md` - 인터프리터 쪽: 생성된 코드(IL)를 읽어 원인 찾기
