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
    | VList of Value list
    /// 내장 함수. args 는 지금까지 받은 인자 (커링: 인자가 다 모이면 실행)
    | VBuiltin of name: string * args: Value list

type Env = Map<string, Value>

/// 문자열을 리터럴 형태로 출력 (렉서가 받는 이스케이프로 되돌림)
let quote (s: string) =
    let escaped =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\r", "\\r")
    "\"" + escaped + "\""

let rec formatValue value =
    match value with
    | VInt n -> string n
    | VBool true -> "true"
    | VBool false -> "false"
    | VString s -> quote s
    | VClosure _ | VRecClosure _ | VBuiltin _ -> "<fun>"
    | VList items -> "[" + (items |> List.map formatValue |> String.concat "; ") + "]"

let typeName value =
    match value with
    | VInt _ -> "int"
    | VBool _ -> "bool"
    | VString _ -> "string"
    | VClosure _ | VRecClosure _ | VBuiltin _ -> "function"
    | VList _ -> "list"

let typeError expected value =
    failwithf "Type error: expected %s but got %s" expected (typeName value)

/// = 와 <> 는 같은 타입끼리만 비교 가능. 리스트는 원소를 차례로 비교
let rec valueEquals a b =
    match a, b with
    | VInt x, VInt y -> x = y
    | VBool x, VBool y -> x = y
    | VString x, VString y -> x = y
    | VList xs, VList ys ->
        List.length xs = List.length ys && List.forall2 valueEquals xs ys
    | VInt _, v -> typeError "int" v
    | VBool _, v -> typeError "bool" v
    | VString _, v -> typeError "string" v
    | VList _, v -> typeError "list" v
    | v, _ -> typeError "int, bool, string or list" v

// ---- 내장 함수 ----

let private str value =
    match value with
    | VString s -> s
    | v -> typeError "string" v

let private int' value =
    match value with
    | VInt n -> n
    | v -> typeError "int" v

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
    ]

/// 인자가 다 모인 내장 함수를 실행. 문자열 위치와 길이는 UTF-16 코드 단위 (.NET string 과 같음)
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
    | "toString", [ VString s ] -> VString s
    | "toString", [ v ] -> VString (formatValue v)
    | "toInt", [ s ] ->
        let s = str s
        match System.Int32.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign,
                                    System.Globalization.CultureInfo.InvariantCulture) with
        | true, n -> VInt n
        | false, _ -> failwithf "toInt: invalid integer: %s" (quote s)
    | _ -> failwithf "Unknown builtin: %s" name

/// 처음 환경. 내장 함수는 보통 변수처럼 let 으로 가릴 수 있음
let builtins: Env =
    builtinArity |> Map.map (fun name _ -> VBuiltin (name, []))

let rec evalIn (env: Env) expr =
    match expr with
    | Number n -> VInt n
    | Bool b -> VBool b
    | Str s -> VString s
    | Var name ->
        match Map.tryFind name env with
        | Some v -> v
        | None -> failwithf "Unbound variable: %s" name
    | Let (name, value, body) ->
        let v = evalIn env value
        evalIn (Map.add name v env) body
    | LetRec (name, param, body, inExpr) ->
        evalIn (Map.add name (VRecClosure (name, param, body, env)) env) inExpr
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
    | Match (scrutinee, nilCase, head, tail, consCase) ->
        match evalList env scrutinee with
        | [] -> evalIn env nilCase
        | h :: t -> evalIn (env |> Map.add head h |> Map.add tail (VList t)) consCase

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
    | VInt x -> compare x (evalInt env b)
    | VString x -> System.String.CompareOrdinal(x, evalString env b)
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
    | VBuiltin (name, args) ->
        let args = args @ [ av ]
        if List.length args = builtinArity.[name] then callBuiltin name args
        else VBuiltin (name, args)
    | v -> typeError "function" v

/// 내장 함수만 있는 환경에서 평가
let eval expr = evalIn builtins expr
