module Ast

type Expr =
    | Number of int
    | Var of string
    | Let of string * Expr * Expr
    | Add of Expr * Expr
    | Subtract of Expr * Expr
    | Multiply of Expr * Expr
    | Divide of Expr * Expr
    | Negate of Expr
