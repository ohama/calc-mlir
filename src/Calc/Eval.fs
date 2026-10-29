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
    /// ref v: 내용을 바꿀 수 있는 칸 하나 (!r 로 읽고 r := v 로 씀)
    | VRef of Value ref
    /// { x = 1; y = 2 }. fields 는 선언한 순서
    | VRecord of typeName: string * fields: (string * Value) list
    /// 레코드 타입의 선언 (환경에만 있는 값): 환경의 "." + 필드 이름이 이 값. 필드 이름으로 타입과 필드 순서를 찾음
    | VRecordType of typeName: string * fields: string list

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
    | VData (_, ctor, Some payload) -> applied ctor payload
    | VRef cell -> applied "ref" cell.Value
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
    | VClosure _ | VRecClosure _ | VRecGroup _ | VBuiltin _ | VConstructor _ -> "function"
    | VList _ -> "list"
    | VTuple _ -> "tuple"
    | VData (typeName, _, _) -> typeName
    | VRef _ -> "ref"
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
    // 같은 타입끼리: 필드를 선언한 순서로 비교
    | VRecord (t1, xs), VRecord (t2, ys) when t1 = t2 -> List.forall2 (fun (_, x) (_, y) -> valueEquals x y) xs ys
    | VInt _, v -> typeError "int" v
    | VBool _, v -> typeError "bool" v
    | VString _, v -> typeError "string" v
    | VList _, v -> typeError "list" v
    | VTuple _, v -> typeError "tuple" v
    | VData (t, _, _), v -> typeError t v
    | VRef _, v -> typeError "ref" v
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
    | RecordDecl (typeName, _, fields, body) -> evalIn (declareRecord env typeName fields) body
    // 쓴 순서대로 계산한 뒤 선언한 순서로 놓음. 필드가 빠지거나 다른 타입의 필드면 오류 (타입 검사가 먼저 막음)
    | Record fields ->
        let typeName, names = recordTypeOf env (fst fields.Head)
        let values = fields |> List.map (fun (f, e) -> f, evalIn env e)
        for f, _ in fields do
            if not (List.contains f names) then failwithf "The record field %s does not belong to type %s" f typeName
        let missing = names |> List.filter (fun n -> not (List.exists (fun (f, _) -> f = n) fields))
        if not missing.IsEmpty then failwithf "Missing record field: %s" (String.concat ", " missing)
        VRecord (typeName, names |> List.map (fun n -> n, List.find (fun (f, _) -> f = n) values |> snd))
    | Field (e, field) ->
        match evalIn env e with
        | VRecord (_, values) as v ->
            match List.tryFind (fun (f, _) -> f = field) values with
            | Some (_, x) -> x
            | None -> failwithf "Type error: the record %s has no field %s" (formatValue v) field
        | v -> typeError "record" v
    | RecordUpdate (e, fields) ->
        match evalIn env e with
        | VRecord (typeName, values) as v ->
            let updates = fields |> List.map (fun (f, x) -> f, evalIn env x)
            for f, _ in updates do
                if not (List.exists (fun (n, _) -> n = f) values) then
                    failwithf "Type error: the record %s has no field %s" (formatValue v) f
            VRecord (typeName, values |> List.map (fun (n, old) -> n, defaultArg (updates |> List.tryFind (fun (f, _) -> f = n) |> Option.map snd) old))
        | v -> typeError "record" v
    | ExceptionDecl (name, argType, body) -> evalIn (declareConstructors env "exn" [ name, argType ]) body
    | Printf (kind, format) -> formatFunction kind format
    // 본문에서 raise 된 예외만 받음 (0으로 나누기 같은 실행 중 오류는 받지 않음). 맞는 분기가 없으면 같은 값을 다시 raise
    | Try (body, cases) ->
        try
            evalIn env body
        with CalcException exn ->
            let rec handle cases =
                match cases with
                | [] -> raise (CalcException exn)
                | (pattern, guard, handler) :: rest ->
                    match matchPattern pattern exn env with
                    | Some caseEnv when (match guard with None -> true | Some g -> evalBool caseEnv g) ->
                        evalIn caseEnv handler
                    | _ -> handle rest
            handle cases

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
