/// 대화형 실행 (OCaml toplevel, dotnet fsi 처럼). 입력 하나는 줄 끝의 ;; 까지.
/// 앞에서 정의한 이름과 타입은 다음 입력에서도 보임
module Repl

open System.IO
open System.Text
open Ast

type State =
    { /// 지금까지 받아들인 선언과 식. 타입 추론할 때 새 입력 앞에 let ... in 사슬로 붙임 (chain)
      Declarations: TopItem list
      /// 평가 환경. 선언을 다시 평가하지 않으므로 앞 입력의 print 가 되풀이되지 않음
      Env: Eval.Env
      /// 앞 입력에서 선언한 모듈과 open 한 이름 (Modules.resolve 가 새 입력의 이름을 바꿀 때 씀)
      Modules: Modules.Scope }

/// prelude 의 선언을 모두 받아들인 상태. 값은 함수뿐이라 바로 만들 수 있음 (출력 없음)
let private withPrelude =
    lazy
        (let declare env item =
            match item with
            | TopLet (name, value, _) -> Map.add name (Eval.evalIn env value) env
            | TopLetRec (name, param, body, _) -> Eval.declareRecGroup env [ name, param, body ]
            | TopLetRecs (bindings, _) -> Eval.declareRecGroup env bindings
            | _ -> failwith "prelude.calc: only let and let rec declarations are supported"
         let items = Prelude.items ()
         { Declarations = items; Env = List.fold declare Eval.builtins items; Modules = Modules.empty })

/// 처음 상태: prelude 의 정의가 모두 보임
let initial () = withPrelude.Value

/// 앞 입력들을 body 앞에 let ... in 사슬로. 식 입력도 let $it = e in 으로 넣음: 식이 정한 타입이 다음 입력에도 남도록
/// (let r = ref [] 의 '_a 는 r := [1] 이 int 로 정함. 빼면 다음 입력의 r := ["a"] 가 통과해서 soundness 가 깨짐)
let rec private chain (items: TopItem list) (body: Expr) =
    match items with
    | [] -> body
    | TopExpr e :: rest -> Let ("$it", e, chain rest body)
    | item :: rest -> Prelude.chain [ item ] (chain rest body)

/// 패턴이 묶는 이름 (쓴 순서)
let rec private patternNames pattern =
    match pattern with
    | PVar name -> [ name ]
    | PCons (h, t) -> patternNames h @ patternNames t
    | PTuple items -> List.collect patternNames items
    | PRecord fields -> List.collect (snd >> patternNames) fields
    | PCtor (_, Some p) -> patternNames p
    | POr (a, _) -> patternNames a
    | PWildcard | PInt _ | PBool _ | PString _ | PNil | PUnit | PCtor (_, None) -> []

let private typeOf (expr: Expr) =
    Infer.typeToString (Frontend.runWithLargeStack (fun () -> Infer.typecheck expr))

/// 선언들 다음에 보이는 name 의 타입. generalize 하지 않은 변수(value restriction)는 '_a: name 을 두 번 쓴 식에서
/// 두 번 다 같은 변수로 나오는 것
let private bindingType (declarations: TopItem list) (name: string) =
    match Frontend.runWithLargeStack (fun () -> Infer.typecheck (chain declarations (Tuple [ Var name; Var name ]))) |> Infer.prune with
    | Infer.TyTuple [ first; second ] -> Infer.weakTypeToString first second
    | t -> Infer.typeToString t

/// expr 의 경고 중 base 에 없던 것 (앞 선언의 경고를 되풀이하지 않음. 같은 경고가 여럿이면 개수로 뺌)
let private newWarnings (baseExpr: Expr) (expr: Expr) =
    let warningsOf e =
        snd (Frontend.runWithLargeStack (fun () -> Infer.typecheckWithWarnings e))
    let seen = warningsOf baseExpr |> List.countBy id |> dict |> System.Collections.Generic.Dictionary
    [ for w in warningsOf expr do
          match seen.TryGetValue w with
          | true, n when n > 0 -> seen[w] <- n - 1
          | _ -> yield w ]

let private report (out: TextWriter) (input: string) (kind: string) (message: string) (position: (int * int) option) =
    match position with
    | Some (line, column) ->
        out.WriteLine(sprintf "%s: %s (line %d, column %d)" kind message line column)
        Frontend.errorLocation input line column |> List.iter out.WriteLine
    | None -> out.WriteLine(sprintf "%s: %s" kind message)

/// 항목 하나: 타입을 확인하고, 평가하고, OCaml 처럼 결과를 보여 줌. 새 상태를 돌려줌
let private processItem (out: TextWriter) (input: string) (state: State) (item: TopItem) : State =
    let declarations = state.Declarations @ [ item ]
    let warn expr =
        for w in newWarnings (chain state.Declarations Unit) expr do
            report out input "Warning" w.Message w.Position
    let evaluate (expr: Expr) =
        Frontend.runWithLargeStack (fun () -> Eval.withOutput out (fun () -> Eval.evalIn state.Env expr))
    match item with
    | TopExpr expr ->
        let body = chain state.Declarations expr
        warn body
        let t = typeOf body
        let value = evaluate expr
        out.WriteLine(sprintf "- : %s = %s" t (Eval.formatValue value))
        { state with Declarations = declarations }
    | TopLet (name, value, _) ->
        warn (chain declarations Unit)
        let t = bindingType declarations name
        let v = evaluate value
        out.WriteLine(sprintf "val %s : %s = %s" name t (Eval.formatValue v))
        { state with Declarations = declarations; Env = Map.add name v state.Env }
    | TopLetRec (name, param, body, _) ->
        warn (chain declarations Unit)
        let t = typeOf (chain declarations (Var name))
        let v = (Eval.declareRecGroup state.Env [ name, param, body ])[name]
        out.WriteLine(sprintf "val %s : %s = %s" name t (Eval.formatValue v))
        { state with Declarations = declarations; Env = Map.add name v state.Env }
    | TopLetRecs (bindings, _) ->
        warn (chain declarations Unit)
        let env = Eval.declareRecGroup state.Env bindings
        for name, _, _ in bindings do
            out.WriteLine(sprintf "val %s : %s = %s" name (typeOf (chain declarations (Var name))) (Eval.formatValue env[name]))
        { state with Declarations = declarations; Env = env }
    | TopLetPattern (pattern, value, _) ->
        warn (chain declarations Unit)
        let types = patternNames pattern |> List.map (fun name -> name, bindingType declarations name)
        let wildcardType = if pattern = PWildcard then Some (typeOf (chain state.Declarations value)) else None
        let v = evaluate value
        match Eval.matchPattern pattern v state.Env with
        | None -> Eval.matchFailure v
        | Some env ->
            match wildcardType with
            | Some t -> out.WriteLine(sprintf "- : %s = %s" t (Eval.formatValue v))
            | None ->
                for name, t in types do
                    out.WriteLine(sprintf "val %s : %s = %s" name t (Eval.formatValue env[name]))
            { state with Declarations = declarations; Env = env }
    | TopType (name, typeParams, _, _)
    | TopRecord (name, typeParams, _, _) ->
        typeOf (chain declarations Unit) |> ignore
        let shown =
            match typeParams with
            | [] -> name
            | [ p ] -> sprintf "'%s %s" p name
            | ps -> sprintf "(%s) %s" (ps |> List.map (sprintf "'%s") |> String.concat ", ") name
        out.WriteLine(sprintf "type %s" shown)
        let env =
            match item with
            | TopRecord (_, _, fields, _) -> Eval.declareRecord state.Env name fields
            | TopType (_, _, ctors, _) -> Eval.declareConstructors state.Env name ctors
            | _ -> failwith "unreachable"
        { state with Declarations = declarations; Env = env }
    // OCaml 처럼 exception E of int. 인자 타입은 생성자의 타입 (int -> exn) 에서 꺼냄
    | TopException (name, argType, _) ->
        let ctorType = typeOf (chain declarations (Ctor name))
        let suffix = " -> exn"
        let shown = if ctorType.EndsWith suffix then sprintf "%s of %s" name (ctorType.Substring(0, ctorType.Length - suffix.Length)) else name
        out.WriteLine(sprintf "exception %s" shown)
        { state with Declarations = declarations; Env = Eval.declareConstructors state.Env "exn" [ name, argType ] }
    | TopModule _ | TopOpen _ -> failwith "Repl: modules are resolved by Modules.resolve"

/// 입력 하나(;; 로 끝나는 덩어리)를 처리. 오류가 나면 알리고, 그 앞 항목까지의 상태를 돌려줌
let submit (state: State) (input: string) (out: TextWriter) : State =
    // 모듈은 이름을 바꿔 펼친 선언으로 (Modules.resolve). 입력이 모두 처리되면 새 모듈 scope 를 남김
    let resolved =
        try
            Some (Modules.resolve state.Modules (Frontend.parseItems input))
        with Errors.SyntaxError (message, line, column) ->
            report out input "Error" message (Some (line, column))
            None
    let items = resolved |> Option.map snd
    let mutable current = state
    let mutable failed = false
    for item in defaultArg items [] do
        if not failed then
            try
                current <- processItem out input current item
            with
            | Infer.TypeError (message, position) ->
                report out input "Error" message position
                failed <- true
            | ex ->
                report out input "Error" ex.Message None
                failed <- true
    match resolved with
    | Some (modules, _) when not failed -> { current with Modules = modules }
    | _ -> current

/// 줄을 읽어 ;; 로 끝나는 입력마다 처리. #quit;; 나 입력 끝(Ctrl-D)에서 끝냄
let run (banner: string) (input: TextReader) (out: TextWriter) =
    out.WriteLine banner
    let buffer = StringBuilder()
    let mutable state = initial ()
    let mutable running = true
    while running do
        out.Write(if buffer.Length = 0 then "# " else "  ")
        out.Flush()
        match input.ReadLine() with
        | null ->
            // ;; 없이 끝난 마지막 입력도 처리
            if buffer.ToString().Trim() <> "" then state <- submit state (buffer.ToString()) out
            out.WriteLine()
            running <- false
        | line ->
            buffer.AppendLine line |> ignore
            if line.TrimEnd().EndsWith ";;" then
                let text = buffer.ToString()
                buffer.Clear() |> ignore
                if text.Trim() = "#quit;;" then running <- false
                else state <- submit state text out
