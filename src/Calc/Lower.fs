/// Ast.Expr → Core: 연산을 Prim 으로 모으고, &&/|| 를 if 로 바꾸고, 클로저 변환으로 함수를 최상위로 옮김 (docs/compiler-design.md D4)
module Lower

open Ast
open Core

/// 이름으로 알려진 함수: 인자 Arity 개를 모두 받는 최상위 함수 Direct. 그 이름의 값(바깥 클로저)을 클로저로 받음
type private Known = { Direct: string; Arity: int }

/// 지금 범위: 보이는 변수와, 그 가운데 알려진 함수. 이름을 다시 묶으면 알려진 함수에서 빠짐.
/// Fields 는 필드 이름 → 그 레코드 타입의 필드들 (선언한 순서). 같은 필드 이름의 타입을 다시 선언하면 뒤의 것
type private Scope =
    { Vars: Set<string>
      Known: Map<string, Known>
      Fields: Map<string, string list> }

let private emptyScope = { Vars = Set.empty; Known = Map.empty; Fields = Map.empty }
let private bind (name: string) (scope: Scope) = { scope with Vars = Set.add name scope.Vars; Known = Map.remove name scope.Known }
let private bindAll (names: seq<string>) (scope: Scope) = Seq.fold (fun acc name -> bind name acc) scope names
let private know (name: string) (known: Known) (scope: Scope) = { bind name scope with Known = Map.add name known scope.Known }

/// 프로그램 하나를 바꾸는 동안의 상태: 만든 함수와 다음 이름 번호 (테스트가 병렬이라 호출마다 새로)
type private State(ints: System.Collections.Generic.HashSet<obj>) =
    let functions = ResizeArray<CFunction>()
    let mutable next = 0

    /// 타입 추론이 피연산자를 int 로 정한 연산 노드인가 (Infer.intOperations)
    member _.IsInt(node: Expr) = ints.Contains(box node)

    member _.FreshName() =
        let name = sprintf "lambda_%d" next
        next <- next + 1
        name

    member _.AddFunctionNamed(name: string, param: string, self: string option, body: CExpr) =
        // 자유 변수 중 인자와 자기 자신을 뺀 나머지를 캡처. 정렬된 순서가 클로저의 칸 순서
        let bound = Set.ofList (param :: Option.toList self)
        let captured = Set.toList (Set.difference (freeVars body) bound)
        functions.Add { Name = name; Params = [ param ]; Self = self; Captured = captured; Body = body }
        CMakeClosure (name, captured)

    member this.AddFunction(param: string, self: string option, body: CExpr) =
        this.AddFunctionNamed(this.FreshName(), param, self, body)

    /// 이름에 묶을 함수의 바깥 클로저 이름과 Known. 본문의 (재귀) 호출이 쓰므로 본문을 낮추기 전에 정함.
    /// 인자가 하나면 바깥 클로저의 함수가 곧 direct 함수
    member this.Reserve(arity: int) =
        let outer = this.FreshName()
        outer, { Direct = (if arity = 1 then outer else outer + "_direct"); Arity = arity }

    /// 인자 parameters 를 하나씩 받는 클로저들(값으로 쓸 때)과, 인자를 모두 받는 direct 함수.
    /// 둘은 한 번 낮춘 본문을 함께 씀. direct 함수는 바깥 클로저를 받아 같은 칸에서 캡처한 값을 꺼냄
    /// (바깥 클로저의 캡처 = 본문의 자유 변수 - 인자 - 자기 자신 = direct 함수의 캡처)
    member this.AddCurried(outer: string, parameters: string list, self: string option, body: CExpr) =
        match parameters with
        | first :: rest ->
            let inner = List.foldBack (fun p acc -> this.AddFunction(p, None, acc)) rest body
            let closure = this.AddFunctionNamed(outer, first, self, inner)
            match closure with
            | CMakeClosure (_, captured) when not rest.IsEmpty ->
                functions.Add { Name = outer + "_direct"; Params = parameters; Self = self; Captured = captured; Body = body }
            | _ -> ()
            closure
        | [] -> failwith "Lower: function without parameters"

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

/// fun a -> fun b -> body 를 ([a; b], body) 로. 인자 표기(let a = (a : t) in ...)와 결과 표기는 값이 그대로라 건너뜀
let rec private lambdaChain (expr: Expr) =
    match expr with
    | Lambda (param, body) ->
        let parameters, inner = lambdaChain body
        param :: parameters, inner
    | Let (name, Annot (Var same, _), body) when name = same -> lambdaChain body
    | Annot (e, _) -> lambdaChain e
    | _ -> [], expr

/// 레코드 패턴을 선언한 순서의 모든 필드로 (쓰지 않은 필드는 _). Codegen 이 i 번째 필드를 칸 2 + i 에서 읽음
let rec private fillRecordFields (scope: Scope) (pattern: Pattern) : Pattern =
    let fill = fillRecordFields scope
    match pattern with
    | PRecord fields ->
        let names = scope.Fields[fst fields.Head]
        PRecord (names |> List.map (fun n -> n, fields |> List.tryFind (fun (f, _) -> f = n) |> Option.map (snd >> fill) |> Option.defaultValue PWildcard))
    | PCons (h, t) -> PCons (fill h, fill t)
    | PTuple items -> PTuple (List.map fill items)
    | PCtor (name, Some p) -> PCtor (name, Some (fill p))
    | POr (a, b) -> POr (fill a, fill b)
    | PVar _ | PWildcard | PInt _ | PBool _ | PString _ | PNil | PUnit | PCtor (_, None) -> pattern

/// ctors 는 지금 범위의 생성자 이름 → 인자가 있는지. type 선언이 범위를 넓힘 (안쪽 선언이 같은 이름을 가림).
/// scope 는 지금 범위의 변수. 범위에 없는 이름은 내장 함수 (let 으로 가린 내장 함수는 보통 변수)
let rec private lower (state: State) (ctors: Map<string, bool>) (scope: Scope) (expr: Expr) : CExpr =
    let isBuiltin name = not (Set.contains name scope.Vars) && Eval.builtinArity.ContainsKey name
    let prim p args = CPrim (p, List.map (lower state ctors scope) args)
    match expr with
    | Number n -> CInt n
    | Bool b -> CBool b
    | Str s -> CString s
    | Unit -> CUnit
    | Annot (e, _) -> lower state ctors scope e
    // e1; e2 == let $seq = e1 in e2 (이름은 렉서가 만들 수 없어서 겹치지 않음)
    | Seq (first, rest) -> CLet ("$seq", lower state ctors scope first, lower state ctors scope rest)
    | Var name when isBuiltin name -> builtinValue state name
    | Var name -> CVar name
    // 이름에 묶은 함수: 이 이름에 인자를 모두 주는 호출은 direct 함수로
    | Let (name, value, body) when not (fst (lambdaChain value)).IsEmpty ->
        let parameters, inner = lambdaChain value
        let outer, known = state.Reserve parameters.Length
        let closure = state.AddCurried(outer, parameters, None, lower state ctors (bindAll parameters scope) inner)
        CLet (name, closure, lower state ctors (know name known scope) body)
    | Let (name, value, body) -> CLet (name, lower state ctors scope value, lower state ctors (bind name scope) body)
    | If (c, t, e) -> CIf (lower state ctors scope c, lower state ctors scope t, lower state ctors scope e)
    | Add (a, b) -> prim (if state.IsInt expr then PIntAdd else PAdd) [ a; b ]
    | Subtract (a, b) -> prim PSubtract [ a; b ]
    | Multiply (a, b) -> prim PMultiply [ a; b ]
    | Divide (a, b) -> prim PDivide [ a; b ]
    | Modulo (a, b) -> prim PModulo [ a; b ]
    | Negate e -> prim PNegate [ e ]
    | Equal (a, b) -> prim (if state.IsInt expr then PIntEqual else PEqual) [ a; b ]
    | NotEqual (a, b) -> CPrim (PNot, [ prim (if state.IsInt expr then PIntEqual else PEqual) [ a; b ] ])
    | Less (a, b) -> prim (if state.IsInt expr then PIntLess else PLess) [ a; b ]
    | Greater (a, b) -> prim (if state.IsInt expr then PIntGreater else PGreater) [ a; b ]
    | LessEqual (a, b) -> prim (if state.IsInt expr then PIntLessEqual else PLessEqual) [ a; b ]
    | GreaterEqual (a, b) -> prim (if state.IsInt expr then PIntGreaterEqual else PGreaterEqual) [ a; b ]
    | Not e -> prim PNot [ e ]
    // 단락 평가: 오른쪽은 if 의 한 분기에서만 계산
    | And (a, b) -> CIf (lower state ctors scope a, lower state ctors scope b, CBool false)
    | Or (a, b) -> CIf (lower state ctors scope a, CBool true, lower state ctors scope b)
    | Lambda (param, body) -> state.AddFunction(param, None, lower state ctors (bind param scope) body)
    // let rec f x = body in e: 본문의 f 는 클로저 자신이라 캡처하지 않음. 본문의 재귀 호출도 direct 함수로
    | LetRec (name, param, body, inExpr) ->
        let rest, inner = lambdaChain body
        let parameters = param :: rest
        let outer, known = state.Reserve parameters.Length
        let scope = know name known scope
        CLet (name, state.AddCurried(outer, parameters, Some name, lower state ctors (bindAll parameters scope) inner), lower state ctors scope inExpr)
    // 함수마다 클로저. 자기 자신은 %closure, 다른 함수는 캡처한 칸 (Codegen 이 나중에 채움)
    | LetRecs (bindings, inExpr) ->
        let reserved =
            [ for name, param, body in bindings ->
                  let rest, inner = lambdaChain body
                  let outer, known = state.Reserve(1 + rest.Length)
                  name, outer, known, param :: rest, inner ]
        let groupScope = reserved |> List.fold (fun acc (name, _, known, _, _) -> know name known acc) scope
        let closures =
            [ for name, outer, _, parameters, inner in reserved ->
                  name, state.AddCurried(outer, parameters, Some name, lower state ctors (bindAll parameters groupScope) inner) ]
        CLetRecGroup (closures, lower state ctors groupScope inExpr)
    // 생성자를 바로 적용하면 그 자리에서 블록을 만듦 (인터프리터의 VConstructor 호출과 같은 결과)
    | App (Ctor name, a) when Map.tryFind name ctors = Some true -> CData (name, Some (lower state ctors scope a))
    // 내장 함수 sprintf 에 포맷 문자열 리터럴: 포맷의 함수에 나머지 인자를 적용
    | App _ when (match spine expr with
                  | Var name, Str _ :: _ -> isBuiltin name && Format.kinds.Contains name
                  | _ -> false) ->
        match spine expr with
        | Var name, Str format :: rest -> lower state ctors scope (List.fold (fun f a -> App (f, a)) (Printf (name, format)) rest)
        | _ -> failwith "unreachable"
    // 포맷의 함수 (Format.expand) 는 내장 함수(toString, print)만 쓰므로 사용자 이름이 없는 범위에서 낮춤
    | Printf (kind, format) ->
        match Format.parse format with
        | Ok pieces -> lower state ctors emptyScope (Format.expand kind pieces)
        | Error message -> failwithf "Lower: %s" message // 타입 검사를 통과했으므로 생기지 않음
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
    // 알려진 함수에 인자를 모두 주면 direct 함수를 바로 부름. 남는 인자는 결과에 차례로 적용
    | App _ when (match spine expr with
                  | Var name, args -> scope.Known |> Map.tryFind name |> Option.exists (fun k -> args.Length >= k.Arity)
                  | _ -> false) ->
        let name, args = match spine expr with
                         | Var name, args -> name, args
                         | _ -> failwith "unreachable"
        let known = scope.Known[name]
        let call = CCallDirect (known.Direct, CVar name, args |> List.take known.Arity |> List.map (lower state ctors scope))
        args |> List.skip known.Arity |> List.fold (fun f a -> CApply (f, lower state ctors scope a)) call
    | App (f, a) -> CApply (lower state ctors scope f, lower state ctors scope a)
    | Ctor name ->
        match Map.tryFind name ctors with
        | Some false -> CData (name, None)
        // 인자 있는 생성자를 값으로 쓰면 함수: fun x -> C x
        | Some true -> state.AddFunction("x", None, CData (name, Some (CVar "x")))
        // 타입 검사를 통과했으므로 생기지 않음
        | None -> failwithf "Lower: unknown constructor %s" name
    | ExceptionDecl (name, argType, body) -> lower state (Map.add name (Option.isSome argType) ctors) scope body
    // 본문은 fun () -> body, handler 는 fun $exn -> match $exn with <분기> | _ -> raise $exn (맞는 분기가 없으면 다시 raise)
    | Try (body, cases) ->
        let handlerScope = bind "$exn" scope
        let lowerCase (pattern, guard, handler) =
            let scope = bindAll (patternVars pattern) handlerScope
            fillRecordFields scope pattern, Option.map (lower state ctors scope) guard, lower state ctors scope handler
        let reraise = PWildcard, None, CBuiltin ("raise", [ CVar "$exn" ])
        let handler = CMatch (CVar "$exn", List.map lowerCase cases @ [ reraise ])
        CTry (state.AddFunction("$unit", None, lower state ctors scope body), state.AddFunction("$exn", None, handler))
    | TypeDecl (_, _, declared, body) ->
        let ctors = declared |> List.fold (fun acc (name, argType) -> Map.add name (Option.isSome argType) acc) ctors
        lower state ctors scope body
    | Nil -> CNil
    | RecordDecl (_, _, fields, body) ->
        let names = List.map fst fields
        lower state ctors { scope with Fields = names |> List.fold (fun acc f -> Map.add f names acc) scope.Fields } body
    // 값을 쓴 순서대로 $field_<이름> 에 묶고, 블록에는 선언한 순서로 넣음 (인터프리터와 같은 계산 순서)
    | Record fields ->
        let names = scope.Fields[fst fields.Head]
        let block = CRecord (names, names |> List.map (fun n -> CVar ("$field_" + n)))
        List.foldBack (fun (f, e) acc -> CLet ("$field_" + f, lower state ctors scope e, acc)) fields block
    | Field (e, field) ->
        let names = scope.Fields[field]
        CField (lower state ctors scope e, List.findIndex ((=) field) names)
    // { e with f = x }: e 를 $record 에 묶고, 바꾸지 않는 필드는 $record 에서 꺼냄
    | RecordUpdate (e, fields) ->
        let names = scope.Fields[fst fields.Head]
        let value n =
            if List.exists (fun (f, _) -> f = n) fields then CVar ("$field_" + n)
            else CField (CVar "$record", List.findIndex ((=) n) names)
        let block = CRecord (names, List.map value names)
        CLet ("$record", lower state ctors scope e,
              List.foldBack (fun (f, x) acc -> CLet ("$field_" + f, lower state ctors scope x, acc)) fields block)
    | Cons (h, t) -> CCons (lower state ctors scope h, lower state ctors scope t)
    | Tuple items -> CTuple (List.map (lower state ctors scope) items)
    | Match (scrutinee, cases) ->
        let lowerCase (pattern, guard, body) =
            let inner = bindAll (patternVars pattern) scope
            fillRecordFields scope pattern, Option.map (lower state ctors inner) guard, lower state ctors inner body
        CMatch (lower state ctors scope scrutinee, List.map lowerCase cases)
    // let (a, b) = e in body 는 분기 하나짜리 match (일치하지 않으면 같은 Match failure)
    | LetPattern (pattern, value, body) ->
        CMatch (lower state ctors scope value, [ fillRecordFields scope pattern, None, lower state ctors (bindAll (patternVars pattern) scope) body ])

let lowerProgram (expr: Expr) : CProgram =
    let state = State(Infer.intOperations expr)
    // 'a option = None | Some of 'a 와 예외 Failure, Not_found 는 선언 없이 쓸 수 있음 (Eval 의 초기 환경과 같음)
    let main = lower state (Map.ofList [ "None", false; "Some", true; "Failure", true; "Not_found", false ]) emptyScope expr
    { Functions = state.Functions; Main = main }
