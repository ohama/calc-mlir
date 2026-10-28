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

/// 내장 함수를 값으로 쓸 때: 인자를 하나씩 받는 클로저 (fun $b0 -> fun $b1 -> ... -> 호출). 인터프리터의 VBuiltin 과 같음
let private builtinValue (state: State) (name: string) =
    let arity = Eval.builtinArity[name]
    let parameters = [ for i in 0 .. arity - 1 -> sprintf "$b%d" i ]
    let call = CBuiltin (name, parameters |> List.map CVar)
    // 안쪽 함수부터 만듦. 바깥 인자는 클로저 변환이 캡처함
    List.foldBack (fun param body -> state.AddFunction(param, None, body)) parameters call

/// f a1 a2 ... 를 (f, [a1; a2; ...]) 로
let rec private spine (expr: Expr) =
    match expr with
    | App (f, a) ->
        let head, args = spine f
        head, args @ [ a ]
    | _ -> expr, []

/// ctors 는 지금 범위의 생성자 이름 → 인자가 있는지. type 선언이 범위를 넓힘 (안쪽 선언이 같은 이름을 가림).
/// scope 는 지금 범위의 변수. 범위에 없는 이름은 내장 함수 (let 으로 가린 내장 함수는 보통 변수)
let rec private lower (state: State) (ctors: Map<string, bool>) (scope: Set<string>) (expr: Expr) : CExpr =
    let isBuiltin name = not (Set.contains name scope) && Eval.builtinArity.ContainsKey name
    let prim p args = CPrim (p, List.map (lower state ctors scope) args)
    match expr with
    | Number n -> CInt n
    | Bool b -> CBool b
    | Str s -> CString s
    | Unit -> CUnit
    // e1; e2 == let $seq = e1 in e2 (이름은 렉서가 만들 수 없어서 겹치지 않음)
    | Seq (first, rest) -> CLet ("$seq", lower state ctors scope first, lower state ctors scope rest)
    | Var name when isBuiltin name -> builtinValue state name
    | Var name -> CVar name
    | Let (name, value, body) -> CLet (name, lower state ctors scope value, lower state ctors (Set.add name scope) body)
    | If (c, t, e) -> CIf (lower state ctors scope c, lower state ctors scope t, lower state ctors scope e)
    | Add (a, b) -> prim PAdd [ a; b ]
    | Subtract (a, b) -> prim PSubtract [ a; b ]
    | Multiply (a, b) -> prim PMultiply [ a; b ]
    | Divide (a, b) -> prim PDivide [ a; b ]
    | Modulo (a, b) -> prim PModulo [ a; b ]
    | Negate e -> prim PNegate [ e ]
    | Equal (a, b) -> prim PEqual [ a; b ]
    | NotEqual (a, b) -> CPrim (PNot, [ prim PEqual [ a; b ] ])
    | Less (a, b) -> prim PLess [ a; b ]
    | Greater (a, b) -> prim PGreater [ a; b ]
    | LessEqual (a, b) -> prim PLessEqual [ a; b ]
    | GreaterEqual (a, b) -> prim PGreaterEqual [ a; b ]
    | Not e -> prim PNot [ e ]
    // 단락 평가: 오른쪽은 if 의 한 분기에서만 계산
    | And (a, b) -> CIf (lower state ctors scope a, lower state ctors scope b, CBool false)
    | Or (a, b) -> CIf (lower state ctors scope a, CBool true, lower state ctors scope b)
    | Lambda (param, body) -> state.AddFunction(param, None, lower state ctors (Set.add param scope) body)
    // let rec f x = body in e: 본문의 f 는 클로저 자신이라 캡처하지 않음
    | LetRec (name, param, body, inExpr) ->
        let closure = state.AddFunction(param, Some name, lower state ctors (scope |> Set.add name |> Set.add param) body)
        CLet (name, closure, lower state ctors (Set.add name scope) inExpr)
    // 함수마다 클로저. 자기 자신은 %closure, 다른 함수는 캡처한 칸 (Codegen 이 나중에 채움)
    | LetRecs (bindings, inExpr) ->
        let groupScope = bindings |> List.fold (fun acc (name, _, _) -> Set.add name acc) scope
        let closures =
            [ for name, param, body in bindings ->
                  name, state.AddFunction(param, Some name, lower state ctors (Set.add param groupScope) body) ]
        CLetRecGroup (closures, lower state ctors groupScope inExpr)
    // 생성자를 바로 적용하면 그 자리에서 블록을 만듦 (인터프리터의 VConstructor 호출과 같은 결과)
    | App (Ctor name, a) when Map.tryFind name ctors = Some true -> CData (name, Some (lower state ctors scope a))
    // 인자가 다 모인 내장 함수는 런타임 함수를 바로 호출. 남는 인자는 결과에 차례로 적용
    | App _ when (match spine expr with
                  | Var name, args -> isBuiltin name && args.Length >= Eval.builtinArity[name]
                  | _ -> false) ->
        let name, args = match spine expr with
                         | Var name, args -> name, args
                         | _ -> failwith "unreachable"
        let arity = Eval.builtinArity[name]
        let call = CBuiltin (name, args |> List.take arity |> List.map (lower state ctors scope))
        args |> List.skip arity |> List.fold (fun f a -> CApply (f, lower state ctors scope a)) call
    | App (f, a) -> CApply (lower state ctors scope f, lower state ctors scope a)
    | Ctor name ->
        match Map.tryFind name ctors with
        | Some false -> CData (name, None)
        // 인자 있는 생성자를 값으로 쓰면 함수: fun x -> C x
        | Some true -> state.AddFunction("x", None, CData (name, Some (CVar "x")))
        // 타입 검사를 통과했으므로 생기지 않음
        | None -> failwithf "Lower: unknown constructor %s" name
    | TypeDecl (_, _, declared, body) ->
        let ctors = declared |> List.fold (fun acc (name, argType) -> Map.add name (Option.isSome argType) acc) ctors
        lower state ctors scope body
    | Nil -> CNil
    | Cons (h, t) -> CCons (lower state ctors scope h, lower state ctors scope t)
    | Tuple items -> CTuple (List.map (lower state ctors scope) items)
    | Match (scrutinee, cases) ->
        let lowerCase (pattern, guard, body) =
            let scope = Set.union scope (patternVars pattern)
            pattern, Option.map (lower state ctors scope) guard, lower state ctors scope body
        CMatch (lower state ctors scope scrutinee, List.map lowerCase cases)
    // let (a, b) = e in body 는 분기 하나짜리 match (일치하지 않으면 같은 Match failure)
    | LetPattern (pattern, value, body) ->
        CMatch (lower state ctors scope value, [ pattern, None, lower state ctors (Set.union scope (patternVars pattern)) body ])

let lowerProgram (expr: Expr) : CProgram =
    let state = State()
    // 'a option = None | Some of 'a 는 선언 없이 쓸 수 있음 (Eval 의 초기 환경과 같음)
    let main = lower state (Map.ofList [ "None", false; "Some", true ]) Set.empty expr
    { Functions = state.Functions; Main = main }
