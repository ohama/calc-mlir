module Ast

type Expr =
    | Number of int
    | Bool of bool
    | Var of string
    | Let of string * Expr * Expr
    | LetRec of name: string * param: string * body: Expr * inExpr: Expr
    | Lambda of string * Expr
    | App of Expr * Expr
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
    /// 빈 리스트 []
    | Nil
    /// head :: tail. 리스트 리터럴 [a; b] 는 Cons(a, Cons(b, Nil)) 로 바뀜
    | Cons of Expr * Expr
    /// match e with [] -> nilCase | head :: tail -> consCase
    | Match of scrutinee: Expr * nilCase: Expr * head: string * tail: string * consCase: Expr
