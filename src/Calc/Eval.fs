module Eval

open Ast

type Value =
    | VInt of int
    | VBool of bool
    /// fun param -> body, 정의된 시점의 환경을 함께 저장 (정적 범위)
    | VClosure of param: string * body: Expr * env: Map<string, Value>
    /// let rec name param = body. 호출할 때 name 을 자기 자신으로 환경에 추가
    | VRecClosure of name: string * param: string * body: Expr * env: Map<string, Value>
    | VList of Value list

type Env = Map<string, Value>

let rec formatValue value =
    match value with
    | VInt n -> string n
    | VBool true -> "true"
    | VBool false -> "false"
    | VClosure _ | VRecClosure _ -> "<fun>"
    | VList items -> "[" + (items |> List.map formatValue |> String.concat "; ") + "]"

let typeName value =
    match value with
    | VInt _ -> "int"
    | VBool _ -> "bool"
    | VClosure _ | VRecClosure _ -> "function"
    | VList _ -> "list"

let typeError expected value =
    failwithf "Type error: expected %s but got %s" expected (typeName value)

/// = 와 <> 는 같은 타입끼리만 비교 가능. 리스트는 원소를 차례로 비교
let rec valueEquals a b =
    match a, b with
    | VInt x, VInt y -> x = y
    | VBool x, VBool y -> x = y
    | VList xs, VList ys ->
        List.length xs = List.length ys && List.forall2 valueEquals xs ys
    | VInt _, v -> typeError "int" v
    | VBool _, v -> typeError "bool" v
    | VList _, v -> typeError "list" v
    | v, _ -> typeError "int, bool or list" v

let rec evalIn (env: Env) expr =
    match expr with
    | Number n -> VInt n
    | Bool b -> VBool b
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
    | Add (a, b) -> VInt (evalInt env a + evalInt env b)
    | Subtract (a, b) -> VInt (evalInt env a - evalInt env b)
    | Multiply (a, b) -> VInt (evalInt env a * evalInt env b)
    | Divide (a, b) -> VInt (evalInt env a / evalInt env b)
    | Negate e -> VInt -(evalInt env e)
    | Equal (a, b) -> VBool (evalEquals env a b)
    | NotEqual (a, b) -> VBool (not (evalEquals env a b))
    | Less (a, b) -> VBool (evalInt env a < evalInt env b)
    | Greater (a, b) -> VBool (evalInt env a > evalInt env b)
    | LessEqual (a, b) -> VBool (evalInt env a <= evalInt env b)
    | GreaterEqual (a, b) -> VBool (evalInt env a >= evalInt env b)
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
