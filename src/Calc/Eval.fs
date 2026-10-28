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
    | VClosure _ | VRecClosure _ -> "<fun>"
    | VList items -> "[" + (items |> List.map formatValue |> String.concat "; ") + "]"

let typeName value =
    match value with
    | VInt _ -> "int"
    | VBool _ -> "bool"
    | VString _ -> "string"
    | VClosure _ | VRecClosure _ -> "function"
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
    | v -> typeError "function" v

/// 빈 환경에서 평가
let eval expr = evalIn Map.empty expr
