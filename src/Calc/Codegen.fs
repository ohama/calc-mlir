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
let kindString = 2L

let kindCons = 3L
let kindTuple = 4L
let kindData = 5L

/// [] 는 즉시값 5
let nilValue = 5L

/// () 는 즉시값 7 (하위 3비트 111)
let unitValue = 7L

let closureHeader (capturedCount: int) = kindClosure ||| (int64 (1 + capturedCount) <<< 8)

/// cons 블록 = [헤더, head, tail]
let consHeader = kindCons ||| (2L <<< 8)

/// 튜플 블록 = [헤더, 원소...]
let tupleHeader (count: int) = kindTuple ||| (int64 count <<< 8)

/// 생성자 값 블록 = [헤더, 생성자 이름(C 문자열 포인터), 인자(있으면)]
let dataHeader (hasPayload: bool) = kindData ||| ((if hasPayload then 2L else 1L) <<< 8)

/// 문자열 블록 = [헤더, 길이, UTF-16 코드 단위...]. 뒤따르는 단어 수는 길이 칸 + 코드 단위를 담는 단어
let stringHeader (length: int) = kindString ||| (int64 (1 + (2 * length + 7) / 8) <<< 8)

/// 문자열 상수를 모아 전역으로 만듦 (같은 내용은 하나만). 프로그램 하나에 하나
type private StringTable(prefix: string) =
    let names = System.Collections.Generic.Dictionary<string, string>()
    let order = ResizeArray<string>()
    let ctorNames = System.Collections.Generic.Dictionary<string, string>()
    let ctorOrder = ResizeArray<string>()
    let units = System.Collections.Generic.Dictionary<string, string * string>()
    let unitOrder = ResizeArray<string>()

    member _.NameOf(s: string) =
        match names.TryGetValue s with
        | true, name -> name
        | _ ->
            let name = sprintf "%sstr_%d" prefix names.Count
            names[s] <- name
            order.Add s
            name

    member _.Entries = order |> Seq.map (fun s -> names[s], s) |> List.ofSeq

    /// 생성자 이름 → 이름을 담은 C 문자열 전역. 같은 이름은 하나 (런타임은 이름으로 비교하고 출력함).
    /// Some 과 None 은 런타임의 전역: 선언 없이 모든 프로그램에 있는 타입이라 이름을 한 곳에 둠 (패턴 검사는 이름 포인터 비교)
    member _.CtorName(ctor: string) =
        match ctor, ctorNames.TryGetValue ctor with
        | "Some", _ -> "calc_name_some"
        | "None", _ -> "calc_name_none"
        | _, (true, name) -> name
        | _ ->
            let name = sprintf "%sctor_%d" prefix ctorNames.Count
            ctorNames[ctor] <- name
            ctorOrder.Add ctor
            name

    /// 인자 없는 생성자의 값: 전역 상수 블록 [헤더, 이름]
    member this.CtorUnit(ctor: string) =
        let nameGlobal = this.CtorName ctor
        match ctor, units.TryGetValue ctor with
        | "None", _ -> "calc_none"
        | _, (true, (name, _)) -> name
        | _ ->
            let name = sprintf "%sunit_%d" prefix units.Count
            units[ctor] <- (name, nameGlobal)
            unitOrder.Add ctor
            name

    member _.CtorEntries = ctorOrder |> Seq.map (fun c -> ctorNames[c], c) |> List.ofSeq
    member _.UnitEntries = unitOrder |> Seq.map (fun c -> units[c]) |> List.ofSeq

    /// 이 프로그램의 심볼(함수, 문자열 상수) 이름 앞에 붙임. 여러 프로그램을 한 모듈에 넣을 때 이름이 겹치지 않게
    member _.Prefix = prefix

/// 생성자 이름 전역 (C 문자열). 값이 아니라 블록의 칸에 들어가므로 정렬이 필요 없음
let private emitCtorNameGlobal (sb: StringBuilder) (name: string, ctor: string) =
    sb.AppendLine(sprintf "llvm.mlir.global internal constant @%s(\"%s\\00\") {addr_space = 0 : i32}" name ctor) |> ignore

/// 인자 없는 생성자의 값: 8바이트 정렬 전역 블록 [헤더, 이름 포인터]. 주소가 곧 값
let private emitCtorUnitGlobal (sb: StringBuilder) (name: string, nameGlobal: string) =
    let ty = "!llvm.struct<(i64, ptr)>"
    let line (text: string) = sb.AppendLine(text) |> ignore
    line (sprintf "llvm.mlir.global internal constant @%s() {addr_space = 0 : i32, alignment = 8 : i64} : %s {" name ty)
    line (sprintf "  %%0 = llvm.mlir.undef : %s" ty)
    line (sprintf "  %%h = llvm.mlir.constant(%d : i64) : i64" (dataHeader false))
    line (sprintf "  %%1 = llvm.insertvalue %%h, %%0[0] : %s" ty)
    line (sprintf "  %%n = llvm.mlir.addressof @%s : !llvm.ptr" nameGlobal)
    line (sprintf "  %%2 = llvm.insertvalue %%n, %%1[1] : %s" ty)
    line (sprintf "  llvm.return %%2 : %s" ty)
    line "}"

/// 문자열 상수 전역: 8바이트 정렬이라 주소가 곧 값 (하위 3비트 000). 상수라 GC 대상이 아님
let private emitStringGlobal (sb: StringBuilder) (name: string, s: string) =
    let units = s.ToCharArray() |> Array.map (fun c -> int16 c)
    let ty =
        if units.Length = 0 then "!llvm.struct<(i64, i64)>"
        else sprintf "!llvm.struct<(i64, i64, array<%d x i16>)>" units.Length
    let line (text: string) = sb.AppendLine(text) |> ignore
    line (sprintf "llvm.mlir.global internal constant @%s() {addr_space = 0 : i32, alignment = 8 : i64} : %s {" name ty)
    line (sprintf "  %%0 = llvm.mlir.undef : %s" ty)
    line (sprintf "  %%h = llvm.mlir.constant(%d : i64) : i64" (stringHeader units.Length))
    line (sprintf "  %%1 = llvm.insertvalue %%h, %%0[0] : %s" ty)
    line (sprintf "  %%n = llvm.mlir.constant(%d : i64) : i64" units.Length)
    line (sprintf "  %%2 = llvm.insertvalue %%n, %%1[1] : %s" ty)
    if units.Length = 0 then
        line (sprintf "  llvm.return %%2 : %s" ty)
    else
        let values = units |> Array.map string |> String.concat ", "
        line (sprintf "  %%a = llvm.mlir.constant(dense<[%s]> : tensor<%dxi16>) : !llvm.array<%d x i16>" values units.Length units.Length)
        line (sprintf "  %%3 = llvm.insertvalue %%a, %%2[2] : %s" ty)
        line (sprintf "  llvm.return %%3 : %s" ty)
    line "}"

/// 함수 하나의 본문을 만드는 동안의 상태: SSA 이름, 출력 줄, 들여쓰기(scf.if 영역)
type private FunctionBuilder(strings: StringTable) =
    let lines = StringBuilder()
    let mutable next = 0
    let mutable depth = 1

    member _.Fresh() =
        next <- next + 1
        sprintf "%%v%d" next

    member _.Emit(line: string) =
        lines.Append(String.replicate depth "  ").AppendLine(line) |> ignore

    member _.Strings = strings
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
let private emitIfOf (fb: FunctionBuilder) (ty: string) (cond: string) (thenBranch: unit -> string) (elseBranch: unit -> string) =
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = scf.if %s -> (%s) {" r cond ty)
    fb.Indent()
    fb.Emit(sprintf "scf.yield %s : %s" (thenBranch ()) ty)
    fb.Dedent()
    fb.Emit("} else {")
    fb.Indent()
    fb.Emit(sprintf "scf.yield %s : %s" (elseBranch ()) ty)
    fb.Dedent()
    fb.Emit("}")
    r

let private emitIf fb cond thenBranch elseBranch = emitIfOf fb "i64" cond thenBranch elseBranch

/// 값 count 개를 내는 scf.if. 결과 이름은 %vN#0, %vN#1, ...
let private emitIfMany (fb: FunctionBuilder) (count: int) (cond: string) (thenBranch: unit -> string list) (elseBranch: unit -> string list) =
    let r = fb.Fresh()
    let types = List.replicate count "i64" |> String.concat ", "
    fb.Emit(sprintf "%s:%d = scf.if %s -> (%s) {" r count cond types)
    fb.Indent()
    fb.Emit(sprintf "scf.yield %s : %s" (thenBranch () |> String.concat ", ") types)
    fb.Dedent()
    fb.Emit("} else {")
    fb.Indent()
    fb.Emit(sprintf "scf.yield %s : %s" (elseBranch () |> String.concat ", ") types)
    fb.Dedent()
    fb.Emit("}")
    [ for i in 0 .. count - 1 -> sprintf "%s#%d" r i ]

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

/// 전역의 주소를 i64 로
let private emitGlobalAddress (fb: FunctionBuilder) (name: string) =
    let address = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.mlir.addressof @%s : !llvm.ptr" address name)
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.ptrtoint %s : !llvm.ptr to i64" r address)
    r

/// 블록 값 v 의 index 번째 단어 (0: 헤더)
let private emitField (fb: FunctionBuilder) (v: string) (index: int) =
    let block = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.inttoptr %s : i64 to !llvm.ptr" block v)
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.load %s : !llvm.ptr -> i64" r (emitSlot fb block index))
    r

/// [헤더, 값...] 블록을 GC 로 할당하고 채움. 값은 이미 계산한 SSA 이름 (평가 순서는 부르는 쪽이 정함)
let private emitBlock (fb: FunctionBuilder) (header: int64) (values: string list) =
    let size = emitConst fb "i64" (int64 (8 * (1 + values.Length)))
    let block = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.call @calc_alloc(%s) : (i64) -> !llvm.ptr" block size)
    fb.Emit(sprintf "llvm.store %s, %s : i64, !llvm.ptr" (emitConst fb "i64" header) block)
    values |> List.iteri (fun i v -> fb.Emit(sprintf "llvm.store %s, %s : i64, !llvm.ptr" v (emitSlot fb block (1 + i))))
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.ptrtoint %s : !llvm.ptr to i64" r block)
    r

/// 어떤 값과도 일치하는 패턴 (검사 코드가 필요 없음)
let rec private irrefutable (pattern: Ast.Pattern) =
    match pattern with
    | Ast.PVar _ | Ast.PWildcard | Ast.PUnit -> true
    | Ast.PTuple items -> List.forall irrefutable items
    | Ast.POr (a, _) -> irrefutable a
    | _ -> false

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
    | CUnit -> emitConst fb "i64" unitValue
    | CString s -> emitGlobalAddress fb (fb.Strings.NameOf s)
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
    | CPrim ((PDivide | PModulo) as op, [ a; b ]) ->
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
        // % 도 같은 두 검사 (.NET 은 MinValue % -1 에서도 OverflowException, LLVM srem 은 정의되지 않은 동작)
        retagInt fb (emitOp fb (if op = PDivide then "arith.divsi" else "arith.remsi") x y "i32")
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
        fb.Emit(sprintf "%s = llvm.mlir.addressof @%s%s : !llvm.ptr" code fb.Strings.Prefix funcName)
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
    // 모든 클로저를 먼저 할당한 뒤 다른 함수를 가리키는 칸을 채움 (서로를 가리키므로 순서대로 만들 수 없음)
    | CLetRecGroup (closures, body) ->
        let groupNames = closures |> List.map fst |> Set.ofList
        let blocks =
            [ for name, closure in closures ->
                  match closure with
                  | CMakeClosure (funcName, captured) ->
                      let size = emitConst fb "i64" (int64 (8 * (2 + captured.Length)))
                      let block = fb.Fresh()
                      fb.Emit(sprintf "%s = llvm.call @calc_alloc(%s) : (i64) -> !llvm.ptr" block size)
                      fb.Emit(sprintf "llvm.store %s, %s : i64, !llvm.ptr" (emitConst fb "i64" (closureHeader captured.Length)) block)
                      let code = fb.Fresh()
                      fb.Emit(sprintf "%s = llvm.mlir.addressof @%s%s : !llvm.ptr" code fb.Strings.Prefix funcName)
                      fb.Emit(sprintf "llvm.store %s, %s : !llvm.ptr, !llvm.ptr" code (emitSlot fb block 1))
                      let value = fb.Fresh()
                      fb.Emit(sprintf "%s = llvm.ptrtoint %s : !llvm.ptr to i64" value block)
                      name, block, value, captured
                  | other -> failwithf "Codegen: let rec group expects closures, got %A" other ]
        let groupEnv = blocks |> List.fold (fun acc (name, _, value, _) -> Map.add name value acc) env
        for _, block, _, captured in blocks do
            captured
            |> List.iteri (fun i c ->
                let v = if groupNames.Contains c then groupEnv[c] else emitExpr fb env (CVar c)
                fb.Emit(sprintf "llvm.store %s, %s : i64, !llvm.ptr" v (emitSlot fb block (2 + i))))
        emitExpr fb groupEnv body
    | CNil -> emitConst fb "i64" nilValue
    // 인터프리터처럼 head 를 먼저 계산
    | CCons (h, t) ->
        let hv = emitExpr fb env h
        let tv = emitExpr fb env t
        emitBlock fb consHeader [ hv; tv ]
    | CTuple items ->
        let values = items |> List.map (emitExpr fb env)
        emitBlock fb (tupleHeader values.Length) values
    | CBuiltin (name, args) ->
        let values = args |> List.map (emitExpr fb env)
        emitCall fb ("calc_builtin_" + name) values "i64"
    | CData (ctor, None) -> emitGlobalAddress fb (fb.Strings.CtorUnit ctor)
    | CData (ctor, Some payload) ->
        let pv = emitExpr fb env payload
        emitBlock fb (dataHeader true) [ emitGlobalAddress fb (fb.Strings.CtorName ctor); pv ]
    | CMatch (scrutinee, cases) ->
        let v = emitExpr fb env scrutinee
        // 분기마다 "조건(패턴 검사, 가드) → 본문, 아니면 나머지 분기". 조건을 i1 로 먼저 만들어서 나머지 분기는 한 번만 생성
        let rec emitCases cases =
            match cases with
            | [] ->
                fb.Emit(sprintf "llvm.call @calc_match_failure(%s) : (i64) -> ()" v)
                emitConst fb "i64" 0L // 돌아오지 않음. scf.yield 할 값
            | (pattern, None, body) :: _ when irrefutable pattern ->
                // 뒤 분기에는 닿지 않음
                emitExpr fb (emitBind fb pattern v env) body
            | (pattern, guard, body) :: rest ->
                let test = emitTest fb pattern v
                let cond =
                    match guard with
                    | None -> test
                    | Some g ->
                        let guardTrue () = untagBool fb (emitExpr fb (emitBind fb pattern v env) g)
                        emitIfOf fb "i1" test guardTrue (fun () -> emitConst fb "i1" 0L)
                emitIf fb cond (fun () -> emitExpr fb (emitBind fb pattern v env) body) (fun () -> emitCases rest)
        emitCases cases

/// 패턴이 값 v 와 일치하는지 (i1). 하위 패턴은 앞이 일치할 때만 검사하고, 필드도 그때 읽음
and private emitTest (fb: FunctionBuilder) (pattern: Ast.Pattern) (v: string) : string =
    let equalsConst (c: int64) =
        let r = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i64" r v (emitConst fb "i64" c))
        r
    // 필드 index 의 하위 패턴들을 차례로: 앞이 실패하면 뒤는 검사하지 않음
    let rec fields (subpatterns: (Ast.Pattern * int) list) =
        match subpatterns |> List.filter (fun (p, _) -> not (irrefutable p)) with
        | [] -> emitConst fb "i1" 1L
        | [ p, index ] -> emitTest fb p (emitField fb v index)
        | (p, index) :: rest ->
            let c = emitTest fb p (emitField fb v index)
            emitIfOf fb "i1" c (fun () -> fields rest) (fun () -> emitConst fb "i1" 0L)
    match pattern with
    // () 는 unit 의 유일한 값이라 항상 일치
    | Ast.PVar _ | Ast.PWildcard | Ast.PUnit -> emitConst fb "i1" 1L
    | Ast.PInt n -> equalsConst (tagInt n)
    | Ast.PBool b -> equalsConst (tagBool b)
    | Ast.PNil -> equalsConst nilValue
    | Ast.PString s ->
        let eq = emitCall fb "calc_eq" [ v; emitExpr fb Map.empty (CString s) ] "i64"
        untagBool fb eq
    | Ast.PCons (h, t) ->
        let isCons = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi ne, %s, %s : i64" isCons v (emitConst fb "i64" nilValue))
        if irrefutable h && irrefutable t then isCons
        else emitIfOf fb "i1" isCons (fun () -> fields [ h, 1; t, 2 ]) (fun () -> emitConst fb "i1" 0L)
    // 타입 검사를 통과했으므로 원소 수는 항상 같음
    | Ast.PTuple items -> fields (items |> List.mapi (fun i p -> p, 1 + i))
    // 왼쪽이 맞으면 true, 아니면 오른쪽
    | Ast.POr (a, b) ->
        let c = emitTest fb a v
        emitIfOf fb "i1" c (fun () -> emitConst fb "i1" 1L) (fun () -> emitTest fb b v)
    // 타입 검사를 통과했으므로 v 는 같은 타입의 생성자 값. 이름 포인터가 같으면 같은 생성자 (이름은 모듈에 하나)
    | Ast.PCtor (ctor, argPattern) ->
        let isCtor = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i64" isCtor (emitField fb v 1) (emitGlobalAddress fb (fb.Strings.CtorName ctor)))
        match argPattern with
        | Some p when not (irrefutable p) ->
            emitIfOf fb "i1" isCtor (fun () -> emitTest fb p (emitField fb v 2)) (fun () -> emitConst fb "i1" 0L)
        | _ -> isCtor

/// 일치한 패턴이 묶는 이름을 환경에 넣음. 이름을 묶는 하위 패턴의 필드만 읽음 (인터프리터처럼 앞에서부터, 같은 이름은 뒤가 이김)
and private emitBind (fb: FunctionBuilder) (pattern: Ast.Pattern) (v: string) (env: Map<string, string>) =
    let field (env: Map<string, string>) (p: Ast.Pattern, index: int) =
        if Set.isEmpty (patternVars p) then env else emitBind fb p (emitField fb v index) env
    match pattern with
    | Ast.PVar name -> Map.add name v env
    | Ast.PCons (h, t) -> List.fold field env [ h, 1; t, 2 ]
    | Ast.PTuple items -> List.fold field env (items |> List.mapi (fun i p -> p, 1 + i))
    | Ast.PCtor (_, Some p) -> field env (p, 2)
    // 맞은 쪽(왼쪽부터)에서 이름마다 값을 꺼냄: 이름 수만큼 값을 내는 scf.if
    | Ast.POr (a, b) ->
        let names = patternVars a |> Set.toList
        if names.IsEmpty then env
        else
            let c = emitTest fb a v
            let valuesFrom p () =
                let bound = emitBind fb p v Map.empty
                names |> List.map (fun n -> bound[n])
            let results = emitIfMany fb names.Length c (valuesFrom a) (valuesFrom b)
            List.fold2 (fun acc n r -> Map.add n r acc) env names results
    | _ -> env

/// 최상위 함수 하나: 입구에서 캡처한 값을 꺼내 환경을 만들고 본문을 생성
let private emitFunction (strings: StringTable) (sb: StringBuilder) (f: CFunction) =
    let fb = FunctionBuilder(strings)
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
    // internal: 모듈 밖에서 보이지 않음. 여러 프로그램의 오브젝트를 한 실행 파일로 링크해도 이름이 겹치지 않음
    sb.AppendLine(sprintf "llvm.func internal @%s%s(%%closure: !llvm.ptr, %%arg: i64) -> i64 {" strings.Prefix f.Name) |> ignore
    sb.Append(fb.Text) |> ignore
    sb.AppendLine(sprintf "  llvm.return %s : i64" result).AppendLine("}").AppendLine() |> ignore

let private declarations =
    [ "// Calc 컴파일러가 만든 MLIR. 값은 i64 하나 (docs/compiler-design.md D3)"
      "llvm.func @calc_init()"
      "llvm.func @calc_alloc(i64) -> !llvm.ptr"
      "llvm.func @calc_print_result(i64)"
      "llvm.func @calc_error_divide_by_zero()"
      "llvm.func @calc_error_overflow()"
      "llvm.func @calc_add(i64, i64) -> i64"
      "llvm.func @calc_eq(i64, i64) -> i64"
      "llvm.func @calc_compare(i64, i64) -> i32"
      "llvm.func @calc_match_failure(i64)"
      // 내장 함수: runtime/builtins.c
      for KeyValue (name, arity) in Eval.builtinArity do
          sprintf "llvm.func @calc_builtin_%s(%s) -> i64" name (List.replicate arity "i64" |> String.concat ", ")
      // option 의 이름과 None 값은 런타임에 하나 (runtime.h)
      "llvm.mlir.global external constant @calc_name_some() {addr_space = 0 : i32} : !llvm.array<5 x i8>"
      "llvm.mlir.global external constant @calc_name_none() {addr_space = 0 : i32} : !llvm.array<5 x i8>"
      "llvm.mlir.global external constant @calc_none() {addr_space = 0 : i32} : !llvm.struct<(i64, ptr)>"
      "" ]
/// 프로그램 하나: 문자열 상수, 클로저마다 최상위 함수, 식 하나를 계산하는 진입 함수 @entry. 심볼 이름에는 prefix 를 붙임
let private emitBody (sb: StringBuilder) (prefix: string) (entry: string) (expr: Ast.Expr) =
    let program = Lower.lowerProgram expr
    let strings = StringTable(prefix)
    let fb = FunctionBuilder(strings)
    let result = emitExpr fb Map.empty program.Main
    // 함수 본문을 먼저 만들어야 그 안의 문자열 상수도 모임
    let functions = StringBuilder()
    program.Functions |> List.iter (emitFunction strings functions)
    let line (s: string) = sb.AppendLine(s) |> ignore
    strings.Entries |> List.iter (emitStringGlobal sb)
    strings.CtorEntries |> List.iter (emitCtorNameGlobal sb)
    strings.UnitEntries |> List.iter (emitCtorUnitGlobal sb)
    if not (strings.Entries.IsEmpty && strings.CtorEntries.IsEmpty) then line ""
    sb.Append(functions) |> ignore
    line (sprintf "llvm.func @%s() -> i64 {" entry)
    sb.Append(fb.Text) |> ignore
    line (sprintf "  llvm.return %s : i64" result)
    line "}"
    line ""

/// 실행 파일 하나가 될 프로그램: @calc_main 과 결과를 출력하는 @main
let emitProgram (expr: Ast.Expr) : string =
    let sb = StringBuilder()
    let line (s: string) = sb.AppendLine(s) |> ignore
    declarations |> List.iter line
    emitBody sb "" "calc_main" expr
    line "llvm.func @main() -> i32 {"
    line "  llvm.call @calc_init() : () -> ()"
    line "  %result = llvm.call @calc_main() : () -> i64"
    line "  llvm.call @calc_print_result(%result) : (i64) -> ()"
    line "  %ok = arith.constant 0 : i32"
    line "  llvm.return %ok : i32"
    line "}"
    sb.ToString()

/// 여러 프로그램을 모듈 하나에: 프로그램마다 진입 함수 @entry 와 그 프로그램만의 심볼 접두어 (entry + "_").
/// @main 은 없음 (Toolchain.buildMany 가 만듦). 테스트에서 외부 도구와 실행 파일 수를 줄이려고 씀
let emitEntries (programs: (string * Ast.Expr) list) : string =
    let sb = StringBuilder()
    declarations |> List.iter (fun s -> sb.AppendLine(s) |> ignore)
    for entry, expr in programs do
        emitBody sb (entry + "_") entry expr
    sb.ToString()
