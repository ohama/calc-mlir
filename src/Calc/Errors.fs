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
