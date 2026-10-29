/// Calc 로 쓴 표준 라이브러리 (prelude.calc). 프로그램 앞에 let ... in 사슬로 붙여서
/// 인터프리터, 타입 추론, 컴파일러가 모두 같은 정의를 씀 (내장 함수처럼 F# 와 C 에 따로 두지 않음)
module Prelude

open System.IO
open System.Reflection
open Ast

/// prelude.calc 의 내용 (Calc.fsproj 의 EmbeddedResource)
let source =
    use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream "prelude.calc"
    use reader = new StreamReader(stream)
    reader.ReadToEnd()

/// prelude 의 선언들. 한 번만 파싱하고 모든 프로그램이 같은 노드를 씀 (AST 는 바뀌지 않음)
let private parsed = lazy (Frontend.parseItems source)

let items () = parsed.Value

/// 선언들을 body 앞에 let ... in 사슬로 (식 항목은 이름을 묶지 않으므로 뺌)
let chain (declarations: TopItem list) (body: Expr) =
    List.foldBack
        (fun item rest ->
            match item with
            | TopLet (name, value, _) -> Let (name, value, rest)
            | TopLetRec (name, param, b, _) -> LetRec (name, param, b, rest)
            | TopLetRecs (bindings, _) -> LetRecs (bindings, rest)
            | TopLetPattern (pattern, value, _) -> LetPattern (pattern, value, rest)
            | TopType (name, typeParams, ctors, _) -> TypeDecl (name, typeParams, ctors, rest)
            | TopRecord (name, typeParams, fields, _) -> RecordDecl (name, typeParams, fields, rest)
            | TopException (name, argType, _) -> ExceptionDecl (name, argType, rest)
            | TopExpr _ -> rest
            | TopModule _ | TopOpen _ -> failwith "Prelude.chain: modules are resolved by Modules.resolve")
        declarations
        body

/// 식 안에 Var 로 나오는 이름 전부. 묶인 이름도 포함하므로 실제로 쓰는 이름보다 많을 수 있음 (붙이는 정의가 늘 뿐)
let rec private varNames (expr: Expr) : Set<string> =
    match expr with
    | Var name -> Set.singleton name
    | Number _ | Bool _ | Str _ | Unit | Nil | Ctor _ | Printf _ -> Set.empty
    | Negate e | Not e | Annot (e, _) | Lambda (_, e) | TypeDecl (_, _, _, e) | RecordDecl (_, _, _, e) | ExceptionDecl (_, _, e)
    | Field (e, _) -> varNames e
    | Record fields -> fields |> List.map (snd >> varNames) |> Set.unionMany
    | RecordUpdate (e, fields) -> fields |> List.map (snd >> varNames) |> List.fold Set.union (varNames e)
    | Seq (a, b) | Let (_, a, b) | LetRec (_, _, a, b) | LetPattern (_, a, b) | App (a, b)
    | Add (a, b) | Subtract (a, b) | Multiply (a, b) | Divide (a, b) | Modulo (a, b)
    | Equal (a, b) | NotEqual (a, b) | Less (a, b) | Greater (a, b) | LessEqual (a, b) | GreaterEqual (a, b)
    | And (a, b) | Or (a, b) | Cons (a, b) -> Set.union (varNames a) (varNames b)
    | If (c, t, e) -> Set.unionMany [ varNames c; varNames t; varNames e ]
    | Tuple items -> items |> List.map varNames |> Set.unionMany
    | LetRecs (bindings, body) -> bindings |> List.map (fun (_, _, b) -> varNames b) |> List.fold Set.union (varNames body)
    | Match (scrutinee, cases) | Try (scrutinee, cases) ->
        cases
        |> List.map (fun (_, guard, body) -> Set.union (guard |> Option.map varNames |> Option.defaultValue Set.empty) (varNames body))
        |> List.fold Set.union (varNames scrutinee)

/// prelude 선언 중 program 이 (다른 prelude 선언을 거쳐서라도) 쓰는 것만 앞에 붙임.
/// 쓰지 않는 정의는 타입 추론, 평가, 컴파일 비용만 늘리므로 뺌
let wrap (program: Expr) : Expr =
    let needed, _ =
        List.foldBack
            (fun item (kept, names: Set<string>) ->
                let defines, uses =
                    match item with
                    | TopLet (name, value, _) -> [ name ], varNames value
                    | TopLetRec (name, _, body, _) -> [ name ], varNames body
                    | TopLetRecs (bindings, _) ->
                        bindings |> List.map (fun (name, _, _) -> name),
                        bindings |> List.map (fun (_, _, b) -> varNames b) |> Set.unionMany
                    | _ -> failwith "prelude.calc: only let and let rec declarations are supported"
                if defines |> List.exists names.Contains then item :: kept, Set.union names uses
                else kept, names)
            (items ())
            ([], varNames program)
    chain needed program
