/// indentation 문법 (F# light syntax): 렉서와 파서 사이에서 offside rule 로 가상 토큰을 넣음 (docs/indentation-design.md).
/// 블록(let 의 = 뒤, ->, then, else, try, do 뒤의 식)을 OBLOCKBEGIN … OBLOCKEND 로 감싸고, 블록의 열과 같은 열의 새 줄에는
/// 구분자(in, ;, ;;)를, 블록보다 왼쪽인 새 줄에서는 블록을 닫는 토큰을 넣음. 한 줄 안에서는 구분자를 넣지 않으므로
/// verbose syntax(in, ;, ;; 를 쓴 코드)의 뜻은 바뀌지 않음
module LayoutFilter

open System.Collections.Generic
open FSharp.Text.Lexing
open Parser

type private Tok = { Token: token; Start: Position; End: Position }

type private BlockKind =
    | LetValue
    | Arrow
    | Then
    | Else
    | Try
    | Do

type private Kind =
    /// 프로그램 (0열). 같은 열의 새 줄은 ;;
    | KTop
    /// 식 블록. 같은 열의 새 줄은 ;
    | KBlock of BlockKind
    /// let ... (= 뒤가 값). Decl 이면 최상위나 모듈의 선언 (몸이 없어도 됨), 아니면 같은 열의 새 줄이 in
    | KLet
    /// fun ... -> 의 -> 까지
    | KFun
    /// match, try, function: with 뒤(Arms)의 -> 가 분기 블록을 엶
    | KMatch
    /// ( [ { 와 짝이 되는 닫는 토큰. 같은 열의 새 줄은 ; (리스트, 레코드의 원소 구분자, 괄호 안의 순서 실행)
    | KParen of closer: token
    /// while, for: do 뒤가 블록, 닫을 때 done
    | KLoop
    /// module M 다음 = 까지
    | KModuleHead
    /// 모듈의 선언들. Light 면 struct 없이 들여쓴 것 (닫을 때 end)
    | KModule of light: bool

type private Ctx =
    { Kind: Kind
      /// offside line. 이 열보다 왼쪽인 새 줄이 context 를 닫음
      mutable Col: int
      /// KLet: = 를 지나 값을 읽는 중. KMatch: with 를 지나 분기를 읽는 중
      mutable Body: bool
      Decl: bool
      /// KMatch: 분기의 | 가 이 열까지 왼쪽에 있어도 됨 (let f x = match x with 다음 줄의 | , F# 와 같음)
      BarCol: int
      Pos: Position }

/// 다음 토큰이 여는 것
type private Pending =
    | OpenBlock of BlockKind
    /// 괄호나 struct 안의 첫 토큰이 그 context 의 열을 정함
    | FirstInside of Ctx
    /// module M = 다음: struct 가 아니면 들여쓴 모듈 (가상 struct)
    | ModuleBody

/// 식을 끝낼 수 있는 토큰 (Go 의 세미콜론 규칙처럼, 이것 뒤에서만 구분자를 넣음)
let private canEnd t =
    match t with
    | IDENT _ | QIDENT _ | CTOR _ | QCTOR _ | NUMBER _ | STRING _ | INTERP _ | TRUE | FALSE
    | RPAREN | RBRACKET | RBRACE | DONE | END -> true
    | _ -> false

/// 식이나 선언을 시작할 수 있는 토큰
let private canStart t =
    match t with
    | IDENT _ | QIDENT _ | CTOR _ | QCTOR _ | NUMBER _ | STRING _ | INTERP _ | TRUE | FALSE
    | LPAREN | LBRACKET | LBRACE | LET | IF | MATCH | FUN | FUNCTION | TRY | WHILE | FOR | NOT | BANG
    | TYPE | EXCEPTION | MODULE | OPEN -> true
    | _ -> false

let private isInfix t =
    match t with
    | PLUS | MINUS | STAR | SLASH | PERCENT | EQUALS | NOTEQUAL | LESS | GREATER | LESSEQUAL | GREATEREQUAL
    | AND | OR | PIPE | COMPOSE | CONS | COLONEQUALS -> true
    | _ -> false

/// 이 토큰으로 시작하는 줄은 앞 줄을 이음 (구분자를 넣지 않음)
let private continues t =
    isInfix t
    || (match t with
        | BAR | THEN | ELSE | WITH | ARROW | IN | ANDKW | DO | TO | DOWNTO | OF | COMMA | SEMI | SEMISEMI
        | RPAREN | RBRACKET | RBRACE | DONE | END | COLON | WHEN | DOT | EOF -> true
        | _ -> false)

/// 짝이 되는 context 를 찾아 닫는 토큰. 새 줄에 있어도 offside 규칙을 먼저 쓰지 않음
/// (else 가 if 의 열에 있어도 then 블록과 짝이 맞아야 하므로)
let private closesByItself t =
    match t with
    | IN | RPAREN | RBRACKET | RBRACE | ELSE | WITH | DONE | END | SEMISEMI | EOF -> true
    | _ -> false

let private isDeclKeyword t =
    match t with
    | LET | TYPE | EXCEPTION | MODULE | OPEN -> true
    | _ -> false

/// tokenize 를 감싸서 가상 토큰을 넣는 토큰 함수. input 은 탭 검사에 씀
let create (input: string) (tokenize: LexBuffer<char> -> token) : LexBuffer<char> -> token =
    let stack = Stack<Ctx>()
    let output = Queue<Tok>()
    let mutable prev: Tok option = None
    let mutable lineFirst: Tok option = None
    let mutable pending: Pending option = None
    let newCtx kind col (pos: Position) =
        { Kind = kind; Col = col; Body = false; Decl = false; BarCol = col; Pos = pos }
    stack.Push(newCtx KTop 0 Position.Empty)

    let prevCanEnd () = prev |> Option.exists (fun p -> canEnd p.Token)

    /// 가상 토큰: 위치는 지금 토큰 (parse error 가 실제 토큰을 가리키고, lexbuf 의 위치가 렉서와 어긋나지 않음)
    let emit (t: token) (at: Tok) = output.Enqueue { Token = t; Start = at.Start; End = at.End }

    let unfinishedLet (c: Ctx) = Errors.syntaxError "The block following this let is unfinished" c.Pos

    /// 맨 위 context 를 닫고 닫는 가상 토큰을 냄
    let pop (at: Tok) =
        let c = stack.Pop()
        match c.Kind with
        | KBlock _ -> emit OBLOCKEND at
        | KLoop -> emit DONE at
        | KModule true -> emit END at
        | KLet when not c.Decl -> unfinishedLet c
        | _ -> ()

    /// 위에서부터 target 을 찾음. barrier 를 먼저 만나면 None (짝이 없으므로 아무것도 닫지 않음, 파서가 오류를 냄)
    let find (target: Ctx -> bool) (barrier: Ctx -> bool) =
        stack |> Seq.tryFindIndex (fun c -> target c || barrier c) |> Option.filter (fun i -> target (Seq.item i stack))

    /// target 위의 context 를 모두 닫음 (target 은 남김)
    let popAbove (index: int) (at: Tok) =
        for _ in 1 .. index do pop at

    let isBarrier (c: Ctx) =
        match c.Kind with
        | KParen _ | KTop | KModule _ -> true
        | _ -> false

    /// 새 줄의 첫 토큰: offside 인 context 를 닫고, 같은 열이면 구분자를 넣음
    let layout (tok: Tok) =
        let t = tok.Token
        let col = tok.Start.Column
        // 줄 머리의 infix 연산자는 "연산자 길이 + 1" 만큼 왼쪽에 있어도 됨 (F# 와 같음)
        let effective = if isInfix t then col + (tok.End.AbsoluteOffset - tok.Start.AbsoluteOffset) + 1 else col
        let separate () = prevCanEnd () && canStart t
        let mutable go = true
        while go do
            let top = stack.Peek()
            match top.Kind with
            | KTop ->
                if col = 0 && not (continues t) && separate () then emit SEMISEMI tok
                go <- false
            | KParen _ ->
                if col = top.Col && not (continues t) && separate () then emit SEMI tok
                go <- false
            // struct … end 는 괄호처럼 end 만 닫음
            | KModule false ->
                if col = top.Col && not (continues t) && separate () then emit SEMISEMI tok
                go <- false
            | KMatch when t = BAR && col >= top.BarCol -> go <- false
            | _ when top.Col > effective -> pop tok
            | _ when top.Col = col && not (continues t) ->
                match top.Kind with
                | KBlock _ ->
                    if separate () then emit SEMI tok
                    go <- false
                | KModule _ ->
                    if separate () then emit SEMISEMI tok
                    go <- false
                | KLet when not top.Decl ->
                    if separate () then
                        stack.Pop() |> ignore
                        emit IN tok
                    go <- false
                // 선언 let, match, fun, while 은 끝나고 바깥 context 에서 다시 봄
                | _ -> pop tok
            | _ -> go <- false

    /// 짝을 찾아 닫는 토큰
    let close (tok: Tok) =
        let kindIs f (c: Ctx) = f c.Kind
        match tok.Token with
        | IN ->
            match find (kindIs ((=) KLet)) isBarrier with
            | Some i ->
                popAbove i tok
                stack.Pop() |> ignore
            | None -> ()
        | RPAREN | RBRACKET | RBRACE as t ->
            let isParen (c: Ctx) = match c.Kind with KParen _ -> true | _ -> false
            match find (kindIs ((=) (KParen t))) (fun c -> isParen c || c.Kind = KTop || (match c.Kind with KModule _ -> true | _ -> false)) with
            | Some i ->
                popAbove i tok
                stack.Pop() |> ignore
            | None -> ()
        | ELSE ->
            match find (kindIs ((=) (KBlock Then))) isBarrier with
            | Some i ->
                popAbove (i + 1) tok
            | None -> ()
        | WITH ->
            match find (fun c -> c.Kind = KMatch && not c.Body) isBarrier with
            | Some i ->
                popAbove i tok
                stack.Peek().Body <- true
            | None -> ()
        | BAR ->
            match find (kindIs ((=) KMatch)) isBarrier with
            | Some i -> popAbove i tok
            | None -> ()
        // , 와 타입 표기의 : 는 괄호 안의 블록을 끝냄 ((fun x -> x, 1), (fun x -> x : int -> int))
        | COMMA | COLON ->
            let isParen (c: Ctx) = match c.Kind with KParen _ -> true | _ -> false
            match find isParen (fun c -> c.Kind = KTop || (match c.Kind with KModule _ -> true | _ -> false)) with
            | Some i -> popAbove i tok
            | None -> ()
        // then, else 의 몸은 ; 를 받지 않는 식이라 같은 줄의 ; 가 끝냄 (if c then a else b; d == (if …); d)
        | SEMI ->
            let isBlock (c: Ctx) = match c.Kind with KBlock _ -> true | _ -> false
            let mutable go = true
            while go do
                match find isBlock isBarrier with
                | Some i when (match (Seq.item i stack).Kind with KBlock Then | KBlock Else -> true | _ -> false) ->
                    popAbove (i + 1) tok
                | _ -> go <- false
        | ANDKW ->
            match find (kindIs ((=) KLet)) isBarrier with
            | Some i ->
                popAbove i tok
                stack.Peek().Body <- false
            | None -> ()
        | DONE ->
            match find (kindIs ((=) KLoop)) isBarrier with
            | Some i ->
                popAbove i tok
                stack.Pop() |> ignore
            | None -> ()
        | END ->
            match find (fun c -> match c.Kind with KModule _ -> true | _ -> false) (fun c -> c.Kind = KTop) with
            | Some i ->
                popAbove i tok
                stack.Pop() |> ignore
            | None -> ()
        | SEMISEMI ->
            while (match stack.Peek().Kind with KTop | KModule _ -> false | _ -> true) do pop tok
        | EOF ->
            while stack.Peek().Kind <> KTop do pop tok
        | _ -> ()

    /// 여는 토큰: context 를 쌓거나 다음 토큰이 블록을 열게 함
    let openFor (tok: Tok) =
        let col = tok.Start.Column
        let top = stack.Peek()
        match tok.Token with
        | LET ->
            // 최상위와 모듈의 let 은 선언 (in 뒤의 let 은 식)
            let decl =
                (match top.Kind with KTop | KModule _ -> true | _ -> false)
                && not (prev |> Option.exists (fun p -> p.Token = IN))
            stack.Push { newCtx KLet col tok.Start with Decl = decl }
        | EQUALS when top.Kind = KLet && not top.Body ->
            top.Body <- true
            pending <- Some (OpenBlock LetValue)
        | EQUALS when top.Kind = KModuleHead ->
            stack.Pop() |> ignore
            pending <- Some ModuleBody
        | FUN -> stack.Push(newCtx KFun col tok.Start)
        | ARROW when top.Kind = KFun ->
            stack.Pop() |> ignore
            pending <- Some (OpenBlock Arrow)
        | ARROW when top.Kind = KMatch && top.Body -> pending <- Some (OpenBlock Arrow)
        | MATCH | TRY | FUNCTION ->
            // 줄이 | 로 시작하지 않으면 분기의 | 를 그 줄의 머리까지 왼쪽에 써도 됨
            let barCol =
                match lineFirst with
                | Some first when first.Token <> BAR -> min col first.Start.Column
                | _ -> col
            stack.Push { newCtx KMatch col tok.Start with Body = (tok.Token = FUNCTION); BarCol = barCol }
            if tok.Token = TRY then pending <- Some (OpenBlock Try)
        | THEN -> pending <- Some (OpenBlock Then)
        | ELSE -> pending <- Some (OpenBlock Else)
        | WHILE | FOR -> stack.Push(newCtx KLoop col tok.Start)
        | DO when top.Kind = KLoop -> pending <- Some (OpenBlock Do)
        | LPAREN | LBRACKET | LBRACE ->
            let closer =
                match tok.Token with
                | LPAREN -> RPAREN
                | LBRACKET -> RBRACKET
                | _ -> RBRACE
            let c = newCtx (KParen closer) col tok.Start
            stack.Push c
            pending <- Some (FirstInside c)
        | MODULE -> stack.Push(newCtx KModuleHead col tok.Start)
        | STRUCT ->
            let c = newCtx (KModule false) col tok.Start
            stack.Push c
            pending <- Some (FirstInside c)
        | _ -> ()

    let processToken (tok: Tok) =
        let t = tok.Token
        let newLine =
            match prev with
            | Some p -> tok.Start.Line > p.End.Line
            | None -> true
        if newLine && t <> EOF then
            lineFirst <- Some tok
            // 들여쓰기의 탭은 열을 셀 수 없으므로 오류 (F# 와 같음)
            let lineStart = tok.Start.AbsoluteOffset - tok.Start.Column
            if lineStart >= 0 && lineStart + tok.Start.Column <= input.Length
               && input.Substring(lineStart, tok.Start.Column).Contains '\t' then
                Errors.syntaxError "Tabs are not allowed in indentation" tok.Start
        // 블록, 괄호, 모듈의 첫 토큰: 그 context 의 열을 정함 (offside 를 보지 않음)
        let first =
            match pending with
            | Some p ->
                pending <- None
                match p with
                | OpenBlock kind ->
                    stack.Push(newCtx (KBlock kind) tok.Start.Column tok.Start)
                    emit OBLOCKBEGIN tok
                | FirstInside c -> c.Col <- tok.Start.Column
                | ModuleBody when t = STRUCT -> ()
                | ModuleBody ->
                    emit STRUCT tok
                    stack.Push(newCtx (KModule true) tok.Start.Column tok.Start)
                true
            | None -> false
        if newLine && not first && not (closesByItself t) then layout tok
        close tok
        // 한 줄에 쓴 선언들 (let x = 1 let y = 2): 다음 선언 키워드가 앞 선언 let 의 값을 끝냄
        // (새 줄이면 layout 이 이미 처리함)
        if isDeclKeyword t && not newLine && prevCanEnd () then
            let isDeclLet (c: Ctx) = c.Kind = KLet && c.Decl
            match find isDeclLet (fun c -> isBarrier c || (c.Kind = KLet && not c.Decl)) with
            | Some i ->
                popAbove i tok
                stack.Pop() |> ignore
            | None -> ()
        openFor tok
        output.Enqueue tok
        prev <- Some tok

    fun (lexbuf: LexBuffer<char>) ->
        while output.Count = 0 do
            let t = tokenize lexbuf
            processToken { Token = t; Start = lexbuf.StartPos; End = lexbuf.EndPos }
        let tok = output.Dequeue()
        // 파서는 토큰을 받은 뒤 lexbuf 의 위치를 읽음. 미리 읽은 토큰은 없으므로 마지막 토큰의 끝이 렉서의 위치와 같음
        lexbuf.StartPos <- tok.Start
        lexbuf.EndPos <- tok.End
        // 렉서가 입력 끝에 닿으면 IsPastEndOfStream 이 켜지고, 파서는 그때부터 토큰 함수를 부르지 않고 입력이 끝났다고 봄.
        // 입력 끝 앞에 넣은 가상 토큰(블록을 닫는 것)이 남아 있으면 끔
        lexbuf.IsPastEndOfStream <- (tok.Token = EOF)
        tok.Token
