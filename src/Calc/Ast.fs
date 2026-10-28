module Ast

/// type 선언과 타입 표기 (e : t) 에 적는 타입. 실행 중 타입 검사에는 쓰지 않고, 생성자 인자 수(튜플 원소 수)만 사용
type TypeExpr =
    /// int, string, 'a list, (int, string) pair (인자는 앞에 씀)
    | TName of string * TypeExpr list
    /// 'a
    | TVar of string
    /// t1 * t2 * ...
    | TTuple of TypeExpr list
    /// t1 -> t2
    | TFun of TypeExpr * TypeExpr

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
    /// None, Some p, Node (l, x, r)
    | PCtor of string * Pattern option
    /// ()
    | PUnit
    /// p1 | p2: 왼쪽부터 처음 일치하는 쪽. 두 쪽이 묶는 이름은 같아야 함
    | POr of Pattern * Pattern

type Expr =
    | Number of int
    | Bool of bool
    /// 문자열 리터럴 "..." (이스케이프는 렉서에서 처리된 값)
    | Str of string
    /// () : unit 의 유일한 값
    | Unit
    /// e1; e2: e1 을 평가하고 버린 뒤 e2 (OCaml, F# 와 같음)
    | Seq of Expr * Expr
    | Var of string
    | Let of string * Expr * Expr
    | LetRec of name: string * param: string * body: Expr * inExpr: Expr
    /// let rec f x = ... and g y = ... in e (함수 둘 이상, mutual recursion). 하나면 LetRec
    | LetRecs of bindings: (string * string * Expr) list * inExpr: Expr
    | Lambda of string * Expr
    | App of Expr * Expr
    | If of Expr * Expr * Expr
    | Add of Expr * Expr
    | Subtract of Expr * Expr
    | Multiply of Expr * Expr
    | Divide of Expr * Expr
    /// a % b: 나머지 (부호는 a 를 따름, .NET 과 같음)
    | Modulo of Expr * Expr
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
    /// 생성자 이름 (대문자로 시작). 인자가 있는 생성자는 App (Ctor "Some", e)
    | Ctor of string
    /// type 'a name = C1 | C2 of t | ... in body
    | TypeDecl of name: string * typeParams: string list * ctors: (string * TypeExpr option) list * body: Expr
    /// (e : t): e 의 타입이 t 여야 함. 실행할 때는 e 와 같음.
    /// 매개변수 표기 fun (x: t) -> body 는 fun x -> let x = (x : t) in body, 반환 타입 표기 let f x : t = e 는 let f x = (e : t)
    | Annot of Expr * TypeExpr

/// 프로그램의 최상위 항목: in 없는 선언 또는 식. 위치는 선언의 시작 (풀어 쓴 let 노드에 기록)
type TopItem =
    | TopLet of string * Expr * FSharp.Text.Lexing.Position
    | TopLetRec of string * string * Expr * FSharp.Text.Lexing.Position
    | TopLetRecs of (string * string * Expr) list * FSharp.Text.Lexing.Position
    | TopLetPattern of Pattern * Expr * FSharp.Text.Lexing.Position
    | TopType of string * string list * (string * TypeExpr option) list * FSharp.Text.Lexing.Position
    | TopExpr of Expr
