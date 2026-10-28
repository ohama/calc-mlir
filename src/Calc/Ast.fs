module Ast

type Pattern =
    /// x: 무엇이든 일치하고 x 에 묶음
    | PVar of string
    /// _: 무엇이든 일치, 묶지 않음
    | PWildcard
    | PInt of int
    | PBool of bool
    | PString of string
    /// []
    | PNil
    /// head :: tail. 리스트 패턴 [a; b] 는 PCons(a, PCons(b, PNil)) 로 바뀜
    | PCons of Pattern * Pattern
    /// (p1, p2, ...) 원소 2개 이상
    | PTuple of Pattern list

type Expr =
    | Number of int
    | Bool of bool
    /// 문자열 리터럴 "..." (이스케이프는 렉서에서 처리된 값)
    | Str of string
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
    /// (e1, e2, ...) 원소 2개 이상
    | Tuple of Expr list
    /// match e with | pattern [when guard] -> body | ...  (위에서부터 처음 일치하는 분기)
    | Match of scrutinee: Expr * cases: (Pattern * Expr option * Expr) list
    /// let (a, b) = e in body. 일치하지 않으면 Match failure
    | LetPattern of Pattern * Expr * Expr
