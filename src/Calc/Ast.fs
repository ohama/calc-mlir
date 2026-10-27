module Ast

type Expr =
    | Number of int
    | Bool of bool
    | Var of string
    | Let of string * Expr * Expr
    | If of Expr * Expr * Expr
    | Add of Expr * Expr
    | Subtract of Expr * Expr
    | Multiply of Expr * Expr
    | Divide of Expr * Expr
    | Negate of Expr
    | Equal of Expr * Expr
    | NotEqual of Expr * Expr
    | Less of Expr * Expr
    | Greater of Expr * Expr
    | LessEqual of Expr * Expr
    | GreaterEqual of Expr * Expr
    | And of Expr * Expr
    | Or of Expr * Expr
    | Not of Expr
