/// Hindley–Milner 타입 추론 (let 다형성). 단계(level) 기반 일반화를 쓰는 Algorithm W 변형.
/// 평가 전에 실행해서 타입이 맞지 않는 프로그램을 거부한다. 평가기는 그대로 동적 검사를 유지한다.
module Infer

open System.Collections.Generic
open Ast

type Type =
    /// int, bool, string, 'a list, 'a option, 사용자 타입. 이름은 고유 이름 (사용자 타입은 "shape@3")
    | TyCon of string * Type list
    | TyFun of Type * Type
    | TyTuple of Type list
    | TyVar of TypeVar

and TypeVar =
    { Id: int
      /// 단일화로 정해진 타입 (union-find 의 링크)
      mutable Link: Type option
      /// 이 변수가 생긴 let 깊이. genericLevel 이면 일반화된(∀) 변수
      mutable Level: int
      /// +, <, length, sort 처럼 일부 타입만 허용하는 변수 (타입 생성자 이름 목록). 이런 변수는 일반화하지 않음
      mutable Allowed: string list option }

/// 정적 타입 오류. position 은 원인이 된 식이나 패턴의 시작 (줄, 열; 1부터). Message 에는 위치를 넣지 않음
exception TypeError of message: string * position: (int * int) option with
    override this.Message = this.message

let private typeError message = raise (TypeError (message, None))

/// 위치가 없는 타입 오류에 node 의 위치를 붙임 (안쪽에서 이미 붙였으면 그대로)
let private locate (node: obj) (action: unit -> 'a) : 'a =
    try
        action ()
    with TypeError (message, None) ->
        raise (TypeError (message, Errors.positionOf node))

/// 패턴 매칭이 모든 값을 덮지 못함 (경고). position 은 match 또는 let 의 시작
type Warning = { Message: string; Position: (int * int) option }

let private genericLevel = System.Int32.MaxValue

/// 추론 한 번의 상태. 테스트가 병렬로 돌기 때문에 모듈 전역 변수 대신 호출마다 만듦
type private State =
    { mutable Level: int
      mutable NextId: int
      /// 사용자 타입의 고유 이름 → (타입 매개변수 변수, 생성자와 인자 타입). 빠진 분기 검사가 씀
      DataTypes: Dictionary<string, TypeVar list * (string * Type option) list>
      /// 검사할 패턴 매칭: 대상 타입, (패턴, 가드가 있는지) 목록, 위치. 추론이 끝난 뒤 타입이 다 정해지면 검사
      /// (패턴, 가드가 있는지, 분기의 위치)
      Matches: List<Type * (Pattern * bool * (int * int) option) list * (int * int) option * string>
      /// e1; e2 에서 버리는 e1 의 타입과 위치
      Discarded: List<Type * (int * int) option>
      /// 타입 표기의 'a 가 가리키는 변수. let 정의마다 범위 하나 (단계, 이름 → 변수), 안쪽 범위가 앞
      mutable TypeVarScopes: (int * Dictionary<string, Type>) list
      /// 타입 표기에 쓴 'a 와 그 변수, 처음 쓴 위치. 추론이 끝난 뒤 다른 타입으로 정해졌는지 확인
      NamedVars: List<string * Type * (int * int) option> }

type private Context =
    { Vars: Map<string, Type>
      /// 타입 이름 → (고유 이름, 타입 인자 수)
      Types: Map<string, string * int> }

let private tInt = TyCon ("int", [])
let private tBool = TyCon ("bool", [])
let private tString = TyCon ("string", [])
let private tUnit = TyCon ("unit", [])
let private listOf t = TyCon ("list", [ t ])
let private optionOf t = TyCon ("option", [ t ])

let rec prune t =
    match t with
    | TyVar ({ Link = Some linked } as v) ->
        let root = prune linked
        v.Link <- Some root
        root
    | _ -> t

let private newVarAt (st: State) level allowed =
    st.NextId <- st.NextId + 1
    TyVar { Id = st.NextId; Link = None; Level = level; Allowed = allowed }

let private newVar (st: State) = newVarAt st st.Level None

// ---- 출력 ----

/// 고유 이름 "shape@3" 은 "shape" 로 보여 줌
let private baseName (name: string) =
    match name.IndexOf '@' with
    | -1 -> name
    | i -> name.Substring(0, i)

/// 여러 타입을 같은 변수 이름 규칙('a, 'b, ...)으로 출력 (오류 메시지에서 두 타입을 함께 보여 줄 때)
let typesToString (types: Type list) =
    // 같이 출력하는 타입 안에 이름이 같은 서로 다른 타입이 있으면 OCaml 처럼 t/2 로 구분
    let uniques = List<string>()
    let rec collect t =
        match prune t with
        | TyCon (name, args) ->
            if not (uniques.Contains name) then uniques.Add name
            List.iter collect args
        | TyFun (a, b) ->
            collect a
            collect b
        | TyTuple items -> List.iter collect items
        | TyVar _ -> ()
    List.iter collect types
    let displayName (name: string) =
        let sameBase = uniques |> Seq.filter (fun u -> baseName u = baseName name) |> List.ofSeq
        match sameBase with
        | [ _ ] -> baseName name
        | _ -> sprintf "%s/%d" (baseName name) (List.findIndex ((=) name) sameBase + 1)
    let names = Dictionary<int, string>()
    let nameOf (v: TypeVar) =
        match names.TryGetValue v.Id with
        | true, n -> n
        | _ ->
            let i = names.Count
            let n = "'" + string (char (int 'a' + i % 26)) + (if i >= 26 then string (i / 26) else "")
            names.[v.Id] <- n
            n
    // prec: 0 = 화살표 자리, 1 = 화살표 왼쪽, 2 = 튜플 원소·타입 인자 자리
    let rec go prec t =
        let paren cond (s: string) = if cond then "(" + s + ")" else s
        match prune t with
        | TyVar v -> nameOf v
        | TyCon (name, []) -> displayName name
        | TyCon (name, [ arg ]) -> go 2 arg + " " + displayName name
        | TyCon (name, args) -> "(" + (args |> List.map (go 0) |> String.concat ", ") + ") " + displayName name
        | TyTuple items -> paren (prec >= 2) (items |> List.map (go 2) |> String.concat " * ")
        | TyFun (a, b) -> paren (prec >= 1) (go 1 a + " -> " + go 0 b)
    types |> List.map (go 0)

let typeToString t = typesToString [ t ] |> List.head

// ---- 단일화 ----

exception private Mismatch
exception private NotAllowed of allowed: string list * actual: Type
exception private Infinite of TypeVar * Type

/// v 가 t 안에 있는지 확인하면서, t 안의 변수 단계를 v 의 단계 이하로 낮춤 (일반화가 맞게 되도록)
let rec private occursAdjust (v: TypeVar) t =
    match prune t with
    | TyVar other ->
        if other.Id = v.Id then true
        else
            if other.Level > v.Level then other.Level <- v.Level
            false
    | TyCon (_, args) -> args |> List.exists (occursAdjust v)
    | TyFun (a, b) -> occursAdjust v a || occursAdjust v b
    | TyTuple items -> items |> List.exists (occursAdjust v)

let private bindVar (v: TypeVar) t =
    match t with
    | TyVar other ->
        // 두 변수를 잇기: 허용 목록은 교집합, 단계는 낮은 쪽
        let allowed =
            match v.Allowed, other.Allowed with
            | None, a
            | a, None -> a
            | Some a, Some b ->
                match a |> List.filter (fun x -> List.contains x b) with
                | [] -> raise (NotAllowed (a, t))
                | both -> Some both
        other.Allowed <- allowed
        other.Level <- min v.Level other.Level
        v.Link <- Some t
    | _ ->
        if occursAdjust v t then raise (Infinite (v, t))
        match v.Allowed, t with
        | Some allowed, TyCon (name, _) when List.contains name allowed -> ()
        | Some allowed, _ -> raise (NotAllowed (allowed, t))
        | None, _ -> ()
        v.Link <- Some t

let rec private unify t1 t2 =
    match prune t1, prune t2 with
    | TyVar a, TyVar b when a.Id = b.Id -> ()
    | TyVar v, t
    | t, TyVar v -> bindVar v t
    | TyCon (n1, args1), TyCon (n2, args2) when n1 = n2 && List.length args1 = List.length args2 ->
        List.iter2 unify args1 args2
    | TyFun (a1, b1), TyFun (a2, b2) ->
        unify a1 a2
        unify b1 b2
    | TyTuple items1, TyTuple items2 when List.length items1 = List.length items2 ->
        List.iter2 unify items1 items2
    | _ -> raise Mismatch

/// expected 자리에 actual 이 왔을 때. 실패하면 두 타입 전체를 보여 주는 오류
let private unifyAt expected actual =
    try
        unify expected actual
    with
    | Mismatch ->
        match typesToString [ expected; actual ] with
        | [ e; a ] -> typeError (sprintf "Type mismatch: expected %s but got %s" e a)
        | _ -> failwith "unreachable"
    | NotAllowed (allowed, t) ->
        typeError (sprintf "Type mismatch: expected %s but got %s" (String.concat " or " allowed) (typeToString t))
    | Infinite (v, t) ->
        match typesToString [ TyVar v; t ] with
        | [ a; b ] -> typeError (sprintf "Type mismatch: cannot construct infinite type %s = %s" a b)
        | _ -> failwith "unreachable"

// ---- 일반화와 인스턴스화 ----

/// 현재 단계보다 깊은 변수를 ∀ 로 만듦. 허용 목록이 있는 변수는 일반화하지 않음 (첫 사용이 타입을 정함)
let rec private generalize (st: State) t =
    match prune t with
    | TyVar v when v.Level > st.Level && v.Level <> genericLevel && v.Allowed.IsNone -> v.Level <- genericLevel
    | TyVar _ -> ()
    | TyCon (_, args) -> List.iter (generalize st) args
    | TyFun (a, b) ->
        generalize st a
        generalize st b
    | TyTuple items -> List.iter (generalize st) items

/// ∀ 변수마다 새 변수를 만들어 복사 (허용 목록도 복사)
let private instantiate (st: State) t =
    let fresh = Dictionary<int, Type>()
    let rec go t =
        match prune t with
        | TyVar v when v.Level = genericLevel ->
            match fresh.TryGetValue v.Id with
            | true, copy -> copy
            | _ ->
                let copy = newVarAt st st.Level v.Allowed
                fresh.[v.Id] <- copy
                copy
        | TyVar _ as t -> t
        | TyCon (name, args) -> TyCon (name, List.map go args)
        | TyFun (a, b) -> TyFun (go a, go b)
        | TyTuple items -> TyTuple (List.map go items)
    go t

// ---- 처음 환경 ----

let private initialContext (st: State) =
    let gen allowed = newVarAt st genericLevel allowed
    let a = gen None
    let ( ^-> ) x y = TyFun (x, y)
    let intOrString = gen (Some [ "int"; "string" ])
    let stringOrList = gen (Some [ "string"; "list" ])
    let vars =
        Map.ofList [
            // 문자열
            "length", stringOrList ^-> tInt
            "substr", tString ^-> tInt ^-> tInt ^-> tString
            "upper", tString ^-> tString
            "lower", tString ^-> tString
            "trim", tString ^-> tString
            "contains", tString ^-> tString ^-> tBool
            "startsWith", tString ^-> tString ^-> tBool
            "endsWith", tString ^-> tString ^-> tBool
            "indexOf", tString ^-> tString ^-> tInt
            "replace", tString ^-> tString ^-> tString ^-> tString
            "split", tString ^-> tString ^-> listOf tString
            "join", tString ^-> listOf tString ^-> tString
            "toString", a ^-> tString
            "toInt", tString ^-> tInt
            // 리스트
            "head", listOf a ^-> a
            "tail", listOf a ^-> listOf a
            "nth", listOf a ^-> tInt ^-> a
            "sort", listOf intOrString ^-> listOf intOrString
            // option
            "None", optionOf a
            "Some", a ^-> optionOf a
            // 출력
            "print", a ^-> tUnit
            "println", a ^-> tUnit
            // map, fold, fst, ignore 같은 함수는 prelude.calc 에서 추론한 타입
        ]
    let types =
        Map.ofList [ "int", ("int", 0); "bool", ("bool", 0); "string", ("string", 0); "unit", ("unit", 0); "list", ("list", 1); "option", ("option", 1) ]
    { Vars = vars; Types = types }

// ---- 추론 ----

/// type 선언이나 타입 표기의 TypeExpr 를 Type 으로. typeVar 는 'a 가 가리키는 타입
let rec private convertType (ctx: Context) (typeVar: string -> Type) te =
    match te with
    | TName (name, args) ->
        match Map.tryFind name ctx.Types with
        | Some (unique, arity) when arity = List.length args ->
            TyCon (unique, args |> List.map (convertType ctx typeVar))
        | Some (_, arity) ->
            typeError (sprintf "Type %s expects %d type argument(s) but got %d" name arity (List.length args))
        | None -> typeError (sprintf "Unknown type: %s" name)
    | TVar name -> typeVar name
    | TTuple items -> TyTuple (items |> List.map (convertType ctx typeVar))
    | TFun (a, b) -> TyFun (convertType ctx typeVar a, convertType ctx typeVar b)

/// 타입 표기의 'a: 바깥 범위까지 찾아보고, 없으면 가장 안쪽 범위(지금 let 정의)의 단계에서 새 변수.
/// OCaml 처럼 일반화를 강제하지 않음: let f (x: 'a) = x + 1 은 int -> int (대신 경고)
let private namedTypeVar (st: State) (node: obj) (name: string) =
    let found =
        st.TypeVarScopes
        |> List.tryPick (fun (_, vars) ->
            match vars.TryGetValue name with
            | true, t -> Some t
            | _ -> None)
    match found with
    | Some t -> t
    | None ->
        let level, vars = List.head st.TypeVarScopes
        let t = newVarAt st level None
        vars[name] <- t
        st.NamedVars.Add((name, t, Errors.positionOf node))
        t

/// let 정의의 값을 추론하는 동안 'a 의 범위 하나를 엶 (단계를 올린 뒤에 부름).
/// 타입을 적은 매개변수가 만든 let x = (x : t) 는 열지 않음: fun (x: 'a) (y: 'a) 의 두 'a 는 같음
let private withTypeVarScope (st: State) (opens: bool) (action: unit -> 'a) : 'a =
    if not opens then action ()
    else
        let saved = st.TypeVarScopes
        st.TypeVarScopes <- (st.Level, Dictionary()) :: saved
        try
            action ()
        finally
            st.TypeVarScopes <- saved

/// 패턴의 타입과, 패턴이 묶는 이름들 (단형)
let rec private inferPattern (st: State) (ctx: Context) pattern : Type * (string * Type) list =
    locate pattern (fun () -> inferPatternNode st ctx pattern)

and private inferPatternNode (st: State) (ctx: Context) pattern : Type * (string * Type) list =
    match pattern with
    | PVar name ->
        let t = newVar st
        t, [ name, t ]
    | PWildcard -> newVar st, []
    | PUnit -> tUnit, []
    // 두 쪽의 타입이 같고, 같은 이름은 같은 타입
    | POr (a, b) ->
        let ta, bindsA = inferPattern st ctx a
        let tb, bindsB = inferPattern st ctx b
        locate b (fun () -> unifyAt ta tb)
        let typesB = Map.ofList bindsB
        for name, t in bindsA do
            locate b (fun () -> unifyAt t typesB[name])
        ta, bindsA
    | PInt _ -> tInt, []
    | PBool _ -> tBool, []
    | PString _ -> tString, []
    | PNil -> listOf (newVar st), []
    | PCons (head, tail) ->
        let th, bh = inferPattern st ctx head
        let tt, bt = inferPattern st ctx tail
        unifyAt (listOf th) tt
        listOf th, bh @ bt
    | PTuple items ->
        let results = items |> List.map (inferPattern st ctx)
        TyTuple (results |> List.map fst), results |> List.collect snd
    | PCtor (name, arg) ->
        match Map.tryFind name ctx.Vars with
        | None -> typeError (sprintf "Unknown constructor: %s" name)
        | Some scheme ->
            match prune (instantiate st scheme), arg with
            | TyFun (argType, result), Some p ->
                let tp, binds = inferPattern st ctx p
                unifyAt argType tp
                result, binds
            | TyFun _, None -> typeError (sprintf "Constructor %s requires an argument" name)
            | result, None -> result, []
            | _, Some _ -> typeError (sprintf "Constructor %s takes no argument" name)

/// 식의 타입. 위치가 없는 타입 오류에는 이 식의 위치를 붙임 (가장 안쪽 식이 먼저 붙임)
let rec private infer (st: State) (ctx: Context) expr : Type =
    locate expr (fun () -> inferNode st ctx expr)

and private inferNode (st: State) (ctx: Context) expr : Type =
    let infer' = infer st ctx
    /// e 의 타입이 expected 여야 함. 맞지 않으면 e 를 가리키는 오류 (OCaml 처럼 문제가 된 식)
    let expect (e: obj) expected actual = locate e (fun () -> unifyAt expected actual)
    let withVars binds (ctx: Context) =
        { ctx with Vars = binds |> List.fold (fun vars (name, t) -> Map.add name t vars) ctx.Vars }
    /// +, <: int 끼리 또는 string 끼리
    let sameIntOrString a b =
        let t = newVarAt st st.Level (Some [ "int"; "string" ])
        expect a t (infer' a)
        expect b t (infer' b)
        t
    let ints a b =
        expect a tInt (infer' a)
        expect b tInt (infer' b)
    let bools a b =
        expect a tBool (infer' a)
        expect b tBool (infer' b)
    match expr with
    | Number _ -> tInt
    | Bool _ -> tBool
    | Str _ -> tString
    | Unit -> tUnit
    | Seq (first, rest) ->
        // 버리는 값이 unit 이 아니면 경고 (F# FS0020, OCaml Warning 10). 타입이 다 정해진 뒤에 확인
        st.Discarded.Add((infer' first, Errors.positionOf first))
        infer' rest
    | Var name ->
        match Map.tryFind name ctx.Vars with
        | Some scheme -> instantiate st scheme
        | None -> typeError (sprintf "Unbound variable: %s" name)
    | Ctor name ->
        match Map.tryFind name ctx.Vars with
        | Some scheme -> instantiate st scheme
        | None -> typeError (sprintf "Unknown constructor: %s" name)
    | Let (name, value, body) ->
        // let 다형성: 값의 타입을 한 단계 안에서 추론하고, 그 단계에서 생긴 변수를 ∀ 로 만듦
        st.Level <- st.Level + 1
        let isParam =
            match value with
            | Annot (Var v, _) -> v = name
            | _ -> false
        let tv = withTypeVarScope st (not isParam) (fun () -> infer' value)
        st.Level <- st.Level - 1
        generalize st tv
        infer st (withVars [ name, tv ] ctx) body
    | LetRec (name, param, body, inExpr) ->
        st.Level <- st.Level + 1
        let tf = newVar st
        let tp = newVar st
        let tb = withTypeVarScope st true (fun () -> infer st (withVars [ name, tf; param, tp ] ctx) body)
        unifyAt tf (TyFun (tp, tb))
        st.Level <- st.Level - 1
        generalize st tf
        infer st (withVars [ name, tf ] ctx) inExpr
    // let rec ... and ...: 그룹 안에서는 서로 단형, 끝나면 함께 generalize
    | LetRecs (bindings, inExpr) ->
        st.Level <- st.Level + 1
        let fnTypes = bindings |> List.map (fun (name, _, _) -> name, newVar st)
        let groupCtx = withVars fnTypes ctx
        // 그룹 전체가 'a 의 범위 하나
        withTypeVarScope st true (fun () ->
            for (_, param, body), (_, tf) in List.zip bindings fnTypes do
                let tp = newVar st
                let tb = infer st (withVars [ param, tp ] groupCtx) body
                locate body (fun () -> unifyAt tf (TyFun (tp, tb))))
        st.Level <- st.Level - 1
        fnTypes |> List.iter (snd >> generalize st)
        infer st (withVars fnTypes ctx) inExpr
    | Lambda (param, body) ->
        let tp = newVar st
        TyFun (tp, infer st (withVars [ param, tp ] ctx) body)
    | App (f, arg) ->
        let tf = infer' f
        let ta = infer' arg
        match prune tf with
        | TyFun (param, result) ->
            expect arg param ta
            result
        | TyVar _ ->
            let result = newVar st
            unifyAt tf (TyFun (ta, result))
            result
        | other -> locate f (fun () -> typeError (sprintf "Type mismatch: expected function but got %s" (typeToString other)))
    | If (cond, thenExpr, elseExpr) ->
        expect cond tBool (infer' cond)
        let t = infer' thenExpr
        expect elseExpr t (infer' elseExpr)
        t
    | Add (a, b) -> sameIntOrString a b
    | Subtract (a, b)
    | Multiply (a, b)
    | Divide (a, b)
    | Modulo (a, b) ->
        ints a b
        tInt
    | Negate e ->
        expect e tInt (infer' e)
        tInt
    | Equal (a, b)
    | NotEqual (a, b) ->
        // OCaml 처럼 모든 타입에 대해 'a -> 'a -> bool. 함수 비교는 실행 중 오류로 남음
        let t = infer' a
        expect b t (infer' b)
        tBool
    | Less (a, b)
    | Greater (a, b)
    | LessEqual (a, b)
    | GreaterEqual (a, b) ->
        sameIntOrString a b |> ignore
        tBool
    | And (a, b)
    | Or (a, b) ->
        bools a b
        tBool
    | Not e ->
        expect e tBool (infer' e)
        tBool
    | Nil -> listOf (newVar st)
    | Cons (head, tail) ->
        let th = infer' head
        expect tail (listOf th) (infer' tail)
        listOf th
    | Tuple items -> TyTuple (items |> List.map infer')
    | Match (scrutinee, cases) ->
        let ts = infer' scrutinee
        let result = newVar st
        for pattern, guard, body in cases do
            let tp, binds = inferPattern st ctx pattern
            expect pattern ts tp
            let caseCtx = withVars binds ctx
            guard |> Option.iter (fun g -> expect g tBool (infer st caseCtx g))
            expect body result (infer st caseCtx body)
        st.Matches.Add((ts, cases |> List.map (fun ((p, g, _) as case) -> p, Option.isSome g, Errors.positionOf case), Errors.positionOf expr, "match"))
        result
    | LetPattern (pattern, value, body) ->
        st.Level <- st.Level + 1
        let tv = withTypeVarScope st true (fun () -> infer' value)
        let tp, binds = inferPattern st ctx pattern
        expect value tp tv
        st.Level <- st.Level - 1
        st.Matches.Add((tv, [ pattern, false, None ], Errors.positionOf expr, "let"))
        binds |> List.iter (snd >> generalize st)
        infer st (withVars binds ctx) body
    | TypeDecl (name, typeParams, ctors, body) ->
        match typeParams |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
        | Some (p, _) -> typeError (sprintf "Duplicate type variable: '%s" p)
        | None -> ()
        // 같은 이름의 다른 type 선언과 섞이지 않도록 고유 이름 (출력할 때는 @ 뒤를 뗌)
        st.NextId <- st.NextId + 1
        let unique = sprintf "%s@%d" name st.NextId
        let paramVars = typeParams |> List.map (fun p -> p, newVarAt st genericLevel None)
        let declCtx = { ctx with Types = Map.add name (unique, List.length typeParams) ctx.Types }
        let result = TyCon (unique, paramVars |> List.map snd)
        let ctorArgs =
            let typeParam name =
                match List.tryFind (fun (p, _) -> p = name) paramVars with
                | Some (_, t) -> t
                | None -> typeError (sprintf "Unbound type variable: '%s" name)
            ctors |> List.map (fun (ctor, argType) -> ctor, argType |> Option.map (convertType declCtx typeParam))
        let paramTypeVars =
            paramVars |> List.map (fun (_, t) -> match t with | TyVar v -> v | _ -> failwith "unreachable")
        st.DataTypes[unique] <- (paramTypeVars, ctorArgs)
        let ctorTypes =
            ctorArgs
            |> List.map (fun (ctor, arg) ->
                match arg with
                | None -> ctor, result
                | Some t -> ctor, TyFun (t, result))
        infer st (withVars ctorTypes declCtx) body
    | Annot (e, te) ->
        let t = convertType ctx (namedTypeVar st expr) te
        expect e t (infer' e)
        t

/// 아직 정해지지 않은 허용 목록 변수는 첫 번째 허용 타입으로 정함 (F# 가 + 를 int 로 정하는 것과 같음)
let rec private applyDefaults (st: State) t =
    match prune t with
    | TyVar ({ Allowed = Some (first :: _) } as v) ->
        v.Link <- Some (if first = "list" then listOf (newVar st) else TyCon (first, []))
    | TyVar _ -> ()
    | TyCon (_, args) -> List.iter (applyDefaults st) args
    | TyFun (a, b) ->
        applyDefaults st a
        applyDefaults st b
    | TyTuple items -> List.iter (applyDefaults st) items

// ---- 빠진 분기 검사 (Maranget, "Warnings for pattern matching", 2007) ----

/// 검사용 패턴: 무엇이든(_) 또는 생성자와 하위 패턴. 생성자 이름: "true", "false", "[]", "::", "tuple", 생성자 이름,
/// 정수·문자열 상수는 "int:5", "str:..." (값이 무한히 많은 타입)
type private Pat =
    | Any
    | Con of string * Pat list
    | Or of Pat * Pat

let rec private toPat pattern =
    match pattern with
    | PVar _
    | PWildcard -> Any
    | PInt n -> Con (sprintf "int:%d" n, [])
    | PBool b -> Con ((if b then "true" else "false"), [])
    | PString s -> Con ("str:" + s, [])
    | PNil -> Con ("[]", [])
    | PCons (h, t) -> Con ("::", [ toPat h; toPat t ])
    | PTuple items -> Con ("tuple", List.map toPat items)
    | PCtor (name, None) -> Con (name, [])
    | PCtor (name, Some p) -> Con (name, [ toPat p ])
    | PUnit -> Con ("()", [])
    | POr (a, b) -> Or (toPat a, toPat b)

/// 타입의 생성자 전부 (이름, 하위 타입). 값이 무한히 많은 타입(int, string)과 정해지지 않은 타입은 None
let private signature (st: State) (t: Type) : (string * Type list) list option =
    match prune t with
    | TyCon ("bool", []) -> Some [ "true", []; "false", [] ]
    | TyCon ("unit", []) -> Some [ "()", [] ]
    | TyCon ("list", [ a ]) as listType -> Some [ "[]", []; "::", [ a; listType ] ]
    | TyCon ("option", [ a ]) -> Some [ "None", []; "Some", [ a ] ]
    | TyTuple items -> Some [ "tuple", items ]
    | TyCon (name, args) when st.DataTypes.ContainsKey name ->
        let paramVars, ctors = st.DataTypes[name]
        let substitution = List.zip (paramVars |> List.map (fun v -> v.Id)) args |> Map.ofList
        let rec subst t =
            match prune t with
            | TyVar v -> substitution |> Map.tryFind v.Id |> Option.defaultValue t
            | TyCon (n, xs) -> TyCon (n, List.map subst xs)
            | TyFun (a, b) -> TyFun (subst a, subst b)
            | TyTuple xs -> TyTuple (List.map subst xs)
        Some (ctors |> List.map (fun (ctor, arg) -> ctor, arg |> Option.map subst |> Option.toList))
    | _ -> None

/// 행렬 rows(각 행은 열마다 패턴)이 덮지 못하는 값의 예. 모두 덮으면 None
let rec private missing (st: State) (rows: Pat list list) (types: Type list) : Pat list option =
    // 첫 열의 p1 | p2 는 행 두 개로 펼침
    let rec expand row =
        match row with
        | Or (a, b) :: tail -> expand (a :: tail) @ expand (b :: tail)
        | _ -> [ row ]
    let rows = List.collect expand rows
    match types with
    | [] -> if List.isEmpty rows then Some [] else None
    | t :: rest ->
        let heads = rows |> List.choose (function Con (name, _) :: _ -> Some name | _ -> None) |> Set.ofList
        // 생성자 c 로 시작하는 값만 남김: 그 생성자의 하위 패턴을 열로 펼침
        let specialize name arity =
            rows
            |> List.choose (fun row ->
                match row with
                | Con (n, args) :: tail when n = name -> Some (args @ tail)
                | Any :: tail -> Some (List.replicate arity Any @ tail)
                | _ -> None)
        let defaultRows = rows |> List.choose (function Any :: tail -> Some tail | _ -> None)
        match signature st t with
        | Some ctors when ctors |> List.forall (fun (name, _) -> heads.Contains name) ->
            // 모든 생성자가 나옴: 생성자마다 확인
            ctors
            |> List.tryPick (fun (name, argTypes) ->
                missing st (specialize name argTypes.Length) (argTypes @ rest)
                |> Option.map (fun w -> Con (name, List.take argTypes.Length w) :: List.skip argTypes.Length w))
        | ctors ->
            // 나오지 않은 생성자가 있음: _ 로 시작하는 행만으로 나머지 열을 덮는지
            missing st defaultRows rest
            |> Option.map (fun w ->
                let head =
                    match ctors with
                    // 이 열에 생성자가 하나도 없으면 어떤 값이든 됨
                    | _ when heads.IsEmpty -> Any
                    | Some ctors ->
                        let name, argTypes = ctors |> List.find (fun (name, _) -> not (heads.Contains name))
                        Con (name, List.replicate argTypes.Length Any)
                    | None when heads.IsEmpty -> Any
                    // int, string: 나온 상수가 아닌 값
                    | None ->
                        match prune t with
                        | TyCon ("int", []) -> Con (sprintf "int:%d" (Seq.initInfinite id |> Seq.find (fun n -> not (heads.Contains(sprintf "int:%d" n)))), [])
                        | _ -> Con ("str:" + (Seq.initInfinite (fun n -> String.replicate n "a") |> Seq.find (fun s -> not (heads.Contains("str:" + s)))), [])
                head :: w)

/// 패턴 q(열마다 하나)가 행렬 rows 가 덮지 못하는 값과 맞을 수 있는지 (Maranget 의 U(P, q)). 아니면 그 분기는 쓰이지 않음
let rec private useful (st: State) (rows: Pat list list) (q: Pat list) (types: Type list) : bool =
    let rec expand row =
        match row with
        | Or (a, b) :: tail -> expand (a :: tail) @ expand (b :: tail)
        | _ -> [ row ]
    let rows = List.collect expand rows
    match q, types with
    | [], _ -> List.isEmpty rows
    | Or (a, b) :: qtail, _ -> useful st rows (a :: qtail) types || useful st rows (b :: qtail) types
    | _, [] -> false
    | qhead :: qtail, t :: rest ->
        let specialize name arity =
            rows
            |> List.choose (fun row ->
                match row with
                | Con (n, args) :: tail when n = name -> Some (args @ tail)
                | Any :: tail -> Some (List.replicate arity Any @ tail)
                | _ -> None)
        match qhead with
        | Con (name, args) ->
            // 생성자의 하위 타입: 타입이 생성자를 모두 알면 거기서, 상수(int, string)는 하위 패턴이 없음
            let argTypes =
                match signature st t with
                | Some ctors -> ctors |> List.tryFind (fst >> (=) name) |> Option.map snd |> Option.defaultValue []
                | None -> []
            useful st (specialize name args.Length) (args @ qtail) (argTypes @ rest)
        | Any ->
            let heads = rows |> List.choose (function Con (name, _) :: _ -> Some name | _ -> None) |> Set.ofList
            match signature st t with
            | Some ctors when not heads.IsEmpty && ctors |> List.forall (fun (name, _) -> heads.Contains name) ->
                ctors
                |> List.exists (fun (name, argTypes) ->
                    useful st (specialize name argTypes.Length) (List.replicate argTypes.Length Any @ qtail) (argTypes @ rest))
            | _ -> useful st (rows |> List.choose (function Any :: tail -> Some tail | _ -> None)) qtail rest
        | Or _ -> failwith "unreachable"

/// 값의 예를 소스 문법으로 (formatValue 와 비슷하게): Some _, _ :: _, (true, _)
let rec private patToString (prec: int) (p: Pat) =
    let paren cond (s: string) = if cond then "(" + s + ")" else s
    match p with
    | Any -> "_"
    | Con ("[]", []) -> "[]"
    | Con ("::", [ h; t ]) ->
        // 끝이 [] 인 사슬은 리스트 문법 [_; _]
        let rec items p =
            match p with
            | Con ("::", [ h; t ]) -> items t |> Option.map (fun rest -> h :: rest)
            | Con ("[]", []) -> Some []
            | _ -> None
        match items p with
        | Some xs -> "[" + (xs |> List.map (patToString 0) |> String.concat "; ") + "]"
        | None -> paren (prec > 0) (patToString 1 h + " :: " + patToString 0 t)
    | Con ("tuple", items) -> "(" + (items |> List.map (patToString 0) |> String.concat ", ") + ")"
    | Con (name, []) when name.StartsWith "int:" -> name.Substring 4
    | Con (name, []) when name.StartsWith "str:" -> "\"" + name.Substring 4 + "\""
    | Con (name, []) -> name
    | Con (name, [ arg ]) -> paren (prec > 1) (name + " " + patToString 2 arg)
    | Con (name, args) -> name + " " + (args |> List.map (patToString 2) |> String.concat " ")
    | Or (a, b) -> paren (prec > 0) (patToString 0 a + " | " + patToString 0 b)

let private checkMatches (st: State) : Warning list =
    [ for t, cases, position, kind in st.Matches do
          // 쓰이지 않는 분기: 앞의 (가드 없는) 분기들이 이미 모두 덮음 (OCaml Warning 11, F# FS0026)
          let mutable before = []
          for p, guarded, casePosition in cases do
              if not (useful st before [ toPat p ] [ t ]) then
                  yield { Message = "this match case is unused"; Position = casePosition |> Option.orElse position }
              if not guarded then before <- before @ [ [ toPat p ] ]
          // 가드가 있는 분기는 실패할 수 있으므로 덮는 데 셈하지 않음 (OCaml, F# 와 같음)
          let rows = cases |> List.filter (fun (_, guarded, _) -> not guarded) |> List.map (fun (p, _, _) -> [ toPat p ])
          match missing st rows [ t ] with
          | Some [ example ] ->
              let what = if kind = "let" then "this let pattern" else "this match"
              yield
                  { Message = sprintf "%s is not exhaustive. Example of a value not matched: %s" what (patToString 0 example)
                    Position = position }
          | _ -> () ]
    |> List.sortBy (fun w -> w.Position)

/// 식 전체의 타입과, 모든 값을 덮지 못하는 패턴 매칭의 경고. 타입이 맞지 않으면 TypeError
let typecheckWithWarnings (expr: Expr) : Type * Warning list =
    let st =
        { Level = 0
          NextId = 0
          DataTypes = Dictionary()
          Matches = List()
          Discarded = List()
          TypeVarScopes = [ 0, Dictionary() ]
          NamedVars = List() }
    let t = infer st (initialContext st) expr
    applyDefaults st t
    let discarded =
        [ for t, position in st.Discarded do
              match prune t with
              | TyCon ("unit", []) | TyVar _ -> ()
              | other ->
                  yield
                      { Message = sprintf "this expression has type %s and its value is ignored (use ignore or let _ = ...)" (typeToString other)
                        Position = position } ]
    // 'a 로 적었는데 다른 타입으로 정해짐 (F# FS0064). OCaml 은 경고하지 않음
    let constrained =
        [ for name, t, position in st.NamedVars do
              let shown =
                  match prune t with
                  | TyVar { Allowed = None } -> None
                  | TyVar { Allowed = Some allowed } -> Some (String.concat " or " allowed)
                  | other -> Some (typeToString other)
              match shown with
              | Some what ->
                  yield
                      { Message = sprintf "the type variable '%s has been constrained to be type %s" name what
                        Position = position }
              | None -> () ]
    t, checkMatches st @ discarded @ constrained |> List.sortBy (fun w -> w.Position)

/// 식 전체의 타입. 타입이 맞지 않으면 TypeError
let typecheck (expr: Expr) : Type = fst (typecheckWithWarnings expr)
