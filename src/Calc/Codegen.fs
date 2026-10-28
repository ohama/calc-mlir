/// Ast.Expr → MLIR 텍스트. 함수는 모두 llvm.func, 본문은 arith/scf/llvm 방언 (docs/compiler-design.md D1, D2)
module Codegen

open System.Text
open Microsoft.FSharp.Reflection
open Ast

/// 아직 컴파일러가 지원하지 않는 기능 (단계별로 줄어듦)
exception NotSupported of feature: string with
    override this.Message = "Not supported by the compiler yet: " + this.feature

// ---- 값 표현 (D3): 모든 값은 i64, 하위 3비트가 종류 ----

/// int n 은 (n << 3) | 1
let tagInt (n: int) = (int64 n <<< 3) ||| 1L

/// bool b 는 (b << 3) | 3
let tagBool (b: bool) = if b then (1L <<< 3) ||| 3L else 3L

/// 식의 종류 이름 (NotSupported 메시지용): Add, Lambda, ...
let private caseName (expr: Expr) =
    let case, _ = FSharpValue.GetUnionFields(expr, typeof<Expr>)
    case.Name

/// 함수 하나의 본문을 만드는 동안의 상태: SSA 이름과 출력 줄
type private FunctionBuilder() =
    let lines = StringBuilder()
    let mutable next = 0

    member _.Fresh() =
        next <- next + 1
        sprintf "%%v%d" next

    member _.Emit(line: string) = lines.Append("  ").AppendLine(line) |> ignore
    member _.Text = lines.ToString()

/// 식의 값을 담은 SSA 이름을 반환
let rec private emitExpr (fb: FunctionBuilder) expr : string =
    let constant (n: int64) =
        let v = fb.Fresh()
        fb.Emit(sprintf "%s = arith.constant %d : i64" v n)
        v
    match expr with
    | Number n -> constant (tagInt n)
    | Bool b -> constant (tagBool b)
    | other -> raise (NotSupported (caseName other))

/// 프로그램 전체: 식 하나를 계산하는 @calc_main 과, 결과를 출력하는 @main
let emitProgram (expr: Expr) : string =
    let fb = FunctionBuilder()
    let result = emitExpr fb expr
    let sb = StringBuilder()
    let line (s: string) = sb.AppendLine(s) |> ignore
    line "// Calc 컴파일러가 만든 MLIR. 값은 i64 하나 (docs/compiler-design.md D3)"
    line "llvm.func @calc_init()"
    line "llvm.func @calc_print_result(i64)"
    line ""
    line "llvm.func @calc_main() -> i64 {"
    sb.Append(fb.Text) |> ignore
    line (sprintf "  llvm.return %s : i64" result)
    line "}"
    line ""
    line "llvm.func @main() -> i32 {"
    line "  llvm.call @calc_init() : () -> ()"
    line "  %result = llvm.call @calc_main() : () -> i64"
    line "  llvm.call @calc_print_result(%result) : (i64) -> ()"
    line "  %ok = arith.constant 0 : i32"
    line "  llvm.return %ok : i32"
    line "}"
    sb.ToString()
