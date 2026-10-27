module Eval

open Ast

type Value =
    | VInt of int
    | VBool of bool

type Env = Map<string, Value>

let formatValue value =
    match value with
    | VInt n -> string n
    | VBool true -> "true"
    | VBool false -> "false"

let typeName value =
    match value with
    | VInt _ -> "int"
    | VBool _ -> "bool"

let typeError expected value =
    failwithf "Type error: expected %s but got %s" expected (typeName value)

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

and evalInt env expr =
    match evalIn env expr with
    | VInt n -> n
    | v -> typeError "int" v

and evalBool env expr =
    match evalIn env expr with
    | VBool b -> b
    | v -> typeError "bool" v

/// = 와 <> 는 같은 타입끼리만 비교 가능
and evalEquals env a b =
    match evalIn env a, evalIn env b with
    | VInt x, VInt y -> x = y
    | VBool x, VBool y -> x = y
    | VInt _, v -> typeError "int" v
    | VBool _, v -> typeError "bool" v

/// 빈 환경에서 평가
let eval expr = evalIn Map.empty expr
