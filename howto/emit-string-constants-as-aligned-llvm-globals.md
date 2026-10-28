---
created: 2026-10-28
description: 태그가 붙은 값 표현에서 문자열 상수의 주소를 그대로 값으로 쓰려면 전역을 8바이트로 정렬해야 한다. 바이트 배열 전역은 주소 하위 비트가 0이 아니다. 헤더·길이·코드 단위를 담은 struct 전역을 초기화 영역으로 만든다
---

# 문자열 상수를 정렬된 LLVM 전역으로 만들기

컴파일한 코드에서 문자열 리터럴 `"hi"`는 실행 중에 만들지 않는다. **프로그램에 박힌 상수 블록**으로 두고 그 주소를 값으로 쓴다. 블록 모양은 힙에서 만드는 문자열(`calc_add`의 결과)과 똑같아서, 런타임은 상수와 힙 문자열을 구분하지 않는다.

```mlir
llvm.mlir.global internal constant @str_0() {addr_space = 0 : i32, alignment = 8 : i64}
    : !llvm.struct<(i64, i64, array<2 x i16>)> {
  %0 = llvm.mlir.undef : !llvm.struct<(i64, i64, array<2 x i16>)>
  %h = llvm.mlir.constant(514 : i64) : i64                 // 헤더 = 2 | (2 << 8)
  %1 = llvm.insertvalue %h, %0[0] : !llvm.struct<(i64, i64, array<2 x i16>)>
  %n = llvm.mlir.constant(2 : i64) : i64                   // 길이
  %2 = llvm.insertvalue %n, %1[1] : !llvm.struct<(i64, i64, array<2 x i16>)>
  %a = llvm.mlir.constant(dense<[104, 105]> : tensor<2xi16>) : !llvm.array<2 x i16>
  %3 = llvm.insertvalue %a, %2[2] : !llvm.struct<(i64, i64, array<2 x i16>)>
  llvm.return %3 : !llvm.struct<(i64, i64, array<2 x i16>)>
}

// 사용하는 곳
%p = llvm.mlir.addressof @str_0 : !llvm.ptr
%v = llvm.ptrtoint %p : !llvm.ptr to i64      // 값 = 주소 (하위 3비트 000)
```

## The Insight

**전역의 주소도 값 표현의 규칙을 지켜야 한다.** 값의 하위 3비트가 종류라면, 포인터 값은 하위 3비트가 `000`이어야 한다. 힙 블록은 GC가 8바이트로 정렬해 주지만, 전역은 그 **타입의 정렬**을 따른다. 바이트 배열 전역은 정렬이 1이라 주소가 아무 데나 놓인다(직접 확인함).

```
@a = internal constant [2 x i8] c"x\00"         주소 & 7 = 4
@b = internal constant [2 x i8] c"y\00"         주소 & 7 = 6
@c = internal constant [3 x i16] [...]          주소 & 7 = 0   (우연)
```

주소 하위 비트가 `5`가 되면 이 표현에서는 `[]`(nil)로 읽힌다. 그래서 정렬을 운에 맡기지 않는다.

- **`alignment = 8 : i64`:** 전역에 붙여서 명시합니다.
- **struct의 첫 칸을 `i64`(헤더)로:** 타입의 정렬도 8이 됩니다.

## Why This Matters

- **C 문자열처럼 `"hi\00"` 바이트 배열로 만든 경우:** 주소의 하위 비트가 0이 아닐 수 있습니다. 그러면 런타임이 문자열을 정수나 불리언, `[]`로 잘못 읽습니다. 빌드할 때마다, 상수 개수에 따라 달라지므로 재현도 어렵습니다.
- **상수를 실행 중에 힙에 복사한 경우:** 리터럴을 평가할 때마다 할당이 생깁니다. 반복문 안의 `"x"`가 GC에 부담이 됩니다.
- **블록 모양이 힙 문자열과 다른 경우:** 런타임의 `calc_eq`, `calc_compare`, 출력이 경우를 둘씩 처리해야 합니다.

## Recognition Pattern

- 태그가 붙은 포인터(하위 비트로 종류 표시)를 쓰는 언어에 상수 데이터를 넣을 때
- 전역 상수의 주소를 `ptrtoint`해서 값으로 쓸 때
- 런타임이 상수와 힙 객체를 같은 코드로 다뤄야 할 때

## The Approach

### Step 1: 힙 블록과 같은 struct 타입을 정한다

```
!llvm.struct<(i64, i64, array<N x i16>)>
              헤더  길이  UTF-16 코드 단위
```

C 런타임에는 같은 모양의 struct가 있다.

```c
typedef struct { int64_t header; int64_t length; uint16_t units[]; } calc_string;
```

### Step 2: 값이 있는 전역은 초기화 영역으로 만든다

`llvm.mlir.global ... @name("문자열")`나 `@name(dense<[...]> : tensor<Nxi16>)` 같은 한 줄 형식은 속성 하나로 표현되는 값(바이트 문자열, 같은 타입의 배열)만 받는다. struct는 영역(`{ ... llvm.return }`) 안에서 `llvm.mlir.undef`부터 `llvm.insertvalue`로 칸을 채우고 돌려준다. 결과 LLVM IR은 다음과 같다.

```llvm
@str_0 = internal constant { i64, i64, [2 x i16] } { i64 514, i64 2, [2 x i16] [i16 104, i16 105] }, align 8
```

### Step 3: 코드 단위는 `dense` 상수로

`dense<[...]> : tensor<Nxi16>`는 `65535`처럼 부호 없는 값도 받아서 같은 비트로 저장한다(`i16 -1`). 생성할 때 `int16`으로 바꿔서 쓰면 출력이 일정해진다.

빈 문자열은 길이 0 배열(`array<0 x i16>`, `dense<[]> : tensor<0xi16>`)도 받아 주었다. 다만 이 프로젝트는 읽기 쉽게 `!llvm.struct<(i64, i64)>`로 따로 만든다.

### Step 4: 같은 내용은 하나만, 이름은 프로그램마다 새로

```fsharp
type private StringTable() =
    let names = Dictionary<string, string>()
    member _.NameOf(s: string) =
        match names.TryGetValue s with
        | true, name -> name
        | _ -> let name = sprintf "str_%d" names.Count in names[s] <- name; name
```

- **모으는 순서:** 함수 본문을 모두 생성한 **뒤에** 전역을 출력해야 함수 안의 리터럴까지 모입니다.
- **같은 내용의 리터럴:** 주소가 같아집니다. 하지만 `=`가 내용을 비교하므로 의미는 달라지지 않습니다.

### Step 5: 상수는 GC 대상이 아니다

전역은 GC가 할당한 메모리가 아니다. Boehm GC는 이 주소를 만나도 무시하고, 상수는 프로그램이 끝날 때까지 남는다. 런타임이 블록을 **고치지 않는다**는 약속도 함께 지킨다. `constant` 전역은 읽기 전용 영역에 놓인다.

## Example

```fsharp
// ❌ BAD: C 문자열처럼 바이트 배열 → 주소 하위 비트가 4, 6 이 될 수 있음
line (sprintf "llvm.mlir.global internal constant @%s(\"%s\\00\") {addr_space = 0 : i32}" name s)

// ✅ GOOD: 헤더로 시작하는 struct + alignment = 8
line (sprintf "llvm.mlir.global internal constant @%s() {addr_space = 0 : i32, alignment = 8 : i64} : %s {" name ty)
```

## 체크리스트

- [ ] 전역에 `alignment = 8`(또는 태그 비트 수에 맞는 정렬)이 있는가?
- [ ] 상수 블록 모양이 힙 블록과 같은가? (헤더, 길이, 내용)
- [ ] 함수 본문을 모두 만든 뒤에 전역을 출력하는가?
- [ ] 런타임이 상수 블록에 쓰지 않는가? (읽기 전용)

## 관련 문서

- `represent-values-as-tagged-words.md` - 하위 3비트 태그와 포인터 정렬
- `store-strings-as-utf16-for-dotnet-compatibility.md` - 코드 단위를 UTF-16으로 둔 이유
- `emit-mlir-as-llvm-func-and-lower.md` - `llvm.mlir.addressof`와 `llvm` 방언
