module Eval

open Ast

type Value =
    | VInt of int
    | VBool of bool
    | VString of string
    /// fun param -> body 를 평가한 함수 (정의된 곳의 칸을 가리킴, 정적 범위). let rec 의 함수도 이것
    | VClosure of Closure
    | VList of Value list
    /// (v1, v2, ...) 원소 2개 이상
    | VTuple of Value list
    /// 내장 함수. args 는 지금까지 받은 인자 (커링: 인자가 다 모이면 실행)
    | VBuiltin of name: string * args: Value list
    /// 생성자로 만든 값. None, Some 1, Rect (2, 3). typeName 은 선언한 타입 이름 (option, shape)
    | VData of typeName: string * ctor: string * payload: Value option
    /// 인자가 있는 생성자 자체 (함수처럼 호출). fields 는 선언의 튜플 원소 수 (of int * int 면 2)
    | VConstructor of typeName: string * ctor: string * fields: int
    /// () : unit
    | VUnit
    /// ref v: 내용을 바꿀 수 있는 칸 하나 (!r 로 읽고 r := v 로 씀)
    | VRef of Value ref
    /// [| 1; 2 |]: 원소를 바꿀 수 있는 고정 길이 배열 (a.[i] <- v)
    | VArray of Value[]
    /// { x = 1; y = 2 }. fields 는 선언한 순서
    | VRecord of typeName: string * fields: (string * Value) list
    /// 레코드 타입의 선언 (환경에만 있는 값): 환경의 "." + 필드 이름이 이 값. 필드 이름으로 타입과 필드 순서를 찾음
    | VRecordType of typeName: string * fields: string list

/// 함수 값: fun p1 -> … -> fun pn -> body 를 인자 Arity 개의 함수 하나로. 부를 때 Size 칸의 Frame 을 만들고 칸 0.. 에 인자를 넣어
/// Code 를 실행. Bound 는 partial application 으로 이미 받은 인자. 같은 함수인지만 비교할 수 있음 (참조)
and Closure(arity: int, size: int, parent: obj[], code: obj[] -> Value, bound: Value list) =
    member _.Arity = arity
    member _.Size = size
    member _.Parent = parent
    member _.Code = code
    member _.Bound = bound

/// 함수 호출(과 최상위 식) 하나의 칸들: [바깥 Frame; 칸 0; 칸 1; …]. 안쪽 함수의 Frame 은 그 함수를 만든 곳의 Frame 을
/// 0번에 가리킴 (정적 범위). 객체 하나라서 호출마다 할당이 한 번 (Frame 객체와 칸 배열을 따로 두면 두 번)
type Frame = obj[]

// 0번에는 Frame(또는 최상위의 null), 나머지에는 Value(또는 아직 쓰지 않은 null)만 넣으므로 형 검사 없이 꺼냄
let inline private slotOf (fr: Frame) (slot: int) : Value = System.Runtime.CompilerServices.Unsafe.As<Value>(fr[slot + 1])
let inline private setSlot (fr: Frame) (slot: int) (v: Value) = fr[slot + 1] <- v
let inline private parentOf (fr: Frame) : Frame = System.Runtime.CompilerServices.Unsafe.As<obj[]>(fr[0])
let inline private newFrame (size: int) (parent: Frame) : Frame =
    let fr: obj[] = Array.zeroCreate (size + 1)
    fr[0] <- parent
    fr

/// 이름 → 값. 내장 함수, 생성자, REPL 의 앞 입력에서 정의한 값 (평가할 때는 상수로 씀)
type Env = Map<string, Value>

/// 문자열을 리터럴 형태로 출력 (렉서가 받는 이스케이프로 되돌림)
let quote (s: string) =
    let escaped =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r")
    "\"" + escaped + "\""

let rec formatValue value =
    match value with
    | VUnit -> "()"
    | VInt n -> string n
    | VBool true -> "true"
    | VBool false -> "false"
    | VString s -> quote s
    | VClosure _ | VBuiltin _ | VConstructor _ -> "<fun>"
    | VList items -> "[" + (items |> List.map formatValue |> String.concat "; ") + "]"
    | VTuple items -> "(" + (items |> List.map formatValue |> String.concat ", ") + ")"
    | VData (_, ctor, None) -> ctor
    | VData (_, ctor, Some payload) -> applied ctor payload
    | VRef cell -> applied "ref" cell.Value
    | VArray items -> "[|" + (items |> Array.map formatValue |> String.concat "; ") + "|]"
    | VRecord (_, fields) -> "{ " + (fields |> List.map (fun (f, v) -> f + " = " + formatValue v) |> String.concat "; ") + " }"
    | VRecordType (typeName, _) -> "<type " + typeName + ">"

/// 생성자나 ref 를 인자에 적용한 모양. 다시 입력했을 때 같은 값이 되도록 괄호: Some (Some 1), Some (-1), ref (ref 1).
/// 튜플은 이미 괄호가 있음
and private applied (name: string) (payload: Value) =
    match payload with
    | VData (_, _, Some _) | VRef _ -> sprintf "%s (%s)" name (formatValue payload)
    | VInt n when n < 0 -> sprintf "%s (%s)" name (formatValue payload)
    | _ -> sprintf "%s %s" name (formatValue payload)

/// raise 로 던진 Calc 예외 (try ... with 가 받음). 잡히지 않으면 calc 가 Message 를 "Error: ..." 로 출력:
/// Failure s 는 s (failwith 의 메시지, 내장 함수의 오류와 같은 모양), 나머지는 "exception <값>"
exception CalcException of value: Value with
    override this.Message =
        match this.value with
        | VData ("exn", "Failure", Some (VString s)) -> s
        | v -> "exception " + formatValue v

let typeName value =
    match value with
    | VUnit -> "unit"
    | VInt _ -> "int"
    | VBool _ -> "bool"
    | VString _ -> "string"
    | VClosure _ | VBuiltin _ | VConstructor _ -> "function"
    | VList _ -> "list"
    | VTuple _ -> "tuple"
    | VData (typeName, _, _) -> typeName
    | VRef _ -> "ref"
    | VArray _ -> "array"
    | VRecord (typeName, _) | VRecordType (typeName, _) -> typeName

/// 오류 메시지용: 튜플은 원소 수까지
let describe value =
    match value with
    | VTuple items -> sprintf "tuple of %d" (List.length items)
    | v -> typeName v

let typeError expected value =
    failwithf "Type error: expected %s but got %s" expected (typeName value)

/// = 와 <> 는 같은 타입끼리만 비교 가능. 리스트는 원소를 차례로 비교
let rec valueEquals a b =
    match a, b with
    | VUnit, VUnit -> true
    | VInt x, VInt y -> x = y
    | VBool x, VBool y -> x = y
    | VString x, VString y -> x = y
    | VList xs, VList ys
    | VTuple xs, VTuple ys ->
        List.length xs = List.length ys && List.forall2 valueEquals xs ys
    // 같은 타입끼리: 생성자가 다르면 false, 같으면 인자를 비교
    | VData (t1, c1, p1), VData (t2, c2, p2) when t1 = t2 ->
        c1 = c2
        && (match p1, p2 with
            | Some x, Some y -> valueEquals x y
            | _ -> true)
    // 내용을 비교 (OCaml, F# 와 같음). 같은 칸인지는 보지 않음
    | VRef x, VRef y -> valueEquals x.Value y.Value
    // 원소를 차례로 비교 (F#, OCaml 과 같음). 같은 배열인지는 보지 않음
    | VArray xs, VArray ys -> xs.Length = ys.Length && Array.forall2 valueEquals xs ys
    // 같은 타입끼리: 필드를 선언한 순서로 비교
    | VRecord (t1, xs), VRecord (t2, ys) when t1 = t2 -> List.forall2 (fun (_, x) (_, y) -> valueEquals x y) xs ys
    | VInt _, v -> typeError "int" v
    | VBool _, v -> typeError "bool" v
    | VString _, v -> typeError "string" v
    | VList _, v -> typeError "list" v
    | VTuple _, v -> typeError "tuple" v
    | VData (t, _, _), v -> typeError t v
    | VRef _, v -> typeError "ref" v
    | VArray _, v -> typeError "array" v
    | VRecord (t, _), v -> typeError t v
    | v, _ -> typeError "int, bool, string, list, tuple or data" v

/// 패턴이 값과 일치하면 묶을 이름들을 env 에 추가해서 반환. 타입이 달라도 오류가 아니라 불일치
let rec matchPattern pattern value (env: Env) : Env option =
    match pattern, value with
    | PVar name, v -> Some (Map.add name v env)
    | PWildcard, _ -> Some env
    | PUnit, VUnit -> Some env
    // 왼쪽부터 처음 일치하는 쪽 (가드가 실패해도 다른 쪽을 다시 시도하지 않음, OCaml 과 같음)
    | POr (a, b), v -> matchPattern a v env |> Option.orElseWith (fun () -> matchPattern b v env)
    | PInt n, VInt m -> if n = m then Some env else None
    | PBool b, VBool c -> if b = c then Some env else None
    | PString s, VString t -> if s = t then Some env else None
    | PNil, VList [] -> Some env
    | PCons (ph, pt), VList (h :: t) ->
        matchPattern ph h env |> Option.bind (matchPattern pt (VList t))
    // 쓴 필드만 검사. 타입 검사를 건너뛰어 없는 필드면 불일치
    | PRecord fields, VRecord (_, values) ->
        fields
        |> List.fold
            (fun acc (f, p) ->
                acc |> Option.bind (fun env ->
                    match List.tryFind (fun (n, _) -> n = f) values with
                    | Some (_, v) -> matchPattern p v env
                    | None -> None))
            (Some env)
    | PTuple ps, VTuple vs when List.length ps = List.length vs ->
        List.fold2 (fun acc p v -> acc |> Option.bind (matchPattern p v)) (Some env) ps vs
    | PCtor (name, argPattern), v ->
        // 오타 난 생성자가 조용히 "불일치"가 되지 않도록, 지금 보이는 생성자인지와 인자 유무를 먼저 확인
        let typeName =
            match Map.tryFind name env, argPattern with
            | Some (VData (t, _, None)), None -> t
            | Some (VConstructor (t, _, _)), Some _ -> t
            | Some (VData _), Some _ -> failwithf "Constructor %s takes no argument" name
            | Some (VConstructor _), None -> failwithf "Constructor %s requires an argument" name
            | _ -> failwithf "Unknown constructor: %s" name
        match v, argPattern with
        | VData (t, c, None), None when t = typeName && c = name -> Some env
        | VData (t, c, Some payload), Some p when t = typeName && c = name -> matchPattern p payload env
        | _ -> None
    | _ -> None

let matchFailure value =
    failwithf "Match failure: %s" (formatValue value)

/// < > <= >= 와 sort 의 순서. int 끼리 또는 string 끼리 (문자 코드 순서)
let compareValues a b =
    match a, b with
    | VInt x, VInt y -> compare x y
    | VString x, VString y -> System.String.CompareOrdinal(x, y)
    | VInt _, v -> typeError "int" v
    | VString _, v -> typeError "string" v
    | v, _ -> typeError "int or string" v

// ---- 내장 함수 ----

let private str value =
    match value with
    | VString s -> s
    | v -> typeError "string" v

let private int' value =
    match value with
    | VInt n -> n
    | v -> typeError "int" v

let private list' value =
    match value with
    | VList items -> items
    | v -> typeError "list" v

let private bool' value =
    match value with
    | VBool b -> b
    | v -> typeError "bool" v

/// 이름과 인자 수
let builtinArity =
    Map.ofList [
        "length", 1
        "substr", 3
        "upper", 1
        "lower", 1
        "trim", 1
        "contains", 2
        "startsWith", 2
        "endsWith", 2
        "indexOf", 2
        "replace", 3
        "split", 2
        "join", 2
        "toString", 1
        "toInt", 1
        // 리스트
        "sort", 1
        // 출력: 문자열은 따옴표 없이, 나머지는 formatValue (toString 과 같음)
        "print", 1
        "println", 1
        // 예외: exn -> 'a
        "raise", 1
        // 가변 참조: ref v, !r, r := v (파서가 ! 와 := 를 이 이름의 호출로 만듦)
        "ref", 1
        "!", 1
        ":=", 2
        // 배열: [| … |] 는 [||] 에 리스트, a.[i] 는 .[], a.[i] <- v 는 .[]<- (파서가 만듦). init, map 같은 함수는 prelude
        "[||]", 1
        ".[]", 2
        ".[]<-", 3
        "Array.length", 1
        "Array.create", 2
        "Array.ofList", 1
        "Array.toList", 1
        // 포맷: 첫 인자(포맷 문자열)가 나머지 인자를 정함 (Format.expand 로 만든 함수를 돌려줌)
        "sprintf", 1
        "printf", 1
        "printfn", 1
        // fst, map, fold, range, head, failwith 같은 함수는 Calc 로 쓴 prelude (prelude.calc)
    ]

let none = VData ("option", "None", None)
let some v = VData ("option", "Some", Some v)

/// print, println 이 쓰는 곳. 테스트는 evalWithOutput 으로 바꿈 (병렬 테스트끼리 섞이지 않게 AsyncLocal).
/// 설정하지 않은 스레드에서는 null 이므로 표준 출력
let private outputSlot = System.Threading.AsyncLocal<System.IO.TextWriter>()

let private output () =
    match outputSlot.Value with
    | null -> System.Console.Out
    | writer -> writer

/// print, println, toString 이 보여 주는 모양: 문자열은 그대로, 나머지는 formatValue
let display value =
    match value with
    | VString s -> s
    | v -> formatValue v

/// 인자가 다 모인 내장 함수를 실행. 문자열 위치와 길이는 UTF-16 코드 단위 (.NET string 과 같음).
/// 사용자 함수를 부르는 고차 함수(map, filter)는 prelude 에 있으므로 내장 함수는 값만 다룸
let callBuiltin name args =
    let ordinal = System.StringComparison.Ordinal
    match name, args with
    | "length", [ VString s ] -> VInt s.Length
    | "length", [ VList items ] -> VInt (List.length items)
    | "length", [ v ] -> typeError "string or list" v
    | "substr", [ s; start; len ] ->
        let s, start, len = str s, int' start, int' len
        if start < 0 || len < 0 || start > s.Length || len > s.Length - start then
            failwithf "substr: index out of range (length %d, start %d, count %d)" s.Length start len
        VString (s.Substring(start, len))
    | "upper", [ s ] -> VString ((str s).ToUpperInvariant())
    | "lower", [ s ] -> VString ((str s).ToLowerInvariant())
    | "trim", [ s ] -> VString ((str s).Trim())
    | "contains", [ s; sub ] -> VBool ((str s).Contains(str sub, ordinal))
    | "startsWith", [ s; prefix ] -> VBool ((str s).StartsWith(str prefix, ordinal))
    | "endsWith", [ s; suffix ] -> VBool ((str s).EndsWith(str suffix, ordinal))
    | "indexOf", [ s; sub ] -> VInt ((str s).IndexOf(str sub, ordinal))
    | "replace", [ s; oldValue; newValue ] ->
        let s, oldValue, newValue = str s, str oldValue, str newValue
        if oldValue = "" then failwith "replace: empty search string"
        VString (s.Replace(oldValue, newValue, ordinal))
    | "split", [ s; sep ] ->
        let s, sep = str s, str sep
        let parts =
            // 빈 구분자는 한 글자씩 나눔
            if sep = "" then s |> Seq.map string |> List.ofSeq
            else s.Split(sep) |> List.ofArray
        VList (parts |> List.map VString)
    | "join", [ sep; items ] ->
        match items with
        | VList xs -> VString (System.String.Join(str sep, xs |> List.map str))
        | v -> typeError "list" v
    | "toString", [ v ] -> VString (display v)
    | "toInt", [ s ] ->
        let s = str s
        match System.Int32.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign,
                                    System.Globalization.CultureInfo.InvariantCulture) with
        | true, n -> VInt n
        | false, _ -> failwithf "toInt: invalid integer: %s" (quote s)
    // 리스트: 다루는 리스트가 먼저
    | "sort", [ xs ] ->
        let xs = list' xs
        // 비교 함수 안에서 난 예외는 List.sortWith 가 InvalidOperationException 으로 감싸므로,
        // 정렬 전에 모든 원소가 첫 원소와 비교 가능한지 확인해서 타입 오류를 그대로 냄
        match xs with
        | first :: _ -> xs |> List.iter (fun x -> compareValues first x |> ignore)
        | [] -> ()
        VList (List.sortWith compareValues xs)
    | "raise", [ v ] -> raise (CalcException v)
    | "ref", [ v ] -> VRef (ref v)
    | "!", [ VRef cell ] -> cell.Value
    | "!", [ v ] -> typeError "ref" v
    | ":=", [ VRef cell; v ] ->
        cell.Value <- v
        VUnit
    | ":=", [ v; _ ] -> typeError "ref" v
    | ("[||]" | "Array.ofList"), [ xs ] -> VArray (Array.ofList (list' xs))
    | "Array.toList", [ VArray items ] -> VList (List.ofArray items)
    | "Array.length", [ VArray items ] -> VInt items.Length
    | "Array.create", [ n; v ] ->
        let n = int' n
        if n < 0 then failwithf "Array.create: the length must be non-negative (got %d)" n
        VArray (Array.create n v)
    // 범위 밖은 .NET 의 IndexOutOfRangeException 과 같은 문장 (0으로 나누기처럼 try 로 받을 수 없는 실행 중 오류)
    | ".[]", [ VArray items; i ] ->
        let i = int' i
        if i < 0 || i >= items.Length then failwith "Index was outside the bounds of the array."
        items[i]
    | ".[]<-", [ VArray items; i; v ] ->
        let i = int' i
        if i < 0 || i >= items.Length then failwith "Index was outside the bounds of the array."
        items[i] <- v
        VUnit
    | ("Array.toList" | "Array.length" | ".[]" | ".[]<-"), v :: _ -> typeError "array" v
    | "print", [ v ] ->
        output().Write(display v)
        VUnit
    | "println", [ v ] ->
        output().WriteLine(display v)
        VUnit
    | _ -> failwithf "Unknown builtin: %s" name

/// 처음 환경. 내장 함수는 보통 변수처럼 let 으로 가릴 수 있음
let builtins: Env =
    builtinArity
    |> Map.map (fun name _ -> VBuiltin (name, []))
    // 'a option = None | Some of 'a 는 선언 없이 쓸 수 있음
    |> Map.add "None" none
    |> Map.add "Some" (VConstructor ("option", "Some", 1))
    // 처음부터 있는 예외 (OCaml 과 같음): failwith 가 쓰는 Failure, 찾지 못함을 뜻하는 Not_found
    |> Map.add "Failure" (VConstructor ("exn", "Failure", 1))
    |> Map.add "Not_found" (VData ("exn", "Not_found", None))

/// type 선언의 생성자를 환경에 추가: 인자 없는 생성자는 값, 인자 있는 생성자는 함수처럼 호출하는 값
let declareConstructors (env: Env) (typeName: string) (ctors: (string * TypeExpr option) list) =
    let bindCtor env (ctor, argType) =
        let value =
            match argType with
            | None -> VData (typeName, ctor, None)
            | Some (TTuple items) -> VConstructor (typeName, ctor, List.length items)
            | Some _ -> VConstructor (typeName, ctor, 1)
        Map.add ctor value env
    List.fold bindCtor env ctors

/// 레코드 타입 선언: 필드마다 "." + 필드 이름으로 타입을 환경에 추가 (사용자가 쓸 수 없는 이름이라 변수와 겹치지 않음).
/// 같은 필드 이름의 타입을 다시 선언하면 뒤의 것이 가림 (OCaml 과 같음)
let declareRecord (env: Env) (typeName: string) (fields: (string * TypeExpr) list) =
    let names = List.map fst fields
    names |> List.fold (fun acc f -> Map.add ("." + f) (VRecordType (typeName, names)) acc) env

/// 필드 이름으로 찾은 레코드 타입 (타입 이름, 선언한 필드 순서)
let recordTypeOf (env: Env) (field: string) =
    match Map.tryFind ("." + field) env with
    | Some (VRecordType (typeName, names)) -> typeName, names
    | _ -> failwithf "Unknown record field: %s" field

// ---- 평가 ----
// 식을 먼저 한 번 훑어서 변수마다 "몇 단계 바깥 함수의 몇 번째 칸"인지 정하고, 그 칸을 읽고 쓰는 F# 클로저(Code)로 바꾼 뒤 실행.
// 환경을 Map 으로 들고 다니면 let 과 호출마다 Map.add, 변수마다 문자열 비교로 찾기가 들어서, prelude 함수를 원소마다 부르는
// 프로그램이 느림 (100만 개 리스트 예제 5.3초). 실행 중 오류와 그 시점은 Map 으로 평가하던 때와 같음

/// 함수 하나(또는 최상위 식)의 칸 수. 본문을 다 훑은 뒤 정해지고, 호출할 때 그만큼 만듦
/// 비교와 논리 연산의 결과는 이 둘을 같이 씀 (결과마다 새로 만들지 않음)
let private vTrue = VBool true
let private vFalse = VBool false
let inline private ofBool b = if b then vTrue else vFalse
/// -256 ~ 1023 의 정수와 빈 리스트도 미리 만들어 둔 값을 씀
let private smallInts = Array.init 1280 (fun i -> VInt (i - 256))
let inline private ofInt n = if uint32 (n + 256) < 1280u then smallInts[n + 256] else VInt n
let private vNil = VList []

type private SlotCount() =
    member val Count = 0 with get, set
    member this.Next() =
        let n = this.Count
        this.Count <- n + 1
        n

/// 훑는 동안의 범위: 변수 → (정의한 함수의 깊이, 칸), 지금 함수의 깊이와 칸 수, 상수(내장 함수, 생성자, 앞 입력의 값)
type private Scope =
    { Vars: Map<string, int * int>
      Depth: int
      Slots: SlotCount
      Consts: Env }

type private Code = Frame -> Value

/// 지금 함수에 칸 하나를 더해 name 을 묶음
let private bindSlot (scope: Scope) (name: string) =
    let slot = scope.Slots.Next()
    slot, { scope with Vars = Map.add name (scope.Depth, slot) scope.Vars }

/// hops 단계 바깥 함수의 slot 번째 칸
let private access (hops: int) (slot: int) : Code =
    match hops with
    | 0 -> fun fr -> slotOf fr slot
    | 1 -> fun fr -> slotOf (parentOf fr) slot
    | 2 -> fun fr -> slotOf (parentOf (parentOf fr)) slot
    | _ ->
        fun fr ->
            let mutable f = fr
            for _ in 1 .. hops do
                f <- parentOf f
            slotOf f slot

let private asInt (code: Code) fr =
    match code fr with
    | VInt n -> n
    | v -> typeError "int" v

let private asBool (code: Code) fr =
    match code fr with
    | VBool b -> b
    | v -> typeError "bool" v

let private asString (code: Code) fr =
    match code fr with
    | VString s -> s
    | v -> typeError "string" v

let private asList (code: Code) fr =
    match code fr with
    | VList items -> items
    | v -> typeError "list" v

/// < > <= >= 는 int 끼리 또는 string 끼리 (문자 코드 순서) 비교. int 끼리는 compareValues 를 거치지 않음
let inline private orderTest (a: Code) (b: Code) ([<InlineIfLambda>] op: int -> int -> bool) ([<InlineIfLambda>] test: int -> bool) : Frame -> bool =
    fun fr ->
        match a fr with
        | VInt x ->
            match b fr with
            | VInt y -> op x y
            | v -> typeError "int" v
        | VString _ as x -> test (compareValues x (b fr))
        | v -> typeError "int or string" v

/// - * / % : int 끼리
let inline private arith (a: Code) (b: Code) ([<InlineIfLambda>] op: int -> int -> int) : Code =
    fun fr ->
        let x = asInt a fr
        ofInt (op x (asInt b fr))

let rec private patternNames pattern =
    match pattern with
    | PVar name -> [ name ]
    | PCons (h, t) -> patternNames h @ patternNames t
    | PTuple items -> List.collect patternNames items
    | PRecord fields -> List.collect (snd >> patternNames) fields
    | PCtor (_, Some p) -> patternNames p
    | POr (a, _) -> patternNames a
    | PWildcard | PInt _ | PBool _ | PString _ | PNil | PUnit | PCtor (_, None) -> []

/// 패턴 → 값이 맞는지 보고 묶는 이름의 칸에 쓰는 함수. 이름의 칸은 scope 에 이미 있음 (compileArm).
/// 생성자가 없거나 인자 유무가 틀리면 그 패턴을 시도할 때 오류 (Map 으로 평가하던 matchPattern 과 같음)
let rec private compilePattern (scope: Scope) (pattern: Pattern) : Value -> Frame -> bool =
    let sub = compilePattern scope
    match pattern with
    | PVar name ->
        let _, slot = scope.Vars[name]
        fun v fr ->
            setSlot fr slot v
            true
    | PWildcard -> fun _ _ -> true
    | PUnit -> fun v _ -> v = VUnit
    // 왼쪽부터 처음 일치하는 쪽. 두 쪽이 같은 칸에 씀
    | POr (a, b) ->
        let a, b = sub a, sub b
        fun v fr -> a v fr || b v fr
    | PInt n -> fun v _ -> (match v with VInt m -> m = n | _ -> false)
    | PBool b -> fun v _ -> (match v with VBool c -> c = b | _ -> false)
    | PString s -> fun v _ -> (match v with VString t -> t = s | _ -> false)
    | PNil -> fun v _ -> (match v with VList [] -> true | _ -> false)
    | PCons (h, t) ->
        let h, t = sub h, sub t
        fun v fr ->
            match v with
            | VList (x :: rest) -> h x fr && t (VList rest) fr
            | _ -> false
    | PTuple items ->
        let items = items |> List.map sub |> Array.ofList
        fun v fr ->
            match v with
            | VTuple vs when List.length vs = items.Length ->
                let mutable rest = vs
                let mutable ok = true
                let mutable i = 0
                while ok && i < items.Length do
                    ok <- items[i] rest.Head fr
                    rest <- rest.Tail
                    i <- i + 1
                ok
            | _ -> false
    | PRecord fields ->
        let fields = fields |> List.map (fun (f, p) -> f, sub p)
        fun v fr ->
            match v with
            | VRecord (_, values) ->
                fields
                |> List.forall (fun (f, m) ->
                    match List.tryFind (fun (n, _) -> n = f) values with
                    | Some (_, x) -> m x fr
                    | None -> false)
            | _ -> false
    | PCtor (name, argPattern) ->
        // 오타 난 생성자가 조용히 "불일치"가 되지 않도록, 지금 보이는 생성자인지와 인자 유무를 먼저 확인
        let typeName =
            match Map.tryFind name scope.Consts, argPattern with
            | Some (VData (t, _, None)), None -> Ok t
            | Some (VConstructor (t, _, _)), Some _ -> Ok t
            | Some (VData _), Some _ -> Error (sprintf "Constructor %s takes no argument" name)
            | Some (VConstructor _), None -> Error (sprintf "Constructor %s requires an argument" name)
            | _ -> Error (sprintf "Unknown constructor: %s" name)
        let arg = argPattern |> Option.map sub
        match typeName with
        | Error message -> fun _ _ -> failwith message
        | Ok typeName ->
            fun v fr ->
                match v, arg with
                | VData (t, c, None), None -> t = typeName && c = name
                | VData (t, c, Some payload), Some p -> t = typeName && c = name && p payload fr
                | _ -> false

/// match, try 의 분기: (패턴, 가드, 본문). 패턴이 묶는 이름마다 칸 하나
let rec private compileArm (scope: Scope) (pattern: Pattern, guard: Expr option, body: Expr) =
    let inner = patternNames pattern |> List.distinct |> List.fold (fun s name -> snd (bindSlot s name)) scope
    compilePattern inner pattern, guard |> Option.map (compile inner), compile inner body

/// 위에서부터 패턴과 가드가 모두 맞는 첫 분기의 번호. 없으면 -1
and private findArm (arms: ((Value -> Frame -> bool) * Code option * Code)[]) (v: Value) (fr: Frame) =
    let mutable found = -1
    let mutable i = 0
    while found < 0 && i < arms.Length do
        let m, guard, _ = arms[i]
        if m v fr && (match guard with None -> true | Some g -> asBool g fr) then found <- i
        i <- i + 1
    found

/// fun p1 -> fun p2 -> body: 인자 여럿의 함수 하나 (칸 0, 1, … 에 인자). 인자 표기(let a = (a : t) in)는 값이 그대로라 건너뜀
and private lambda (scope: Scope) (param: string) (body: Expr) : Code =
    let rec chain (parameters: string list) (body: Expr) =
        match body with
        | Lambda (p, rest) -> chain (parameters @ [ p ]) rest
        | Let (name, Annot (Var same, _), rest) when name = same && List.last parameters = name -> chain parameters rest
        | _ -> parameters, body
    let parameters, body = chain [ param ] body
    let slots = SlotCount()
    let vars = parameters |> List.fold (fun acc p -> Map.add p (scope.Depth + 1, slots.Next()) acc) scope.Vars
    let code = compile { scope with Vars = vars; Depth = scope.Depth + 1; Slots = slots } body
    let arity = parameters.Length
    fun fr -> VClosure (Closure(arity, slots.Count, fr, code, []))

and private compile (scope: Scope) (expr: Expr) : Code =
    let c = compile scope
    match expr with
    | Number n ->
        let v = VInt n
        fun _ -> v
    | Bool b ->
        let v = VBool b
        fun _ -> v
    | Str s ->
        let v = VString s
        fun _ -> v
    | Unit -> fun _ -> VUnit
    | Annot (e, _) -> c e
    | Seq (first, rest) ->
        let first, rest = c first, c rest
        fun fr ->
            first fr |> ignore
            rest fr
    | Var name ->
        match Map.tryFind name scope.Vars with
        | Some (depth, slot) -> access (scope.Depth - depth) slot
        | None ->
            match Map.tryFind name scope.Consts with
            | Some v -> fun _ -> v
            | None -> fun _ -> failwithf "Unbound variable: %s" name
    | Let (name, value, body) ->
        let value = c value
        let slot, inner = bindSlot scope name
        let body = compile inner body
        fun fr ->
            setSlot fr slot (value fr)
            body fr
    // 본문의 name 은 지금 칸을 가리키므로, 함수를 만든 뒤 그 칸에 넣으면 자기 자신
    | LetRec (name, param, body, inExpr) ->
        let slot, inner = bindSlot scope name
        let f = lambda inner param body
        let rest = compile inner inExpr
        fun fr ->
            setSlot fr slot (f fr)
            rest fr
    | LetRecs (bindings, inExpr) ->
        let inner, slots =
            bindings
            |> List.fold (fun (s, acc) (name, _, _) -> let slot, s = bindSlot s name in s, acc @ [ slot ]) (scope, [])
        let fs = bindings |> List.map (fun (_, param, body) -> lambda inner param body) |> Array.ofList
        let slots = Array.ofList slots
        let rest = compile inner inExpr
        fun fr ->
            for i in 0 .. fs.Length - 1 do
                setSlot fr (slots[i]) (fs[i] fr)
            rest fr
    | Lambda (param, body) -> lambda scope param body
    // 값에 의한 호출: 함수와 인자를 먼저 평가한 뒤 본문을 평가. f a b 는 (f a) b 와 같은 순서:
    // 인자 수가 같은 함수면 중간 함수 없이 바로 부름 (partial application 은 부수 효과가 없으므로 순서가 같음)
    | App _ ->
        let rec spine e args =
            match e with
            | App (f, a) -> spine f (a :: args)
            | _ -> e, args
        let head, args = spine expr []
        let f = c head
        let args = args |> List.map c |> Array.ofList
        match saturatedBuiltin scope head args with
        | Some code -> code
        | None ->
        match args with
        | [| arg |] ->
            fun fr ->
                let fv = f fr
                let av = arg fr
                match fv with
                | VClosure cl when cl.Arity = 1 && cl.Bound.IsEmpty ->
                    let frame = newFrame cl.Size cl.Parent
                    setSlot frame 0 av
                    cl.Code frame
                | _ -> apply fv av
        | _ ->
            let n = args.Length
            fun fr ->
                match f fr with
                | VClosure cl when cl.Arity = n && cl.Bound.IsEmpty ->
                    let frame = newFrame cl.Size cl.Parent
                    for i in 0 .. n - 1 do
                        setSlot frame i (args[i] fr)
                    cl.Code frame
                | fv ->
                    let mutable g = fv
                    for i in 0 .. n - 2 do
                        g <- apply g (args[i] fr)
                    apply g (args[n - 1] fr)
    | If (cond, thenExpr, elseExpr) ->
        let cond, t, e = test scope cond, c thenExpr, c elseExpr
        fun fr -> if cond fr then t fr else e fr
    // + 는 int 덧셈 또는 string 연결. 왼쪽 값의 타입으로 정함
    | Add (a, b) ->
        let a, b = c a, c b
        fun fr ->
            match a fr with
            | VInt x -> ofInt (x + asInt b fr)
            | VString x -> VString (x + asString b fr)
            | v -> typeError "int or string" v
    | Subtract (a, b) -> arith (c a) (c b) (fun x y -> x - y)
    | Multiply (a, b) -> arith (c a) (c b) (fun x y -> x * y)
    | Divide (a, b) -> arith (c a) (c b) (fun x y -> x / y)
    | Modulo (a, b) -> arith (c a) (c b) (fun x y -> x % y)
    | Negate e ->
        let e = c e
        fun fr -> VInt -(asInt e fr)
    | Equal _ | NotEqual _ | Less _ | Greater _ | LessEqual _ | GreaterEqual _ | And _ | Or _ | Not _ ->
        let t = test scope expr
        fun fr -> ofBool (t fr)
    | Nil -> fun _ -> vNil
    | Cons (head, tail) ->
        let head, tail = c head, c tail
        fun fr ->
            let h = head fr
            VList (h :: asList tail fr)
    | Tuple items ->
        let items = List.map c items
        fun fr -> VTuple [ for item in items -> item fr ]
    | Match (scrutinee, cases) ->
        let scrutinee = c scrutinee
        let arms = cases |> List.map (compileArm scope) |> Array.ofList
        fun fr ->
            let v = scrutinee fr
            match findArm arms v fr with
            | -1 -> matchFailure v
            | i ->
                let _, _, body = arms[i]
                body fr
    | LetPattern (pattern, value, body) ->
        let value = c value
        let m, _, body = compileArm scope (pattern, None, body)
        fun fr ->
            let v = value fr
            if m v fr then body fr else matchFailure v
    | Ctor name ->
        match Map.tryFind name scope.Consts with
        | Some (VData _ | VConstructor _ as v) -> fun _ -> v
        | _ -> fun _ -> failwithf "Unknown constructor: %s" name
    | TypeDecl (typeName, _, ctors, body) -> compile { scope with Consts = declareConstructors scope.Consts typeName ctors } body
    | RecordDecl (typeName, _, fields, body) -> compile { scope with Consts = declareRecord scope.Consts typeName fields } body
    | ExceptionDecl (name, argType, body) -> compile { scope with Consts = declareConstructors scope.Consts "exn" [ name, argType ] } body
    // 쓴 순서대로 계산한 뒤 선언한 순서로 놓음. 필드가 빠지거나 다른 타입의 필드면 오류 (타입 검사가 먼저 막음)
    | Record fields ->
        let recordType = try Ok (recordTypeOf scope.Consts (fst fields.Head)) with ex -> Error ex.Message
        let fields = fields |> List.map (fun (f, e) -> f, c e)
        fun fr ->
            match recordType with
            | Error message -> failwith message
            | Ok (typeName, names) ->
                let values = fields |> List.map (fun (f, e) -> f, e fr)
                for f, _ in fields do
                    if not (List.contains f names) then failwithf "The record field %s does not belong to type %s" f typeName
                let missing = names |> List.filter (fun n -> not (List.exists (fun (f, _) -> f = n) fields))
                if not missing.IsEmpty then failwithf "Missing record field: %s" (String.concat ", " missing)
                VRecord (typeName, names |> List.map (fun n -> n, List.find (fun (f, _) -> f = n) values |> snd))
    | Field (e, field) ->
        let e = c e
        fun fr ->
            match e fr with
            | VRecord (_, values) as v ->
                match List.tryFind (fun (f, _) -> f = field) values with
                | Some (_, x) -> x
                | None -> failwithf "Type error: the record %s has no field %s" (formatValue v) field
            | v -> typeError "record" v
    | RecordUpdate (e, fields) ->
        let e = c e
        let fields = fields |> List.map (fun (f, x) -> f, c x)
        fun fr ->
            match e fr with
            | VRecord (typeName, values) as v ->
                let updates = fields |> List.map (fun (f, x) -> f, x fr)
                for f, _ in updates do
                    if not (List.exists (fun (n, _) -> n = f) values) then
                        failwithf "Type error: the record %s has no field %s" (formatValue v) f
                VRecord (typeName, values |> List.map (fun (n, old) -> n, defaultArg (updates |> List.tryFind (fun (f, _) -> f = n) |> Option.map snd) old))
            | v -> typeError "record" v
    // 포맷의 함수는 한 번 만들어 둠 (포맷 오류는 평가할 때마다)
    | Printf (kind, format) ->
        let made = lazy (formatFunction kind format)
        fun _ -> made.Force()
    // 본문에서 raise 된 예외만 받음 (0으로 나누기 같은 실행 중 오류는 받지 않음). 맞는 분기가 없으면 같은 값을 다시 raise
    | Try (body, cases) ->
        let body = c body
        let arms = cases |> List.map (compileArm scope) |> Array.ofList
        fun fr ->
            try
                body fr
            with CalcException exn ->
                match findArm arms exn fr with
                | -1 -> raise (CalcException exn)
                | i ->
                    let _, _, handler = arms[i]
                    handler fr

/// 인자가 다 모인 내장 함수 호출 (f 가 범위의 변수가 아니라 내장 함수일 때): VBuiltin 에 인자를 하나씩 모으지 않고
/// 인자를 모두 평가한 뒤 바로 부름. partial application 은 부수 효과가 없으므로 계산 순서와 오류는 같음.
/// !, :=, a.[i], a.[i] <- v 는 이름으로 고르는 callBuiltin 도 거치지 않음
and private saturatedBuiltin (scope: Scope) (head: Expr) (args: Code[]) : Code option =
    let builtin =
        match head with
        | Var name when not (scope.Vars.ContainsKey name) ->
            match Map.tryFind name scope.Consts with
            | Some (VBuiltin (n, [])) when not (Format.kinds.Contains n) && builtinArity[n] = args.Length -> Some n
            | _ -> None
        | _ -> None
    let outOfBounds () = failwith "Index was outside the bounds of the array."
    match builtin, args with
    | None, _ -> None
    | Some "!", [| r |] ->
        Some (fun fr ->
            match r fr with
            | VRef cell -> cell.Value
            | v -> typeError "ref" v)
    | Some ":=", [| r; x |] ->
        Some (fun fr ->
            let r = r fr
            let x = x fr
            match r with
            | VRef cell ->
                cell.Value <- x
                VUnit
            | v -> typeError "ref" v)
    | Some ".[]", [| a; i |] ->
        Some (fun fr ->
            let a = a fr
            let i = i fr
            match a with
            | VArray items ->
                let i = int' i
                if i < 0 || i >= items.Length then outOfBounds ()
                items[i]
            | v -> typeError "array" v)
    | Some ".[]<-", [| a; i; x |] ->
        Some (fun fr ->
            let a = a fr
            let i = i fr
            let x = x fr
            match a with
            | VArray items ->
                let i = int' i
                if i < 0 || i >= items.Length then outOfBounds ()
                items[i] <- x
                VUnit
            | v -> typeError "array" v)
    | Some name, _ -> Some (fun fr -> callBuiltin name [ for a in args -> a fr ])

/// 조건과 비교, 논리 연산: 중간 결과로 VBool 을 만들지 않고 bool 을 돌려줌 (if 의 조건은 이것만 씀)
and private test (scope: Scope) (expr: Expr) : Frame -> bool =
    let c = compile scope
    match expr with
    | Less (a, b) -> orderTest (c a) (c b) (fun x y -> x < y) (fun n -> n < 0)
    | Greater (a, b) -> orderTest (c a) (c b) (fun x y -> x > y) (fun n -> n > 0)
    | LessEqual (a, b) -> orderTest (c a) (c b) (fun x y -> x <= y) (fun n -> n <= 0)
    | GreaterEqual (a, b) -> orderTest (c a) (c b) (fun x y -> x >= y) (fun n -> n >= 0)
    | Equal (a, b) ->
        let a, b = c a, c b
        fun fr ->
            match a fr with
            | VInt x -> (match b fr with VInt y -> x = y | v -> typeError "int" v)
            | x -> valueEquals x (b fr)
    | NotEqual (a, b) ->
        let a, b = c a, c b
        fun fr ->
            match a fr with
            | VInt x -> (match b fr with VInt y -> x <> y | v -> typeError "int" v)
            | x -> not (valueEquals x (b fr))
    // && 와 || 는 단락 평가: 왼쪽으로 결과가 정해지면 오른쪽은 평가하지 않음
    | And (a, b) ->
        let a, b = test scope a, test scope b
        fun fr -> a fr && b fr
    | Or (a, b) ->
        let a, b = test scope a, test scope b
        fun fr -> a fr || b fr
    | Not e ->
        let e = test scope e
        fun fr -> not (e fr)
    | _ ->
        let e = c expr
        fun fr -> asBool e fr

and apply fv av =
    match fv with
    // 인자가 다 모이면 Frame 을 만들어 본문 실행, 아니면 받은 인자를 더한 함수
    | VClosure cl ->
        if cl.Arity = 1 then
            let frame = newFrame cl.Size cl.Parent
            let mutable i = 0
            for b in cl.Bound do
                setSlot frame i b
                i <- i + 1
            setSlot frame i av
            cl.Code frame
        else VClosure (Closure(cl.Arity - 1, cl.Size, cl.Parent, cl.Code, cl.Bound @ [ av ]))
    | VBuiltin (name, []) when Format.kinds.Contains name ->
        match av with
        | VString format -> formatFunction name format
        | v -> typeError "string" v
    | VBuiltin (name, args) ->
        let args = args @ [ av ]
        if List.length args = builtinArity.[name] then callBuiltin name args
        else VBuiltin (name, args)
    | VConstructor (typeName, ctor, fields) ->
        // of int * int 처럼 원소가 여럿이면 그 수만큼의 튜플을 받아야 함
        match av with
        | _ when fields = 1 -> VData (typeName, ctor, Some av)
        | VTuple items when List.length items = fields -> VData (typeName, ctor, Some av)
        | v -> failwithf "Constructor %s expects %d fields but got %s" ctor fields (describe v)
    | v -> typeError "function" v

/// 포맷 문자열의 함수 (Format.expand). 타입 검사를 건너뛰면 잘못된 포맷이 여기까지 옴
and private formatFunction (kind: string) (format: string) =
    match Format.parse format with
    | Ok pieces -> evalIn builtins (Format.expand kind pieces)
    | Error message -> failwith message

/// env 의 이름을 상수로 보고 평가 (REPL 은 앞 입력의 값, 프로그램은 내장 함수)
and evalIn (env: Env) (expr: Expr) : Value =
    let slots = SlotCount()
    let code = compile { Vars = Map.empty; Depth = 0; Slots = slots; Consts = env } expr
    code (newFrame slots.Count null)

/// let rec f x = ... and g y = ...: 그룹의 함수를 만들어 환경에 추가 (REPL 의 최상위 let rec)
let declareRecGroup (env: Env) (bindings: (string * string * Expr) list) =
    match evalIn env (LetRecs (bindings, Tuple (bindings |> List.map (fun (name, _, _) -> Var name)))) with
    | VTuple values -> List.fold2 (fun acc (name, _, _) v -> Map.add name v acc) env bindings values
    | _ -> failwith "unreachable"

/// 내장 함수만 있는 환경에서 평가
let eval expr = evalIn builtins expr

/// action 안에서 print, println 의 출력을 writer 로 보냄 (그 안에서 시작한 스레드도 같음)
let withOutput (writer: System.IO.TextWriter) (action: unit -> 'a) : 'a =
    let previous = outputSlot.Value
    outputSlot.Value <- writer
    try
        action ()
    finally
        outputSlot.Value <- previous

/// print, println 의 출력을 writer 로 보내면서 평가 (테스트)
let evalWithOutput (writer: System.IO.TextWriter) expr = withOutput writer (fun () -> eval expr)
