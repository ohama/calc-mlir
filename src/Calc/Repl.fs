/// 대화형 실행 (OCaml toplevel, dotnet fsi 처럼). 입력 하나는 줄 끝의 ;; 까지.
/// 앞에서 정의한 이름과 타입은 다음 입력에서도 보임
module Repl

open System.IO
open System.Text
open Ast

type State =
    { /// 지금까지 받아들인 선언. 타입 추론할 때 새 입력 앞에 let ... in 사슬로 붙임
      Declarations: TopItem list
      /// 평가 환경. 선언을 다시 평가하지 않으므로 앞 입력의 print 가 되풀이되지 않음
      Env: Eval.Env }

let initial = { Declarations = []; Env = Eval.builtins }

/// 선언들을 body 앞에 let ... in 사슬로 (식 항목은 이름을 묶지 않으므로 뺌)
let private chain (declarations: TopItem list) (body: Expr) =
    List.foldBack
        (fun item rest ->
            match item with
            | TopLet (name, value, _) -> Let (name, value, rest)
            | TopLetRec (name, param, b, _) -> LetRec (name, param, b, rest)
            | TopLetPattern (pattern, value, _) -> LetPattern (pattern, value, rest)
            | TopType (name, typeParams, ctors, _) -> TypeDecl (name, typeParams, ctors, rest)
            | TopExpr _ -> rest)
        declarations
        body

/// 패턴이 묶는 이름 (쓴 순서)
let rec private patternNames pattern =
    match pattern with
    | PVar name -> [ name ]
    | PCons (h, t) -> patternNames h @ patternNames t
    | PTuple items -> List.collect patternNames items
    | PCtor (_, Some p) -> patternNames p
    | PWildcard | PInt _ | PBool _ | PString _ | PNil | PUnit | PCtor (_, None) -> []

let private typeOf (expr: Expr) =
    Infer.typeToString (Frontend.runWithLargeStack (fun () -> Infer.typecheck expr))

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
        state
    | TopLet (name, value, _) ->
        warn (chain declarations Unit)
        let t = typeOf (chain declarations (Var name))
        let v = evaluate value
        out.WriteLine(sprintf "val %s : %s = %s" name t (Eval.formatValue v))
        { Declarations = declarations; Env = Map.add name v state.Env }
    | TopLetRec (name, param, body, _) ->
        warn (chain declarations Unit)
        let t = typeOf (chain declarations (Var name))
        let v = Eval.VRecClosure (name, param, body, state.Env)
        out.WriteLine(sprintf "val %s : %s = %s" name t (Eval.formatValue v))
        { Declarations = declarations; Env = Map.add name v state.Env }
    | TopLetPattern (pattern, value, _) ->
        warn (chain declarations Unit)
        let types = patternNames pattern |> List.map (fun name -> name, typeOf (chain declarations (Var name)))
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
            { Declarations = declarations; Env = env }
    | TopType (name, typeParams, ctors, _) ->
        typeOf (chain declarations Unit) |> ignore
        let shown =
            match typeParams with
            | [] -> name
            | [ p ] -> sprintf "'%s %s" p name
            | ps -> sprintf "(%s) %s" (ps |> List.map (sprintf "'%s") |> String.concat ", ") name
        out.WriteLine(sprintf "type %s" shown)
        { Declarations = declarations; Env = Eval.declareConstructors state.Env name ctors }

/// 입력 하나(;; 로 끝나는 덩어리)를 처리. 오류가 나면 알리고, 그 앞 항목까지의 상태를 돌려줌
let submit (state: State) (input: string) (out: TextWriter) : State =
    let items =
        try
            Some (Frontend.parseItems input)
        with Errors.SyntaxError (message, line, column) ->
            report out input "Error" message (Some (line, column))
            None
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
    current

/// 줄을 읽어 ;; 로 끝나는 입력마다 처리. #quit;; 나 입력 끝(Ctrl-D)에서 끝냄
let run (banner: string) (input: TextReader) (out: TextWriter) =
    out.WriteLine banner
    let buffer = StringBuilder()
    let mutable state = initial
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
