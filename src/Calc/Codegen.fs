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

/// 함수 하나의 본문을 만드는 동안의 상태: SSA 이름, 출력 줄, 들여쓰기(scf.if 영역)
type private FunctionBuilder() =
    let lines = StringBuilder()
    let mutable next = 0
    let mutable depth = 1

    member _.Fresh() =
        next <- next + 1
        sprintf "%%v%d" next

    member _.Emit(line: string) =
        lines.Append(String.replicate depth "  ").AppendLine(line) |> ignore

    member _.Indent() = depth <- depth + 1
    member _.Dedent() = depth <- depth - 1
    member _.Text = lines.ToString()

let private emitConst (fb: FunctionBuilder) (ty: string) (n: int64) =
    let v = fb.Fresh()
    fb.Emit(sprintf "%s = arith.constant %d : %s" v n ty)
    v

let private emitOp (fb: FunctionBuilder) (op: string) (a: string) (b: string) (ty: string) =
    let v = fb.Fresh()
    fb.Emit(sprintf "%s = %s %s, %s : %s" v op a b ty)
    v

/// 태그가 붙은 int → i32 (산술 시프트로 부호 유지)
let private untagInt (fb: FunctionBuilder) (v: string) =
    let shifted = emitOp fb "arith.shrsi" v (emitConst fb "i64" 3L) "i64"
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = arith.trunci %s : i64 to i32" r shifted)
    r

/// i32 → 태그가 붙은 int
let private retagInt (fb: FunctionBuilder) (x: string) =
    let wide = fb.Fresh()
    fb.Emit(sprintf "%s = arith.extsi %s : i32 to i64" wide x)
    let shifted = emitOp fb "arith.shli" wide (emitConst fb "i64" 3L) "i64"
    emitOp fb "arith.ori" shifted (emitConst fb "i64" 1L) "i64"

/// 태그가 붙은 bool → i1
let private untagBool (fb: FunctionBuilder) (v: string) =
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i64" r v (emitConst fb "i64" (tagBool true)))
    r

/// i1 → 태그가 붙은 bool
let private retagBool (fb: FunctionBuilder) (c: string) =
    let t = emitConst fb "i64" (tagBool true)
    let f = emitConst fb "i64" (tagBool false)
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = arith.select %s, %s, %s : i64" r c t f)
    r

/// 값을 내는 scf.if. 각 분기는 영역 안에서 만들고 결과를 scf.yield
let private emitIf (fb: FunctionBuilder) (cond: string) (thenBranch: unit -> string) (elseBranch: unit -> string) =
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = scf.if %s -> (i64) {" r cond)
    fb.Indent()
    fb.Emit(sprintf "scf.yield %s : i64" (thenBranch ()))
    fb.Dedent()
    fb.Emit("} else {")
    fb.Indent()
    fb.Emit(sprintf "scf.yield %s : i64" (elseBranch ()))
    fb.Dedent()
    fb.Emit("}")
    r

/// cond 가 참이면 런타임 오류 함수를 부름 (돌아오지 않음)
let private emitErrorIf (fb: FunctionBuilder) (cond: string) (runtimeFunction: string) =
    fb.Emit(sprintf "scf.if %s {" cond)
    fb.Indent()
    fb.Emit(sprintf "llvm.call @%s() : () -> ()" runtimeFunction)
    fb.Dedent()
    fb.Emit("}")

let private emitCall (fb: FunctionBuilder) (name: string) (args: string list) (resultType: string) =
    let r = fb.Fresh()
    let argTypes = args |> List.map (fun _ -> "i64") |> String.concat ", "
    fb.Emit(sprintf "%s = llvm.call @%s(%s) : (%s) -> %s" r name (String.concat ", " args) argTypes resultType)
    r

/// 식의 값을 담은 SSA 이름을 반환
let rec private emitExpr (fb: FunctionBuilder) expr : string =
    // 정수 연산: 태그를 떼고 32비트에서 계산(한 바퀴 돎, 인터프리터와 같음)한 뒤 다시 태그
    let intOp op a b =
        let x = untagInt fb (emitExpr fb a)
        let y = untagInt fb (emitExpr fb b)
        retagInt fb (emitOp fb op x y "i32")
    // 비교: 런타임이 int 와 (나중에) string 을 고름. 결과는 -1, 0, 1
    let compare predicate a b =
        let x = emitExpr fb a
        let y = emitExpr fb b
        let c = emitCall fb "calc_compare" [ x; y ] "i32"
        let cmp = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi %s, %s, %s : i32" cmp predicate c (emitConst fb "i32" 0L))
        retagBool fb cmp
    match expr with
    | Number n -> emitConst fb "i64" (tagInt n)
    | Bool b -> emitConst fb "i64" (tagBool b)
    // + 는 int 덧셈 또는 string 연결이라 런타임이 태그로 고름 (D6)
    | Add (a, b) ->
        let x = emitExpr fb a
        let y = emitExpr fb b
        emitCall fb "calc_add" [ x; y ] "i64"
    | Subtract (a, b) -> intOp "arith.subi" a b
    | Multiply (a, b) -> intOp "arith.muli" a b
    | Divide (a, b) ->
        let x = untagInt fb (emitExpr fb a)
        let y = untagInt fb (emitExpr fb b)
        let isZero = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i32" isZero y (emitConst fb "i32" 0L))
        emitErrorIf fb isZero "calc_error_divide_by_zero"
        // Int32.MinValue / -1 은 결과가 범위 밖 (.NET 은 OverflowException, LLVM 은 정의되지 않은 동작)
        let isMin = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i32" isMin x (emitConst fb "i32" (int64 System.Int32.MinValue)))
        let isMinusOne = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i32" isMinusOne y (emitConst fb "i32" -1L))
        emitErrorIf fb (emitOp fb "arith.andi" isMin isMinusOne "i1") "calc_error_overflow"
        retagInt fb (emitOp fb "arith.divsi" x y "i32")
    | Negate e ->
        let x = untagInt fb (emitExpr fb e)
        retagInt fb (emitOp fb "arith.subi" (emitConst fb "i32" 0L) x "i32")
    // = 은 구조적 비교라 런타임이 함 (결과는 태그가 붙은 bool)
    | Equal (a, b) ->
        let x = emitExpr fb a
        let y = emitExpr fb b
        emitCall fb "calc_eq" [ x; y ] "i64"
    | NotEqual (a, b) -> emitExpr fb (Not (Equal (a, b)))
    | Less (a, b) -> compare "slt" a b
    | Greater (a, b) -> compare "sgt" a b
    | LessEqual (a, b) -> compare "sle" a b
    | GreaterEqual (a, b) -> compare "sge" a b
    // 단락 평가: 오른쪽은 scf.if 영역 안에서만 계산
    | And (a, b) ->
        let c = untagBool fb (emitExpr fb a)
        emitIf fb c (fun () -> emitExpr fb b) (fun () -> emitConst fb "i64" (tagBool false))
    | Or (a, b) ->
        let c = untagBool fb (emitExpr fb a)
        emitIf fb c (fun () -> emitConst fb "i64" (tagBool true)) (fun () -> emitExpr fb b)
    // true(11) 와 false(3) 는 3번 비트만 다름
    | Not e -> emitOp fb "arith.xori" (emitExpr fb e) (emitConst fb "i64" 8L) "i64"
    | If (cond, thenExpr, elseExpr) ->
        let c = untagBool fb (emitExpr fb cond)
        emitIf fb c (fun () -> emitExpr fb thenExpr) (fun () -> emitExpr fb elseExpr)
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
    line "llvm.func @calc_error_divide_by_zero()"
    line "llvm.func @calc_error_overflow()"
    line "llvm.func @calc_add(i64, i64) -> i64"
    line "llvm.func @calc_eq(i64, i64) -> i64"
    line "llvm.func @calc_compare(i64, i64) -> i32"
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
