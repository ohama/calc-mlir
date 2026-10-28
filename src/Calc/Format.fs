/// 문자열 포맷: sprintf, printf, printfn 의 포맷 문자열과 $"..." 보간 (F# 의 Printf 지정자 일부).
/// 포맷 문자열 하나가 함수 하나를 정함: sprintf "%d is %s" 는 int -> string -> string
module Format

open Ast

/// 포맷 문자열의 % 자리
type Spec =
    /// %d: int
    | SInt
    /// %s: string (따옴표 없이 그대로)
    | SString
    /// %b: bool
    | SBool
    /// %A: 아무 값 (toString 과 같은 모양: 문자열은 따옴표 없이, 나머지는 결과 출력과 같음)
    | SAny

type Piece =
    | Text of string
    | Hole of Spec

/// 포맷 문자열을 받는 함수: sprintf 는 문자열을 돌려주고, printf 와 printfn 은 출력 (printfn 은 줄바꿈까지)
let kinds = set [ "sprintf"; "printf"; "printfn" ]

/// "%d is %s%%" → [Hole SInt; Text " is "; Hole SString; Text "%"]. 잘못된 지정자는 Error 메시지
let parse (format: string) : Result<Piece list, string> =
    let pieces = ResizeArray<Piece>()
    let text = System.Text.StringBuilder()
    let flush () =
        if text.Length > 0 then
            pieces.Add(Text (text.ToString()))
            text.Clear() |> ignore
    let rec go i =
        if i >= format.Length then Ok ()
        elif format[i] <> '%' then
            text.Append(format[i]) |> ignore
            go (i + 1)
        elif i + 1 >= format.Length then Error "Missing format specifier after %"
        else
            let hole spec =
                flush ()
                pieces.Add(Hole spec)
                go (i + 2)
            match format[i + 1] with
            | 'd' -> hole SInt
            | 's' -> hole SString
            | 'b' -> hole SBool
            | 'A' -> hole SAny
            | '%' ->
                text.Append('%') |> ignore
                go (i + 2)
            | c -> Error (sprintf "Unknown format specifier: %%%c" c)
    go 0 |> Result.map (fun () -> flush (); List.ofSeq pieces)

/// 포맷 함수를 닫힌 Calc 식으로. 타입은 Infer 가 포맷에서 바로 계산하고, 인터프리터와 컴파일러는 이 식을 실행함.
///   sprintf "%d is %s" == fun $f0 -> fun $f1 -> toString $f0 + " is " + $f1
///   printfn "%d"       == fun $f0 -> println (toString $f0)
/// toString, print, println 은 내장 함수여야 하므로, 사용자 이름이 보이지 않는 범위(처음 환경)에서 씀
let expand (kind: string) (pieces: Piece list) : Expr =
    let parts, count =
        pieces
        |> List.mapFold
            (fun i piece ->
                match piece with
                | Text t -> Str t, i
                | Hole SString -> Var (sprintf "$f%d" i), i + 1
                | Hole _ -> App (Var "toString", Var (sprintf "$f%d" i)), i + 1)
            0
    let text =
        match parts with
        | [] -> Str ""
        | first :: rest -> List.fold (fun acc part -> Add (acc, part)) first rest
    let body =
        match kind with
        | "sprintf" -> text
        | "printf" -> App (Var "print", text)
        | _ -> App (Var "println", text)
    List.foldBack (fun i acc -> Lambda (sprintf "$f%d" i, acc)) [ 0 .. count - 1 ] body
