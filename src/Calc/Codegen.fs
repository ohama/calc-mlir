/// Core → MLIR 텍스트. 함수는 모두 llvm.func, 본문은 arith/scf/llvm 방언 (docs/compiler-design.md D1, D2)
module Codegen

open System.Text
open Core

// ---- 값 표현 (D3): 모든 값은 i64, 하위 3비트가 종류 ----

/// int n 은 (n << 3) | 1
let tagInt (n: int) = (int64 n <<< 3) ||| 1L

/// bool b 는 (b << 3) | 3
let tagBool (b: bool) = if b then (1L <<< 3) ||| 3L else 3L

/// 힙 블록의 첫 단어: 종류 | (뒤따르는 단어 수 << 8). runtime.h 의 CALC_KIND_* 와 같음
let kindClosure = 1L

let closureHeader (capturedCount: int) = kindClosure ||| (int64 (1 + capturedCount) <<< 8)

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

/// 클로저 블록의 index 번째 단어 주소 (0: 헤더, 1: 코드 포인터, 2~: 캡처한 값)
let private emitSlot (fb: FunctionBuilder) (block: string) (index: int) =
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.getelementptr %s[%d] : (!llvm.ptr) -> !llvm.ptr, i64" r block index)
    r

/// 식의 값을 담은 SSA 이름을 반환. env 는 변수 이름 → 그 값을 담은 SSA 이름
let rec private emitExpr (fb: FunctionBuilder) (env: Map<string, string>) expr : string =
    // 정수 연산: 태그를 떼고 32비트에서 계산(한 바퀴 돎, 인터프리터와 같음)한 뒤 다시 태그
    let intOp op a b =
        let x = untagInt fb (emitExpr fb env a)
        let y = untagInt fb (emitExpr fb env b)
        retagInt fb (emitOp fb op x y "i32")
    // 비교: 런타임이 int 와 (나중에) string 을 고름. 결과는 -1, 0, 1
    let compare predicate a b =
        let x = emitExpr fb env a
        let y = emitExpr fb env b
        let c = emitCall fb "calc_compare" [ x; y ] "i32"
        let cmp = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi %s, %s, %s : i32" cmp predicate c (emitConst fb "i32" 0L))
        retagBool fb cmp
    match expr with
    | CInt n -> emitConst fb "i64" (tagInt n)
    | CBool b -> emitConst fb "i64" (tagBool b)
    // let 은 새 코드가 없음: 값의 SSA 이름을 환경에 넣을 뿐. 섀도잉은 Map.add 가 덮어씀
    | CLet (name, value, body) ->
        let v = emitExpr fb env value
        emitExpr fb (Map.add name v env) body
    | CVar name ->
        match Map.tryFind name env with
        | Some v -> v
        // 타입 검사를 통과했으므로 환경에 없는 이름은 내장 함수 (단계 7)
        | None -> raise (NotSupported ("built-in function " + name))
    // + 는 int 덧셈 또는 string 연결이라 런타임이 태그로 고름 (D6)
    | CPrim (PAdd, [ a; b ]) ->
        let x = emitExpr fb env a
        let y = emitExpr fb env b
        emitCall fb "calc_add" [ x; y ] "i64"
    | CPrim (PSubtract, [ a; b ]) -> intOp "arith.subi" a b
    | CPrim (PMultiply, [ a; b ]) -> intOp "arith.muli" a b
    | CPrim (PDivide, [ a; b ]) ->
        let x = untagInt fb (emitExpr fb env a)
        let y = untagInt fb (emitExpr fb env b)
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
    | CPrim (PNegate, [ e ]) ->
        let x = untagInt fb (emitExpr fb env e)
        retagInt fb (emitOp fb "arith.subi" (emitConst fb "i32" 0L) x "i32")
    // = 은 구조적 비교라 런타임이 함 (결과는 태그가 붙은 bool)
    | CPrim (PEqual, [ a; b ]) ->
        let x = emitExpr fb env a
        let y = emitExpr fb env b
        emitCall fb "calc_eq" [ x; y ] "i64"
    | CPrim (PLess, [ a; b ]) -> compare "slt" a b
    | CPrim (PGreater, [ a; b ]) -> compare "sgt" a b
    | CPrim (PLessEqual, [ a; b ]) -> compare "sle" a b
    | CPrim (PGreaterEqual, [ a; b ]) -> compare "sge" a b
    // true(11) 와 false(3) 는 3번 비트만 다름
    | CPrim (PNot, [ e ]) -> emitOp fb "arith.xori" (emitExpr fb env e) (emitConst fb "i64" 8L) "i64"
    | CPrim (p, args) -> failwithf "Codegen: %A with %d arguments" p args.Length
    | CIf (cond, thenExpr, elseExpr) ->
        let c = untagBool fb (emitExpr fb env cond)
        emitIf fb c (fun () -> emitExpr fb env thenExpr) (fun () -> emitExpr fb env elseExpr)
    // 클로저 블록 = [헤더, 코드 포인터, 캡처한 값...]. GC 가 관리하는 메모리에 할당
    | CMakeClosure (funcName, captured) ->
        let values = captured |> List.map (fun name -> emitExpr fb env (CVar name))
        let size = emitConst fb "i64" (int64 (8 * (2 + captured.Length)))
        let block = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.call @calc_alloc(%s) : (i64) -> !llvm.ptr" block size)
        fb.Emit(sprintf "llvm.store %s, %s : i64, !llvm.ptr" (emitConst fb "i64" (closureHeader captured.Length)) block)
        let code = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.mlir.addressof @%s : !llvm.ptr" code funcName)
        fb.Emit(sprintf "llvm.store %s, %s : !llvm.ptr, !llvm.ptr" code (emitSlot fb block 1))
        values |> List.iteri (fun i v -> fb.Emit(sprintf "llvm.store %s, %s : i64, !llvm.ptr" v (emitSlot fb block (2 + i))))
        let r = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.ptrtoint %s : !llvm.ptr to i64" r block)
        r
    // f a: 클로저의 1번 칸에서 코드 포인터를 꺼내 (클로저, 인자) 로 간접 호출
    | CApply (f, a) ->
        let fv = emitExpr fb env f
        let av = emitExpr fb env a
        let block = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.inttoptr %s : i64 to !llvm.ptr" block fv)
        let code = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.load %s : !llvm.ptr -> !llvm.ptr" code (emitSlot fb block 1))
        let r = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.call %s(%s, %s) : !llvm.ptr, (!llvm.ptr, i64) -> i64" r code block av)
        r

/// 최상위 함수 하나: 입구에서 캡처한 값을 꺼내 환경을 만들고 본문을 생성
let private emitFunction (sb: StringBuilder) (f: CFunction) =
    let fb = FunctionBuilder()
    let captured =
        f.Captured
        |> List.mapi (fun i name ->
            let v = fb.Fresh()
            fb.Emit(sprintf "%s = llvm.load %s : !llvm.ptr -> i64" v (emitSlot fb "%closure" (2 + i)))
            name, v)
    let self =
        match f.Self with
        | Some name ->
            let v = fb.Fresh()
            fb.Emit(sprintf "%s = llvm.ptrtoint %%closure : !llvm.ptr to i64" v)
            [ name, v ]
        | None -> []
    // 인자가 가장 나중: let rec f f = ... 이면 본문의 f 는 인자 (인터프리터와 같음)
    let env = Map.ofList (captured @ self @ [ f.Param, "%arg" ])
    let result = emitExpr fb env f.Body
    sb.AppendLine(sprintf "llvm.func @%s(%%closure: !llvm.ptr, %%arg: i64) -> i64 {" f.Name) |> ignore
    sb.Append(fb.Text) |> ignore
    sb.AppendLine(sprintf "  llvm.return %s : i64" result).AppendLine("}").AppendLine() |> ignore

/// 프로그램 전체: 클로저마다 최상위 함수, 식 하나를 계산하는 @calc_main, 결과를 출력하는 @main
let emitProgram (expr: Ast.Expr) : string =
    let program = Lower.lowerProgram expr
    let fb = FunctionBuilder()
    let result = emitExpr fb Map.empty program.Main
    let sb = StringBuilder()
    let line (s: string) = sb.AppendLine(s) |> ignore
    line "// Calc 컴파일러가 만든 MLIR. 값은 i64 하나 (docs/compiler-design.md D3)"
    line "llvm.func @calc_init()"
    line "llvm.func @calc_alloc(i64) -> !llvm.ptr"
    line "llvm.func @calc_print_result(i64)"
    line "llvm.func @calc_error_divide_by_zero()"
    line "llvm.func @calc_error_overflow()"
    line "llvm.func @calc_add(i64, i64) -> i64"
    line "llvm.func @calc_eq(i64, i64) -> i64"
    line "llvm.func @calc_compare(i64, i64) -> i32"
    line ""
    program.Functions |> List.iter (emitFunction sb)
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
