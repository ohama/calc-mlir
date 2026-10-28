---
created: 2026-10-28
description: 내장 함수를 FSharp.Core 함수로 바로 구현하면 그 함수의 오버플로 검사, 예외 감싸기, 오류 메시지가 언어 의미론으로 새어 나온다. 언어 규칙과 다른 곳을 찾아 막는다
---

# 내장 함수에 새어 나오는 FSharp.Core의 동작 막기

`sum`을 `List.sum`으로, `sort`를 `List.sortWith`로 구현하면 코드는 한 줄이다. 하지만 그 순간 **FSharp.Core 함수의 동작이 언어의 의미론이 된다**. 오버플로를 검사하는지, 예외를 감싸는지, 빈 입력에서 어떤 메시지를 내는지가 모두 언어 사용자에게 보인다. 이 동작이 언어의 다른 규칙과 어긋나는 곳을 찾아 막는다.

## The Insight

라이브러리 함수는 **F# 프로그래머**에게 맞춰진 동작을 한다. 언어 사용자가 기대하는 것은 **이 언어의 규칙**이다. 둘은 자주 다르다.

| 질문 | F# 라이브러리의 답 | 이 언어의 규칙 |
|---|---|---|
| int 오버플로 | `List.sum`은 `OverflowException` | `+`는 한 바퀴 돎 |
| 비교 중 오류 | `List.sortWith`가 `InvalidOperationException`으로 감쌈 | 원래 타입 오류를 그대로 보여야 함 |
| 빈 리스트 | `The input list was empty. (Parameter 'list')` | `head: empty list` |

내장 함수 하나를 추가할 때마다 "이 라이브러리 함수는 경계에서 무엇을 하나?"를 확인해야 한다. 언어 차원의 `+`, `<`와 같은 규칙인지 비교한다.

## Why This Matters

리스트 내장 함수(0.10.0)를 만들면서 실제로 만난 두 가지다.

**1. `sum`이 `+`와 다르게 동작했다.**

```
sum (range 1 100000)   →  Error: Arithmetic operation resulted in an overflow.
1 + 2147483647         →  -2147483648 (한 바퀴 돎)
```

FSharp.Core의 합계 함수는 오버플로를 검사한다. `dotnet fsi`로 확인한 결과다.

```
List.sum [Int32.MaxValue; 1]          OverflowException
List.sumBy id [Int32.MaxValue; 1]     OverflowException
Seq.sum [Int32.MaxValue; 1]           OverflowException
List.fold (+) 0 [Int32.MaxValue; 1]   -2147483648
Int32.MaxValue + 1                    -2147483648
```

같은 `[1; 2; ...]`의 합인데 `fold (fun acc x -> acc + x) 0 xs`와 `sum xs`의 결과가 달라진다.

**2. `sort`의 타입 오류가 다른 메시지로 바뀌었다.**

```
sort [1; "a"]   →  Error: Failed to compare two elements in the array.
```

비교 함수 안에서 던진 `Type error: expected int but got string`이 `InvalidOperationException`의 `InnerException`으로 들어간다. 사용자는 무엇이 잘못됐는지 알 수 없다.

게다가 `List.sortWith`는 원소가 0개나 1개면 **비교 함수를 아예 부르지 않는다**. 그래서 `sort [true]`는 성공하고 `sort [true; false]`는 실패한다. 리스트 길이에 따라 타입 검사가 달라진다.

## Recognition Pattern

- 내장 함수를 `List.xxx`, `Seq.xxx`, `Array.xxx`, .NET 메서드로 한 줄에 구현할 때
- 오류 메시지에 `(Parameter 'list')`, `Failed to compare`, `Arithmetic operation resulted in an overflow` 같은 .NET 문구가 보일 때
- 같은 계산을 내장 함수로 할 때와 직접 짠 코드로 할 때 결과가 다를 때

## The Approach

**경계 입력 네 가지로 라이브러리 함수를 먼저 찔러 본다.** 빈 입력, 오버플로, 콜백이 던지는 예외, 원소 1개. 결과를 언어 규칙과 비교한다.

### Step 1: `dotnet fsi`로 경계 동작을 조사한다

```fsharp
let t name f =
    try printfn "%-28s OK %A" name (f ())
    with e ->
        let inner = if isNull e.InnerException then "" else " | inner: " + e.InnerException.Message
        printfn "%-28s %s: %s%s" name (e.GetType().Name) e.Message inner

t "List.head []" (fun () -> List.head ([]: int list) |> box)
t "List.item 5 [1]" (fun () -> List.item 5 [1] |> box)
t "List.sum [Max;1]" (fun () -> List.sum [System.Int32.MaxValue; 1] |> box)
t "List.sortWith throws" (fun () -> List.sortWith (fun (a: int) b -> failwith "type error") [2; 1] |> box)
t "List.sortWith 1 elem" (fun () -> List.sortWith (fun (a: int) b -> failwith "type error") [1] |> box)
```

이 프로젝트 환경(.NET 10)의 결과다.

| 호출 | 결과 |
|---|---|
| `List.head []`, `List.tail []`, `List.reduce` 빈 리스트 | `ArgumentException: The input list was empty. (Parameter 'list')` |
| `List.max []` | `ArgumentException: The input sequence was empty. (Parameter 'list')` |
| `List.item 5 [1]`, `List.item -1 [1]` | `ArgumentException: The index was outside the range of elements in the list. (Parameter 'index')` |
| `List.sum` 오버플로 | `OverflowException` |
| `List.sortWith` 비교 함수가 예외 | `InvalidOperationException: Failed to compare two elements in the array.` (원래 예외는 inner) |
| `List.sortWith` 원소 1개 | 비교 함수를 부르지 않고 성공 |
| `List.map` 콜백 호출 순서 | 앞에서부터 차례로 |
| `abs Int32.MinValue` | `OverflowException` |
| `Int32.MinValue / -1` | `OverflowException` |

### Step 2: 언어 규칙과 다른 것만 직접 처리한다

모든 것을 다시 짤 필요는 없다. 규칙과 어긋나는 것만 고친다.

```fsharp
// List.sum 은 오버플로를 검사(OverflowException)하므로, 값이 한 바퀴 도는 언어의 + 와 맞추려고 fold 사용
| "sum", [ xs ] -> VInt (list' xs |> List.fold (fun acc x -> acc + int' x) 0)

| "sort", [ xs ] ->
    let xs = list' xs
    // 비교 함수 안에서 난 예외는 List.sortWith 가 InvalidOperationException 으로 감싸므로,
    // 정렬 전에 모든 원소가 첫 원소와 비교 가능한지 확인해서 타입 오류를 그대로 냄
    match xs with
    | first :: _ -> xs |> List.iter (fun x -> compareValues first x |> ignore)
    | [] -> ()
    VList (List.sortWith compareValues xs)
```

- `sort`의 사전 검사는 첫 원소를 **자기 자신과도** 비교한다. 그래서 `sort [true]`도 `expected int or string but got bool`을 낸다. 길이와 상관없이 같은 검사를 한다.
- 빈 리스트와 범위 밖 위치는 라이브러리를 부르기 전에 직접 검사하고 언어의 메시지를 낸다.

```fsharp
| "head", [ xs ] ->
    match list' xs with
    | h :: _ -> h
    | [] -> failwith "head: empty list"
| "nth", [ xs; i ] ->
    let xs, i = list' xs, int' i
    if i < 0 || i >= List.length xs then
        failwithf "nth: index out of range (length %d, index %d)" (List.length xs) i
    List.item i xs
```

### Step 3: 경계를 테스트로 고정한다

```fsharp
// + 와 같이 오버플로는 한 바퀴 돎
"sum [2147483647; 1]", VInt -2147483648

// List.sortWith 가 감싼 예외가 아니라 원래 타입 오류
expectError "sort [1; \"a\"]" "Type error: expected int but got string"
expectError "sort [true]" "Type error: expected int or string but got bool"
expectError "head []" "head: empty list"
```

FsCheck로 "내장 함수 = 직접 짠 버전"을 확인하면 이런 차이를 자동으로 찾을 수 있다.

```fsharp
testProperty "fold 로 만든 length 는 length 와 같음" <| fun (xs: NonNegativeInt list) -> ...
```

## Example

같은 조사로 **언어의 기존 연산자**에서도 새는 곳을 찾았다. 이 문서를 쓰는 시점(0.10.0)에 아직 고치지 않은 것이다.

```
1 + 2147483647                      →  -2147483648                 (한 바퀴 돎)
(0 - 2147483647 - 1) / (0 - 1)      →  Error: Arithmetic operation resulted in an overflow.
```

`Divide`는 F#의 `/`를 그대로 쓴다. .NET에서 `Int32.MinValue / -1`은 `OverflowException`이다. `+`, `-`, `*`는 한 바퀴 도는데 `/`만 예외를 낸다. 언어 규칙을 하나로 정하려면 이 경우를 따로 처리해야 한다. 예를 들어 결과를 `Int32.MinValue`로 정한다(한 바퀴 돈 값).

## 체크리스트

- [ ] 내장 함수에 쓴 라이브러리 함수를 빈 입력, 오버플로, 콜백 예외, 원소 1개로 `dotnet fsi`에서 조사했다
- [ ] 오버플로 동작이 언어의 `+`와 같다 (`List.sum` 대신 `fold`)
- [ ] 콜백 예외가 감싸지지 않고 원래 메시지로 보인다
- [ ] 빈 리스트, 범위 밖 위치는 라이브러리를 부르기 전에 언어의 메시지로 막는다
- [ ] 오류 메시지에 `(Parameter '...')` 같은 .NET 문구가 나오지 않는다
- [ ] 경계 동작을 테스트로 고정했다

## 관련 문서

- `implement-builtins-as-data-values.md` - 내장 함수를 값으로 표현하고 디스패치하는 구조
- `use-ordinal-string-apis-for-language-builtins.md` - 문자열 API의 문화권 기본값도 같은 종류의 누수
