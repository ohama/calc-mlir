/// Ast.Expr → Core: 연산을 Prim 으로 모으고, &&/|| 를 if 로 바꾸고, 클로저 변환으로 함수를 최상위로 옮김 (docs/compiler-design.md D4)
module Lower

open Microsoft.FSharp.Reflection
open Ast
open Core

/// 식의 종류 이름 (NotSupported 메시지용): Str, Cons, Match, ...
let private caseName (expr: Expr) =
    let case, _ = FSharpValue.GetUnionFields(expr, typeof<Expr>)
    case.Name

/// 프로그램 하나를 바꾸는 동안의 상태: 만든 함수와 다음 이름 번호 (테스트가 병렬이라 호출마다 새로)
type private State() =
    let functions = ResizeArray<CFunction>()

    member _.AddFunction(param: string, self: string option, body: CExpr) =
        let name = sprintf "lambda_%d" functions.Count
        // 자유 변수 중 인자와 자기 자신을 뺀 나머지를 캡처. 정렬된 순서가 클로저의 칸 순서
        let bound = Set.ofList (param :: Option.toList self)
        let captured = Set.toList (Set.difference (freeVars body) bound)
        functions.Add { Name = name; Param = param; Self = self; Captured = captured; Body = body }
        CMakeClosure (name, captured)

    member _.Functions = List.ofSeq functions

let rec private lower (state: State) (expr: Expr) : CExpr =
    let prim p args = CPrim (p, List.map (lower state) args)
    match expr with
    | Number n -> CInt n
    | Bool b -> CBool b
    | Str s -> CString s
    | Var name -> CVar name
    | Let (name, value, body) -> CLet (name, lower state value, lower state body)
    | If (c, t, e) -> CIf (lower state c, lower state t, lower state e)
    | Add (a, b) -> prim PAdd [ a; b ]
    | Subtract (a, b) -> prim PSubtract [ a; b ]
    | Multiply (a, b) -> prim PMultiply [ a; b ]
    | Divide (a, b) -> prim PDivide [ a; b ]
    | Negate e -> prim PNegate [ e ]
    | Equal (a, b) -> prim PEqual [ a; b ]
    | NotEqual (a, b) -> CPrim (PNot, [ prim PEqual [ a; b ] ])
    | Less (a, b) -> prim PLess [ a; b ]
    | Greater (a, b) -> prim PGreater [ a; b ]
    | LessEqual (a, b) -> prim PLessEqual [ a; b ]
    | GreaterEqual (a, b) -> prim PGreaterEqual [ a; b ]
    | Not e -> prim PNot [ e ]
    // 단락 평가: 오른쪽은 if 의 한 분기에서만 계산
    | And (a, b) -> CIf (lower state a, lower state b, CBool false)
    | Or (a, b) -> CIf (lower state a, CBool true, lower state b)
    | Lambda (param, body) -> state.AddFunction(param, None, lower state body)
    // let rec f x = body in e: 본문의 f 는 클로저 자신이라 캡처하지 않음
    | LetRec (name, param, body, inExpr) ->
        let closure = state.AddFunction(param, Some name, lower state body)
        CLet (name, closure, lower state inExpr)
    | App (f, a) -> CApply (lower state f, lower state a)
    | other -> raise (NotSupported (caseName other))

let lowerProgram (expr: Expr) : CProgram =
    let state = State()
    let main = lower state expr
    { Functions = state.Functions; Main = main }
