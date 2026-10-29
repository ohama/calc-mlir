/// 모듈: module M = struct ... end 와 open M 을 파싱 직후 이름 바꾸기로 없앰.
/// 모듈 안의 선언은 M.f, M.t, M.C 같은 전체 이름의 최상위 선언이 되고, 모듈 안의 짧은 이름과 open 한 이름은 전체 이름으로 바뀜.
/// 그래서 타입 추론, 평가기, 컴파일러는 모듈을 모름. 점이 들어간 이름은 렉서가 QIDENT, QCTOR 로만 만들므로 사용자 이름과 겹치지 않음.
/// 레코드 필드 이름은 바꾸지 않음 (모듈 밖에서도 p.x 로 씀)
module Modules

open Ast

/// 짧은 이름 → 전체 이름. 바뀌지 않는 이름(최상위에서 선언한 것)은 넣지 않음
type Env =
    { Values: Map<string, string>
      Ctors: Map<string, string>
      Types: Map<string, string>
      /// 모듈 이름 → 전체 경로 (모듈 M 안의 모듈 N 은 "M.N")
      Modules: Map<string, string> }

/// 지금까지의 env 와, 선언한 모듈마다 그 모듈의 이름들 (전체 경로 → 짧은 이름 → 전체 이름). open 이 env 에 더함
type Scope = { Env: Env; Declared: Map<string, Env> }

let private emptyEnv = { Values = Map.empty; Ctors = Map.empty; Types = Map.empty; Modules = Map.empty }

let empty = { Env = emptyEnv; Declared = Map.empty }

let private isEmpty (env: Env) =
    env.Values.IsEmpty && env.Ctors.IsEmpty && env.Types.IsEmpty && env.Modules.IsEmpty

/// 바꾼 노드에 원래 노드의 위치를 옮김 (타입 오류와 경고의 위치)
let private keep (old: 'a) (node: 'a) : 'a = Errors.copyPosition (box old) (box node) :?> 'a

/// 이름 하나: M.f 는 첫 모듈 이름만 전체 경로로 (모듈 안에서 그 안의 모듈을 N.g 로 쓸 때 M.N.g), 짧은 이름은 names 에서
let private resolveIn (names: Map<string, string>) (env: Env) (name: string) =
    match name.IndexOf '.' with
    | -1 -> Map.tryFind name names |> Option.defaultValue name
    | i ->
        match Map.tryFind (name.Substring(0, i)) env.Modules with
        | Some path -> path + name.Substring i
        | None -> name

/// name 을 선언하면 env 에서는 name → full (full = name 이면 바깥의 같은 이름을 가리므로 뺌)
let private declare (names: Map<string, string>) (name: string) (full: string) =
    if name = full then Map.remove name names else Map.add name full names

let private unbind (names: string list) (env: Env) =
    { env with Values = names |> List.fold (fun acc n -> Map.remove n acc) env.Values }

let rec private patternNames pattern =
    match pattern with
    | PVar name -> [ name ]
    | PCons (h, t) -> patternNames h @ patternNames t
    | PTuple items -> List.collect patternNames items
    | PCtor (_, Some p) -> patternNames p
    | POr (a, _) -> patternNames a
    | PWildcard | PInt _ | PBool _ | PString _ | PNil | PUnit | PCtor (_, None) -> []

let rec private renameType (env: Env) (te: TypeExpr) =
    match te with
    | TName (name, args) -> TName (resolveIn env.Types env name, List.map (renameType env) args)
    | TVar _ -> te
    | TTuple items -> TTuple (List.map (renameType env) items)
    | TFun (a, b) -> TFun (renameType env a, renameType env b)

/// 패턴의 생성자를 바꾸고, 묶는 이름 x 는 var x 로 (최상위 let 패턴이 모듈 안에서 M.x 를 묶을 때)
let rec private renamePatternWith (var: string -> string) (env: Env) (pattern: Pattern) =
    let r = renamePatternWith var env
    keep pattern
        (match pattern with
         | PVar name -> PVar (var name)
         | PCtor (name, arg) -> PCtor (resolveIn env.Ctors env name, Option.map r arg)
         | PCons (h, t) -> PCons (r h, r t)
         | PTuple items -> PTuple (List.map r items)
         | POr (a, b) -> POr (r a, r b)
         | PWildcard | PInt _ | PBool _ | PString _ | PNil | PUnit -> pattern)

let private renamePattern env pattern = renamePatternWith id env pattern

/// 식 안의 이름을 env 로 바꿈. 식 안에서 묶은 이름(let, fun, 패턴, 식 안의 type 선언)은 바깥 이름을 가림
let rec private renameExpr (env: Env) (expr: Expr) : Expr =
    if isEmpty env then expr
    else
    let r = renameExpr env
    let arms (cases: (Pattern * Expr option * Expr) list) =
        cases
        |> List.map (fun ((pattern, guard, body) as arm) ->
            let inner = unbind (patternNames pattern) env
            keep arm (renamePattern env pattern, Option.map (renameExpr inner) guard, renameExpr inner body))
    keep expr
        (match expr with
         | Number _ | Bool _ | Str _ | Unit | Nil | Printf _ -> expr
         | Var name -> Var (resolveIn env.Values env name)
         | Ctor name -> Ctor (resolveIn env.Ctors env name)
         | Seq (a, b) -> Seq (r a, r b)
         | Let (name, value, body) -> Let (name, r value, renameExpr (unbind [ name ] env) body)
         | LetRec (name, param, body, inExpr) ->
             let inner = unbind [ name ] env
             LetRec (name, param, renameExpr (unbind [ param ] inner) body, renameExpr inner inExpr)
         | LetRecs (bindings, inExpr) ->
             let inner = unbind (bindings |> List.map (fun (name, _, _) -> name)) env
             LetRecs (bindings |> List.map (fun (name, param, body) -> name, param, renameExpr (unbind [ param ] inner) body),
                      renameExpr inner inExpr)
         | Lambda (param, body) -> Lambda (param, renameExpr (unbind [ param ] env) body)
         | App (f, a) -> App (r f, r a)
         | If (c, t, e) -> If (r c, r t, r e)
         | Add (a, b) -> Add (r a, r b)
         | Subtract (a, b) -> Subtract (r a, r b)
         | Multiply (a, b) -> Multiply (r a, r b)
         | Divide (a, b) -> Divide (r a, r b)
         | Modulo (a, b) -> Modulo (r a, r b)
         | Negate e -> Negate (r e)
         | Equal (a, b) -> Equal (r a, r b)
         | NotEqual (a, b) -> NotEqual (r a, r b)
         | Less (a, b) -> Less (r a, r b)
         | Greater (a, b) -> Greater (r a, r b)
         | LessEqual (a, b) -> LessEqual (r a, r b)
         | GreaterEqual (a, b) -> GreaterEqual (r a, r b)
         | And (a, b) -> And (r a, r b)
         | Or (a, b) -> Or (r a, r b)
         | Not e -> Not (r e)
         | Cons (h, t) -> Cons (r h, r t)
         | Tuple items -> Tuple (List.map r items)
         | Match (scrutinee, cases) -> Match (r scrutinee, arms cases)
         | Try (body, cases) -> Try (r body, arms cases)
         | LetPattern (pattern, value, body) ->
             LetPattern (renamePattern env pattern, r value, renameExpr (unbind (patternNames pattern) env) body)
         | TypeDecl (name, typeParams, ctors, body) ->
             let inner =
                 { env with
                     Types = Map.remove name env.Types
                     Ctors = ctors |> List.fold (fun acc (ctor, _) -> Map.remove ctor acc) env.Ctors }
             TypeDecl (name, typeParams, ctors |> List.map (fun (ctor, t) -> ctor, Option.map (renameType inner) t), renameExpr inner body)
         | RecordDecl (name, typeParams, fields, body) ->
             let inner = { env with Types = Map.remove name env.Types }
             RecordDecl (name, typeParams, fields |> List.map (fun (f, t) -> f, renameType inner t), renameExpr inner body)
         | ExceptionDecl (name, argType, body) ->
             ExceptionDecl (name, Option.map (renameType env) argType, renameExpr { env with Ctors = Map.remove name env.Ctors } body)
         | Annot (e, t) -> Annot (r e, renameType env t)
         | Record fields -> Record (fields |> List.map (fun (f, e) -> f, r e))
         | Field (e, field) -> Field (r e, field)
         | RecordUpdate (e, fields) -> RecordUpdate (r e, fields |> List.map (fun (f, x) -> f, r x)))

/// 항목들을 차례로 바꿈. prefix 는 지금 모듈의 경로와 점("M." , 최상위면 "").
/// 새 scope, 이 항목들이 선언한 이름(모듈의 이름들), 펼친 항목을 돌려줌
let rec private resolveItems (prefix: string) (scope: Scope) (items: TopItem list) : Scope * Env * TopItem list =
    let mutable env = scope.Env
    let mutable declared = scope.Declared
    let mutable members = emptyEnv
    let output = ResizeArray<TopItem>()
    let addValue name =
        let full = prefix + name
        env <- { env with Values = declare env.Values name full }
        members <- { members with Values = Map.add name full members.Values }
        full
    let addType name =
        let full = prefix + name
        env <- { env with Types = declare env.Types name full }
        members <- { members with Types = Map.add name full members.Types }
        full
    let addCtor name =
        let full = prefix + name
        env <- { env with Ctors = declare env.Ctors name full }
        members <- { members with Ctors = Map.add name full members.Ctors }
        full
    for item in items do
        match item with
        | TopExpr e -> output.Add(TopExpr (renameExpr env e))
        | TopLet (name, value, pos) ->
            let value = renameExpr env value
            output.Add(TopLet (addValue name, value, pos))
        | TopLetRec (name, param, body, pos) ->
            let full = addValue name
            output.Add(TopLetRec (full, param, renameExpr (unbind [ param ] env) body, pos))
        | TopLetRecs (bindings, pos) ->
            let fulls = bindings |> List.map (fun (name, _, _) -> addValue name)
            let renamed = List.map2 (fun full (_, param, body) -> full, param, renameExpr (unbind [ param ] env) body) fulls bindings
            output.Add(TopLetRecs (renamed, pos))
        | TopLetPattern (pattern, value, pos) ->
            let value = renameExpr env value
            let names = patternNames pattern
            output.Add(TopLetPattern (renamePatternWith (fun name -> prefix + name) env pattern, value, pos))
            for name in names do addValue name |> ignore
        | TopType (name, typeParams, ctors, pos) ->
            let full = addType name // 생성자 인자에서 자기 자신을 쓸 수 있음
            let ctors = ctors |> List.map (fun (ctor, t) -> ctor, Option.map (renameType env) t)
            output.Add(TopType (full, typeParams, ctors |> List.map (fun (ctor, t) -> addCtor ctor, t), pos))
        | TopRecord (name, typeParams, fields, pos) ->
            let full = addType name
            output.Add(TopRecord (full, typeParams, fields |> List.map (fun (f, t) -> f, renameType env t), pos))
        | TopException (name, argType, pos) ->
            let argType = Option.map (renameType env) argType
            output.Add(TopException (addCtor name, argType, pos))
        | TopModule (name, inner, _) ->
            let path = prefix + name
            // 안쪽은 바깥 이름을 보고, 끝나면 안쪽 이름은 path. 로만 보임
            let innerScope, innerMembers, innerItems = resolveItems (path + ".") { Env = env; Declared = declared } inner
            output.AddRange innerItems
            declared <- Map.add path innerMembers innerScope.Declared
            env <- { env with Modules = declare env.Modules name path }
            members <- { members with Modules = Map.add name path members.Modules }
        | TopOpen (name, pos) ->
            // 모듈 안에서 그 안의 모듈 N 은 M.N
            let path = resolveIn env.Modules env name
            match Map.tryFind path declared with
            | Some m ->
                let merge (outer: Map<string, string>) (inner: Map<string, string>) = Map.fold (fun acc k v -> Map.add k v acc) outer inner
                env <-
                    { Values = merge env.Values m.Values
                      Ctors = merge env.Ctors m.Ctors
                      Types = merge env.Types m.Types
                      Modules = merge env.Modules m.Modules }
            | None -> Errors.syntaxError (sprintf "Unknown module: %s" name) pos
    { Env = env; Declared = declared }, members, List.ofSeq output

/// REPL: 앞 입력의 scope 에서 이어서
let resolve (scope: Scope) (items: TopItem list) : Scope * TopItem list =
    let scope, _, items = resolveItems "" scope items
    scope, items

/// 프로그램 하나
let resolveProgram (items: TopItem list) : TopItem list = snd (resolve empty items)
