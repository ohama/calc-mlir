/// Ast.Expr → Core: 연산을 Prim 으로 모으고, &&/|| 를 if 로 바꾸고, 클로저 변환으로 함수를 최상위로 옮김 (docs/compiler-design.md D4)
module Lower

open Ast
open Core

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

/// ctors 는 지금 범위의 생성자 이름 → 인자가 있는지. type 선언이 범위를 넓힘 (안쪽 선언이 같은 이름을 가림)
let rec private lower (state: State) (ctors: Map<string, bool>) (expr: Expr) : CExpr =
    let prim p args = CPrim (p, List.map (lower state ctors) args)
    match expr with
    | Number n -> CInt n
    | Bool b -> CBool b
    | Str s -> CString s
    | Var name -> CVar name
    | Let (name, value, body) -> CLet (name, lower state ctors value, lower state ctors body)
    | If (c, t, e) -> CIf (lower state ctors c, lower state ctors t, lower state ctors e)
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
    | And (a, b) -> CIf (lower state ctors a, lower state ctors b, CBool false)
    | Or (a, b) -> CIf (lower state ctors a, CBool true, lower state ctors b)
    | Lambda (param, body) -> state.AddFunction(param, None, lower state ctors body)
    // let rec f x = body in e: 본문의 f 는 클로저 자신이라 캡처하지 않음
    | LetRec (name, param, body, inExpr) ->
        let closure = state.AddFunction(param, Some name, lower state ctors body)
        CLet (name, closure, lower state ctors inExpr)
    // 생성자를 바로 적용하면 그 자리에서 블록을 만듦 (인터프리터의 VConstructor 호출과 같은 결과)
    | App (Ctor name, a) when Map.tryFind name ctors = Some true -> CData (name, Some (lower state ctors a))
    | App (f, a) -> CApply (lower state ctors f, lower state ctors a)
    | Ctor name ->
        match Map.tryFind name ctors with
        | Some false -> CData (name, None)
        // 인자 있는 생성자를 값으로 쓰면 함수: fun x -> C x
        | Some true -> state.AddFunction("x", None, CData (name, Some (CVar "x")))
        // 타입 검사를 통과했으므로 생기지 않음
        | None -> failwithf "Lower: unknown constructor %s" name
    | TypeDecl (_, _, declared, body) ->
        let ctors = declared |> List.fold (fun acc (name, argType) -> Map.add name (Option.isSome argType) acc) ctors
        lower state ctors body
    | Nil -> CNil
    | Cons (h, t) -> CCons (lower state ctors h, lower state ctors t)
    | Tuple items -> CTuple (List.map (lower state ctors) items)
    | Match (scrutinee, cases) ->
        let lowerCase (pattern, guard, body) =
            pattern, Option.map (lower state ctors) guard, lower state ctors body
        CMatch (lower state ctors scrutinee, List.map lowerCase cases)
    // let (a, b) = e in body 는 분기 하나짜리 match (일치하지 않으면 같은 Match failure)
    | LetPattern (pattern, value, body) ->
        CMatch (lower state ctors value, [ pattern, None, lower state ctors body ])

let lowerProgram (expr: Expr) : CProgram =
    let state = State()
    // 'a option = None | Some of 'a 는 선언 없이 쓸 수 있음 (Eval 의 초기 환경과 같음)
    let main = lower state (Map.ofList [ "None", false; "Some", true ]) expr
    { Functions = state.Functions; Main = main }
