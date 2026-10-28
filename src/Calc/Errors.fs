module Errors

open FSharp.Text.Lexing

/// 렉서·파서 오류. line, column 은 1부터 셈 (column 은 UTF-16 문자 단위)
exception SyntaxError of message: string * line: int * column: int with
    override this.Message = sprintf "%s (line %d, column %d)" this.message this.line this.column

/// fsyacc 의 parse_error_rich 가 던짐. 오류 토큰의 위치와 내용은 lexbuf 에 있으므로
/// Program.parse 가 잡아서 SyntaxError 로 바꿈
exception ParseFailure

/// fslex 의 Position 은 줄과 열이 0부터 시작
let syntaxError message (pos: Position) =
    raise (SyntaxError (message, pos.Line + 1, pos.Column + 1))

/// 파서가 만든 식·패턴 노드의 시작 위치 (줄, 열; 1부터). AST 모양은 그대로 두고 노드 참조로 찾음.
/// 타입 추론이 오류와 경고의 위치를 알려 줄 때 씀. 값이 하나뿐인 노드(Nil, PNil, PWildcard)는 기록하지 않음
let private positions = System.Runtime.CompilerServices.ConditionalWeakTable<obj, int * int>()

let recordPosition (node: obj) (pos: Position) =
    positions.AddOrUpdate(node, (pos.Line + 1, pos.Column + 1))

let positionOf (node: obj) : (int * int) option =
    match positions.TryGetValue node with
    | true, p -> Some p
    | _ -> None

/// from 의 위치(있으면)를 target 에도 기록하고 target 을 돌려줌
let copyPosition (from: obj) (target: obj) =
    match positionOf from with
    | Some p -> positions.AddOrUpdate(target, p)
    | None -> ()
    target
