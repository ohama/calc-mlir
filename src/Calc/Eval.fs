module Eval

open Ast

type Value =
    | VInt of int
    | VBool of bool
    | VString of string
    /// fun param -> body, 정의된 시점의 환경을 함께 저장 (정적 범위)
    | VClosure of param: string * body: Expr * env: Map<string, Value>
    /// let rec name param = body. 호출할 때 name 을 자기 자신으로 환경에 추가
    | VRecClosure of name: string * param: string * body: Expr * env: Map<string, Value>
    /// let rec f x = ... and g y = ... 의 index 번째 함수. 호출할 때 그룹의 모든 이름을 환경에 추가
    | VRecGroup of index: int * bindings: (string * string * Expr) list * env: Map<string, Value>
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
    | VClosure _ | VRecClosure _ | VRecGroup _ | VBuiltin _ | VConstructor _ -> "<fun>"
    | VList items -> "[" + (items |> List.map formatValue |> String.concat "; ") + "]"
    | VTuple items -> "(" + (items |> List.map formatValue |> String.concat ", ") + ")"
    | VData (_, ctor, None) -> ctor
    | VData (_, ctor, Some payload) ->
        // 다시 입력했을 때 같은 값이 되도록 괄호: Some (Some 1), Some (-1). 튜플은 이미 괄호가 있음
        match payload with
        | VData (_, _, Some _) -> sprintf "%s (%s)" ctor (formatValue payload)
        | VInt n when n < 0 -> sprintf "%s (%s)" ctor (formatValue payload)
        | _ -> sprintf "%s %s" ctor (formatValue payload)

let typeName value =
    match value with
    | VUnit -> "unit"
    | VInt _ -> "int"
    | VBool _ -> "bool"
    | VString _ -> "string"
    | VClosure _ | VRecClosure _ | VRecGroup _ | VBuiltin _ | VConstructor _ -> "function"
    | VList _ -> "list"
    | VTuple _ -> "tuple"
    | VData (typeName, _, _) -> typeName

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
    | VInt _, v -> typeError "int" v
    | VBool _, v -> typeError "bool" v
    | VString _, v -> typeError "string" v
    | VList _, v -> typeError "list" v
    | VTuple _, v -> typeError "tuple" v
    | VData (t, _, _), v -> typeError t v
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
        "head", 1
        "tail", 1
        "isEmpty", 1
        "nth", 2
        "reverse", 1
        "append", 2
        "range", 2
        "sum", 1
        "sort", 1
        "map", 2
        "filter", 2
        "fold", 3
        "exists", 2
        "forall", 2
        // 튜플
        "fst", 1
        "snd", 1
        // option
        "tryHead", 1
        "tryFind", 2
        "defaultValue", 2
        // 출력: 문자열은 따옴표 없이, 나머지는 formatValue (toString 과 같음)
        "print", 1
        "println", 1
        "ignore", 1
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

/// 인자가 다 모인 내장 함수를 실행. 문자열 위치와 길이는 UTF-16 코드 단위 (.NET string 과 같음)
/// apply 는 map, filter 같은 고차 함수가 사용자 함수를 호출할 때 사용 (evalIn 보다 먼저 정의되므로 인자로 받음)
let callBuiltin (apply: Value -> Value -> Value) name args =
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
    // 리스트: 고차 함수는 F# List 처럼 함수가 먼저, 나머지는 리스트가 먼저
    | "head", [ xs ] ->
        match list' xs with
        | h :: _ -> h
        | [] -> failwith "head: empty list"
    | "tail", [ xs ] ->
        match list' xs with
        | _ :: t -> VList t
        | [] -> failwith "tail: empty list"
    | "isEmpty", [ xs ] -> VBool (List.isEmpty (list' xs))
    | "nth", [ xs; i ] ->
        let xs, i = list' xs, int' i
        if i < 0 || i >= List.length xs then
            failwithf "nth: index out of range (length %d, index %d)" (List.length xs) i
        List.item i xs
    | "reverse", [ xs ] -> VList (List.rev (list' xs))
    | "append", [ xs; ys ] -> VList (list' xs @ list' ys)
    | "range", [ a; b ] -> VList ([ int' a .. int' b ] |> List.map VInt)
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
    | "map", [ f; xs ] -> VList (list' xs |> List.map (apply f))
    | "filter", [ f; xs ] -> VList (list' xs |> List.filter (fun x -> bool' (apply f x)))
    | "fold", [ f; acc; xs ] -> list' xs |> List.fold (fun acc x -> apply (apply f acc) x) acc
    | "exists", [ f; xs ] -> VBool (list' xs |> List.exists (fun x -> bool' (apply f x)))
    | "forall", [ f; xs ] -> VBool (list' xs |> List.forall (fun x -> bool' (apply f x)))
    | "fst", [ VTuple [ a; _ ] ] -> a
    | "snd", [ VTuple [ _; b ] ] -> b
    | ("fst" | "snd"), [ v ] -> typeError "pair" v
    | "tryHead", [ xs ] ->
        match list' xs with
        | h :: _ -> some h
        | [] -> none
    | "tryFind", [ f; xs ] ->
        match list' xs |> List.tryFind (fun x -> bool' (apply f x)) with
        | Some x -> some x
        | None -> none
    | "print", [ v ] ->
        output().Write(display v)
        VUnit
    | "println", [ v ] ->
        output().WriteLine(display v)
        VUnit
    | "ignore", [ _ ] -> VUnit
    | "defaultValue", [ d; opt ] ->
        match opt with
        | VData ("option", "Some", Some v) -> v
        | VData ("option", "None", None) -> d
        | v -> typeError "option" v
    | _ -> failwithf "Unknown builtin: %s" name

/// 처음 환경. 내장 함수는 보통 변수처럼 let 으로 가릴 수 있음
let builtins: Env =
    builtinArity
    |> Map.map (fun name _ -> VBuiltin (name, []))
    // 'a option = None | Some of 'a 는 선언 없이 쓸 수 있음
    |> Map.add "None" none
    |> Map.add "Some" (VConstructor ("option", "Some", 1))

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

/// let rec ... and ...: 그룹의 함수마다 VRecGroup 값을 환경에 추가 (서로를 부를 수 있음)
let declareRecGroup (env: Env) (bindings: (string * string * Expr) list) =
    bindings |> List.indexed |> List.fold (fun acc (i, (name, _, _)) -> Map.add name (VRecGroup (i, bindings, env)) acc) env

let rec evalIn (env: Env) expr =
    match expr with
    | Number n -> VInt n
    | Bool b -> VBool b
    | Str s -> VString s
    | Unit -> VUnit
    | Annot (e, _) -> evalIn env e
    | Seq (first, rest) ->
        evalIn env first |> ignore
        evalIn env rest
    | Var name ->
        match Map.tryFind name env with
        | Some v -> v
        | None -> failwithf "Unbound variable: %s" name
    | Let (name, value, body) ->
        let v = evalIn env value
        evalIn (Map.add name v env) body
    | LetRec (name, param, body, inExpr) ->
        evalIn (Map.add name (VRecClosure (name, param, body, env)) env) inExpr
    | LetRecs (bindings, inExpr) -> evalIn (declareRecGroup env bindings) inExpr
    | Lambda (param, body) -> VClosure (param, body, env)
    | App (f, arg) ->
        // 값에 의한 호출: 함수와 인자를 먼저 평가한 뒤 본문을 평가
        let fv = evalIn env f
        let av = evalIn env arg
        apply fv av
    | If (cond, thenExpr, elseExpr) ->
        if evalBool env cond then evalIn env thenExpr else evalIn env elseExpr
    // + 는 int 덧셈 또는 string 연결. 왼쪽 값의 타입으로 정함
    | Add (a, b) ->
        match evalIn env a with
        | VInt x -> VInt (x + evalInt env b)
        | VString x -> VString (x + evalString env b)
        | v -> typeError "int or string" v
    | Subtract (a, b) -> VInt (evalInt env a - evalInt env b)
    | Multiply (a, b) -> VInt (evalInt env a * evalInt env b)
    | Divide (a, b) -> VInt (evalInt env a / evalInt env b)
    | Modulo (a, b) -> VInt (evalInt env a % evalInt env b)
    | Negate e -> VInt -(evalInt env e)
    | Equal (a, b) -> VBool (evalEquals env a b)
    | NotEqual (a, b) -> VBool (not (evalEquals env a b))
    | Less (a, b) -> VBool (evalCompare env a b < 0)
    | Greater (a, b) -> VBool (evalCompare env a b > 0)
    | LessEqual (a, b) -> VBool (evalCompare env a b <= 0)
    | GreaterEqual (a, b) -> VBool (evalCompare env a b >= 0)
    // && 와 || 는 단락 평가: 왼쪽으로 결과가 정해지면 오른쪽은 평가하지 않음
    | And (a, b) -> VBool (evalBool env a && evalBool env b)
    | Or (a, b) -> VBool (evalBool env a || evalBool env b)
    | Not e -> VBool (not (evalBool env e))
    | Nil -> VList []
    | Cons (head, tail) ->
        let h = evalIn env head
        VList (h :: evalList env tail)
    | Tuple items -> VTuple (items |> List.map (evalIn env))
    | Match (scrutinee, cases) ->
        let value = evalIn env scrutinee
        // 위에서부터 패턴과 가드가 모두 맞는 첫 분기
        let rec tryCases cases =
            match cases with
            | [] -> matchFailure value
            | (pattern, guard, body) :: rest ->
                match matchPattern pattern value env with
                | Some caseEnv when (match guard with None -> true | Some g -> evalBool caseEnv g) ->
                    evalIn caseEnv body
                | _ -> tryCases rest
        tryCases cases
    | LetPattern (pattern, value, body) ->
        let v = evalIn env value
        match matchPattern pattern v env with
        | Some bodyEnv -> evalIn bodyEnv body
        | None -> matchFailure v
    | Ctor name ->
        match Map.tryFind name env with
        | Some (VData _ | VConstructor _ as v) -> v
        | _ -> failwithf "Unknown constructor: %s" name
    | TypeDecl (typeName, _, ctors, body) -> evalIn (declareConstructors env typeName ctors) body

and evalInt env expr =
    match evalIn env expr with
    | VInt n -> n
    | v -> typeError "int" v

and evalBool env expr =
    match evalIn env expr with
    | VBool b -> b
    | v -> typeError "bool" v

and evalString env expr =
    match evalIn env expr with
    | VString s -> s
    | v -> typeError "string" v

/// < > <= >= 는 int 끼리 또는 string 끼리 (문자 코드 순서) 비교
and evalCompare env a b =
    match evalIn env a with
    | VInt _ | VString _ as x -> compareValues x (evalIn env b)
    | v -> typeError "int or string" v

and evalList env expr =
    match evalIn env expr with
    | VList items -> items
    | v -> typeError "list" v

and evalEquals env a b =
    let x = evalIn env a
    valueEquals x (evalIn env b)

and apply fv av =
    match fv with
    | VClosure (param, body, closureEnv) ->
        evalIn (Map.add param av closureEnv) body
    | VRecClosure (name, param, body, closureEnv) ->
        let env = closureEnv |> Map.add name fv |> Map.add param av
        evalIn env body
    | VRecGroup (index, bindings, closureEnv) ->
        let _, param, body = bindings[index]
        evalIn (declareRecGroup closureEnv bindings |> Map.add param av) body
    | VBuiltin (name, args) ->
        let args = args @ [ av ]
        if List.length args = builtinArity.[name] then callBuiltin apply name args
        else VBuiltin (name, args)
    | VConstructor (typeName, ctor, fields) ->
        // of int * int 처럼 원소가 여럿이면 그 수만큼의 튜플을 받아야 함
        match av with
        | _ when fields = 1 -> VData (typeName, ctor, Some av)
        | VTuple items when List.length items = fields -> VData (typeName, ctor, Some av)
        | v -> failwithf "Constructor %s expects %d fields but got %s" ctor fields (describe v)
    | v -> typeError "function" v

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
