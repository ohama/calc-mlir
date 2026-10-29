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

// 3 은 예전 cons 블록의 종류. 지금 cons 는 헤더가 없음 (consTag)
let kindTuple = 4L
let kindData = 5L
let kindRef = 6L
let kindRecord = 7L

/// [] 는 즉시값 5
let nilValue = 5L

/// () 는 즉시값 7 (하위 3비트 111)
let unitValue = 7L

let closureHeader (capturedCount: int) = kindClosure ||| (int64 (1 + capturedCount) <<< 8)

/// cons 값 = 헤더 없는 [head, tail] 16바이트 블록의 주소 | 2 (runtime.h 의 CALC_TAG_CONS)
let consTag = 2L

/// 튜플 블록 = [헤더, 원소...]
let tupleHeader (count: int) = kindTuple ||| (int64 count <<< 8)

/// 생성자 값 블록 = [헤더, 생성자 이름(C 문자열 포인터), 인자(있으면)]
let dataHeader (hasPayload: bool) = kindData ||| ((if hasPayload then 2L else 1L) <<< 8)
/// 인자가 튜플이면 원소를 블록에 바로 넣음: [헤더, 이름, 원소...] (블록 하나). 단어 수가 2 면 튜플이 아닌 인자 하나
let flatDataHeader (fields: int) = kindData ||| (int64 (1 + fields) <<< 8)

/// ref 블록 = [헤더, 내용]. := 가 내용 칸을 바꿈
let refHeader = kindRef ||| (1L <<< 8)

/// 레코드 블록 = [헤더, 필드 이름들("x;y" C 문자열), 값...]. 필드 이름은 결과 출력에만 씀
let recordHeader (count: int) = kindRecord ||| (int64 (1 + count) <<< 8)

/// 런타임 함수 없이 Codegen 이 바로 만드는 내장 함수 (가변 참조)
let private inlineBuiltins = set [ "ref"; "!"; ":=" ]

/// 내장 함수의 C 런타임 함수 이름 (builtins.c). 이름에 C 에서 쓸 수 없는 글자가 있는 것(배열)은 따로 정함
let private builtinSymbol (name: string) =
    match name with
    | "[||]" | "Array.ofList" -> "calc_builtin_Array_ofList" // 배열 리터럴은 리스트로 만든 배열
    | ".[]" -> "calc_builtin_array_get"
    | ".[]<-" -> "calc_builtin_array_set"
    | _ -> "calc_builtin_" + name.Replace(".", "_")

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

/// 함수 하나의 본문을 만드는 동안의 상태: SSA 이름, 블록 이름, 출력 줄, 들여쓰기(scf.if 영역)
type private FunctionBuilder(strings: StringTable) =
    let lines = StringBuilder()
    let mutable next = 0
    let mutable nextBlock = 0
    let mutable depth = 1

    member _.Fresh() =
        next <- next + 1
        sprintf "%%v%d" next

    /// 함수 영역의 새 블록 이름 (tail position 의 분기, emitTail)
    member _.FreshBlock() =
        nextBlock <- nextBlock + 1
        sprintf "^bb%d" nextBlock

    member _.Emit(line: string) =
        lines.Append(String.replicate depth "  ").AppendLine(line) |> ignore

    /// 블록 시작. 이 뒤의 줄은 이 블록에 들어감
    member _.Label(block: string) =
        lines.Append(block).AppendLine(":") |> ignore

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

/// cons 값 v 의 index 번째 단어 (0: head, 1: tail). 블록 주소는 v - 2
let private emitConsField (fb: FunctionBuilder) (v: string) (index: int) =
    let block = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.inttoptr %s : i64 to !llvm.ptr" block v)
    let address = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.getelementptr %s[%d] : (!llvm.ptr) -> !llvm.ptr, i8" address block (8 * index - int consTag))
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.load %s : !llvm.ptr -> i64" r address)
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

/// 배열 a 의 i 번째 원소의 주소. 범위 밖이면 calc_error_index (인터프리터와 같은 문장)
let private emitElementAddress (fb: FunctionBuilder) (a: string) (i: string) =
    let index = fb.Fresh()
    fb.Emit(sprintf "%s = arith.extsi %s : i32 to i64" index (untagInt fb i))
    let size = emitOp fb "arith.shrui" (emitField fb a 0) (emitConst fb "i64" 8L) "i64"
    // 음수는 부호 없는 수로 보면 size 보다 큼
    let outside = fb.Fresh()
    fb.Emit(sprintf "%s = arith.cmpi uge, %s, %s : i64" outside index size)
    emitErrorIf fb outside "calc_error_index"
    let block = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.inttoptr %s : i64 to !llvm.ptr" block a)
    let slot = emitOp fb "arith.addi" index (emitConst fb "i64" 1L) "i64"
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.getelementptr %s[%s] : (!llvm.ptr, i64) -> !llvm.ptr, i64" r block slot)
    r

/// 값 v 가 튜플 블록인가 (하위 3비트가 0 이고 헤더의 종류가 튜플)
let private emitIsTuple (fb: FunctionBuilder) (v: string) =
    let tag = emitOp fb "arith.andi" v (emitConst fb "i64" 7L) "i64"
    let isBlock = fb.Fresh()
    fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i64" isBlock tag (emitConst fb "i64" 0L))
    emitIfOf fb "i1" isBlock
        (fun () ->
            let kind = emitOp fb "arith.andi" (emitField fb v 0) (emitConst fb "i64" 0xffL) "i64"
            let c = fb.Fresh()
            fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i64" c kind (emitConst fb "i64" kindTuple))
            c)
        (fun () -> emitConst fb "i1" 0L)

/// 생성자 값 v 의 인자 하나: 단어 수가 2 면 칸 2, 아니면 원소들로 튜플을 다시 만듦
let private emitPayload (fb: FunctionBuilder) (v: string) =
    let words = emitOp fb "arith.shrui" (emitField fb v 0) (emitConst fb "i64" 8L) "i64"
    let single = fb.Fresh()
    fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i64" single words (emitConst fb "i64" 2L))
    emitIfOf fb "i64" single (fun () -> emitField fb v 2) (fun () -> emitCall fb "calc_data_payload" [ v ] "i64")

/// 어떤 값과도 일치하는 패턴 (검사 코드가 필요 없음)
let rec private irrefutable (pattern: Ast.Pattern) =
    match pattern with
    | Ast.PVar _ | Ast.PWildcard | Ast.PUnit -> true
    | Ast.PTuple items -> List.forall irrefutable items
    | Ast.PRecord fields -> fields |> List.forall (snd >> irrefutable)
    | Ast.POr (a, _) -> irrefutable a
    | _ -> false

/// match 의 분기가 모두 원소 n 개인 튜플 패턴이나 _ 인가
let private tupleArms (n: int) (cases: (Ast.Pattern * CExpr option * CExpr) list) =
    cases |> List.forall (fun (p, _, _) -> match p with Ast.PTuple ps -> ps.Length = n | Ast.PWildcard -> true | _ -> false)

/// 분기의 패턴과 원소 값의 짝 (_ 는 원소마다 _)
let private components (pattern: Ast.Pattern) (vs: string list) =
    match pattern with
    | Ast.PTuple ps -> List.zip ps vs
    | _ -> vs |> List.map (fun v -> Ast.PWildcard, v)

/// 식의 값을 담은 SSA 이름을 반환. env 는 변수 이름 → 그 값을 담은 SSA 이름
let rec private emitExpr (fb: FunctionBuilder) (env: Map<string, string>) expr : string =
    // 정수 연산: 태그를 떼고 32비트에서 계산(한 바퀴 돎, 인터프리터와 같음)한 뒤 다시 태그
    let intOp op a b =
        let x = untagInt fb (emitExpr fb env a)
        let y = untagInt fb (emitExpr fb env b)
        retagInt fb (emitOp fb op x y "i32")
    // 태그를 떼지 않고 더하기, 빼기: (8x+1) ± (8y+1) ∓ 1 = 8(x±y)+1 을 64비트에서 계산한 뒤, 35비트에서 부호를 넓혀
    // 32비트에서 한 바퀴 돈 값으로 (shl 29, ashr 29: arm64 에서 sbfx 하나)
    let taggedSum op fix x y =
        let t = emitOp fb fix (emitOp fb op x y "i64") (emitConst fb "i64" 1L) "i64"
        let shifted = emitOp fb "arith.shli" t (emitConst fb "i64" 29L) "i64"
        emitOp fb "arith.shrsi" shifted (emitConst fb "i64" 29L) "i64"
    let taggedAddSub op fix a b =
        let x = emitExpr fb env a
        let y = emitExpr fb env b
        taggedSum op fix x y
    // x 가 int 인가 (타입 검사를 통과했으므로 그러면 다른 쪽도 int)
    let isInt x =
        let tag = emitOp fb "arith.andi" x (emitConst fb "i64" 7L) "i64"
        let c = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i64" c tag (emitConst fb "i64" 1L))
        c
    // 비교: int 끼리는 바로 (태그를 붙인 채로 비교해도 순서가 같음), 나머지(string)는 런타임. 결과는 -1, 0, 1
    let compare predicate a b =
        let x = emitExpr fb env a
        let y = emitExpr fb env b
        emitIf fb (isInt x)
            (fun () ->
                let cmp = fb.Fresh()
                fb.Emit(sprintf "%s = arith.cmpi %s, %s, %s : i64" cmp predicate x y)
                retagBool fb cmp)
            (fun () ->
                let c = emitCall fb "calc_compare" [ x; y ] "i32"
                let cmp = fb.Fresh()
                fb.Emit(sprintf "%s = arith.cmpi %s, %s, %s : i32" cmp predicate c (emitConst fb "i32" 0L))
                retagBool fb cmp)
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
        emitIf fb (isInt x)
            (fun () -> taggedSum "arith.addi" "arith.subi" x y)
            (fun () -> emitCall fb "calc_add" [ x; y ] "i64")
    | CPrim (PSubtract, [ a; b ]) -> taggedAddSub "arith.subi" "arith.addi" a b
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
        // int, bool, [], () (태그가 홀수) 는 값 자체를 비교. 블록과 cons(태그 0, 2)는 런타임이 내용을 비교
        let odd = emitOp fb "arith.andi" x (emitConst fb "i64" 1L) "i64"
        let immediate = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi ne, %s, %s : i64" immediate odd (emitConst fb "i64" 0L))
        emitIf fb immediate
            (fun () ->
                let eq = fb.Fresh()
                fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i64" eq x y)
                retagBool fb eq)
            (fun () -> emitCall fb "calc_eq" [ x; y ] "i64")
    // 타입 추론이 int 로 정한 연산: 태그 검사 없이 (태그를 붙인 채로 비교해도 순서와 같음이 그대로)
    | CPrim (PIntAdd, [ a; b ]) -> taggedAddSub "arith.addi" "arith.subi" a b
    | CPrim ((PIntEqual | PIntLess | PIntGreater | PIntLessEqual | PIntGreaterEqual) as op, [ a; b ]) ->
        let x = emitExpr fb env a
        let y = emitExpr fb env b
        let predicate =
            match op with
            | PIntEqual -> "eq"
            | PIntLess -> "slt"
            | PIntGreater -> "sgt"
            | PIntLessEqual -> "sle"
            | _ -> "sge"
        let cmp = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi %s, %s, %s : i64" cmp predicate x y)
        retagBool fb cmp
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
    | CApply (f, a) -> emitApply fb env "" f a
    | CCallDirect (funcName, closure, args) -> emitCallDirect fb env "" funcName closure args
    // 런타임의 calc_try 가 _setjmp 한 뒤 본문 클로저를 부름 (setjmp 를 한 함수가 살아 있는 동안만 longjmp 할 수 있으므로 C 에서)
    | CTry (body, handler) ->
        let b = emitExpr fb env body
        let h = emitExpr fb env handler
        emitCall fb "calc_try" [ b; h ] "i64"
    | CLetRecGroup (closures, body) -> emitExpr fb (emitRecGroup fb env closures) body
    | CNil -> emitConst fb "i64" nilValue
    // 인터프리터처럼 head 를 먼저 계산
    | CCons (h, t) ->
        let hv = emitExpr fb env h
        let tv = emitExpr fb env t
        let block = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.call @calc_alloc(%s) : (i64) -> !llvm.ptr" block (emitConst fb "i64" 16L))
        fb.Emit(sprintf "llvm.store %s, %s : i64, !llvm.ptr" hv block)
        fb.Emit(sprintf "llvm.store %s, %s : i64, !llvm.ptr" tv (emitSlot fb block 1))
        let address = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.ptrtoint %s : !llvm.ptr to i64" address block)
        emitOp fb "arith.ori" address (emitConst fb "i64" consTag) "i64"
    | CTuple items ->
        let values = items |> List.map (emitExpr fb env)
        emitBlock fb (tupleHeader values.Length) values
    // 가변 참조: 블록 하나를 만들고, 1번 칸을 읽고 씀
    | CBuiltin ("ref", [ v ]) -> emitBlock fb refHeader [ emitExpr fb env v ]
    | CBuiltin ("!", [ r ]) ->
        let block = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.inttoptr %s : i64 to !llvm.ptr" block (emitExpr fb env r))
        let v = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.load %s {tbaa = [#calc_tbaa_ref]} : !llvm.ptr -> i64" v (emitSlot fb block 1))
        v
    // 배열: 범위를 확인하고 바로 읽고 씀 ([헤더, 원소...], 원소 수는 헤더의 단어 수). 인자는 왼쪽부터
    | CBuiltin (".[]", [ a; i ]) ->
        let av = emitExpr fb env a
        let iv = emitExpr fb env i
        let r = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.load %s {tbaa = [#calc_tbaa_array]} : !llvm.ptr -> i64" r (emitElementAddress fb av iv))
        r
    | CBuiltin (".[]<-", [ a; i; v ]) ->
        let av = emitExpr fb env a
        let iv = emitExpr fb env i
        let vv = emitExpr fb env v
        fb.Emit(sprintf "llvm.store %s, %s {tbaa = [#calc_tbaa_array]} : i64, !llvm.ptr" vv (emitElementAddress fb av iv))
        emitConst fb "i64" unitValue
    | CBuiltin (":=", [ r; v ]) ->
        let rv = emitExpr fb env r
        let vv = emitExpr fb env v
        let block = fb.Fresh()
        fb.Emit(sprintf "%s = llvm.inttoptr %s : i64 to !llvm.ptr" block rv)
        fb.Emit(sprintf "llvm.store %s, %s {tbaa = [#calc_tbaa_ref]} : i64, !llvm.ptr" vv (emitSlot fb block 1))
        emitConst fb "i64" unitValue
    // append (reverse xs) ys: 거꾸로 만든 리스트를 다시 복사하지 않고 xs 의 원소를 ys 앞에 하나씩 붙임 (칸 n 개)
    | CBuiltin ("append", [ CBuiltin ("reverse", [ xs ]); ys ]) ->
        let xv = emitExpr fb env xs
        let yv = emitExpr fb env ys
        emitCall fb "calc_rev_append" [ xv; yv ] "i64"
    | CBuiltin (name, args) ->
        let values = args |> List.map (emitExpr fb env)
        emitCall fb (builtinSymbol name) values "i64"
    // 필드 이름 문자열은 생성자 이름과 같은 표에 둠 (필드 이름은 소문자로 시작하고 ; 가 들어가므로 생성자와 겹치지 않음)
    | CRecord (names, values) ->
        let namesAddress = emitGlobalAddress fb (fb.Strings.CtorName (String.concat ";" names))
        emitBlock fb (recordHeader values.Length) (namesAddress :: List.map (emitExpr fb env) values)
    | CField (e, index) -> emitField fb (emitExpr fb env e) (2 + index)
    | CData (ctor, None) -> emitGlobalAddress fb (fb.Strings.CtorUnit ctor)
    | CData (ctor, Some (CTuple items)) ->
        let values = items |> List.map (emitExpr fb env)
        emitBlock fb (flatDataHeader values.Length) (emitGlobalAddress fb (fb.Strings.CtorName ctor) :: values)
    // 인자가 실행 중에 튜플이면 (생성자를 함수 값으로 쓸 때 등) 런타임이 원소를 펴서 넣음
    | CData (ctor, Some payload) ->
        let pv = emitExpr fb env payload
        let name = emitGlobalAddress fb (fb.Strings.CtorName ctor)
        emitIf fb (emitIsTuple fb pv)
            (fun () -> emitCall fb "calc_make_data" [ name; pv ] "i64")
            (fun () -> emitBlock fb (dataHeader true) [ name; pv ])
    // match (a, b) with (p, q) -> …: 튜플 블록을 만들지 않고 원소마다 패턴을 맞춤 (분기가 모두 같은 길이의 튜플 패턴이나 _)
    | CMatch (CTuple items, cases) when tupleArms items.Length cases ->
        let vs = items |> List.map (emitExpr fb env)
        let rec emitCases cases =
            match cases with
            | [] ->
                fb.Emit(sprintf "llvm.call @calc_match_failure(%s) : (i64) -> ()" (emitBlock fb (tupleHeader vs.Length) vs))
                emitConst fb "i64" 0L
            | (pattern, None, body) :: _ when irrefutable pattern -> emitExpr fb (emitBindAll fb (components pattern vs) env) body
            | (pattern, guard, body) :: rest ->
                let cond = emitComponentsCondition fb env (components pattern vs) guard
                emitIf fb cond (fun () -> emitExpr fb (emitBindAll fb (components pattern vs) env) body) (fun () -> emitCases rest)
        emitCases cases
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
                let cond = emitCaseCondition fb env pattern guard v
                emitIf fb cond (fun () -> emitExpr fb (emitBind fb pattern v env) body) (fun () -> emitCases rest)
        emitCases cases

/// f a: 클로저의 1번 칸에서 코드 포인터를 꺼내 (클로저, 인자) 로 간접 호출. tailKind 는 "" 또는 "tail "
and private emitApply (fb: FunctionBuilder) (env: Map<string, string>) (tailKind: string) (f: CExpr) (a: CExpr) =
    let fv = emitExpr fb env f
    let av = emitExpr fb env a
    let block = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.inttoptr %s : i64 to !llvm.ptr" block fv)
    let code = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.load %s : !llvm.ptr -> !llvm.ptr" code (emitSlot fb block 1))
    let r = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.call tailcc %s%s(%s, %s) : !llvm.ptr, (!llvm.ptr, i64) -> i64" r tailKind code block av)
    r

/// 알려진 함수 호출: 최상위 함수를 (클로저, 인자...) 로 직접 부름. 클로저와 인자는 왼쪽부터 계산
and private emitCallDirect (fb: FunctionBuilder) (env: Map<string, string>) (tailKind: string) (funcName: string) (closure: CExpr) (args: CExpr list) =
    let cv = emitExpr fb env closure
    let values = args |> List.map (emitExpr fb env)
    let block = fb.Fresh()
    fb.Emit(sprintf "%s = llvm.inttoptr %s : i64 to !llvm.ptr" block cv)
    let r = fb.Fresh()
    let types = List.replicate values.Length "i64" |> String.concat ", "
    fb.Emit(
        sprintf "%s = llvm.call tailcc %s@%s%s(%s) : (!llvm.ptr, %s) -> i64"
            r tailKind fb.Strings.Prefix funcName (String.concat ", " (block :: values)) types)
    r

/// match 분기의 조건 (i1): 패턴 검사, 가드가 있으면 패턴이 맞을 때만 가드를 계산
and private emitCaseCondition (fb: FunctionBuilder) (env: Map<string, string>) (pattern: Ast.Pattern) (guard: CExpr option) (v: string) =
    let test = emitTest fb pattern v
    match guard with
    | None -> test
    | Some g ->
        let guardTrue () = untagBool fb (emitExpr fb (emitBind fb pattern v env) g)
        emitIfOf fb "i1" test guardTrue (fun () -> emitConst fb "i1" 0L)

/// 원소별로 맞추는 튜플 match 의 분기 조건: 원소의 패턴을 왼쪽부터 (앞이 실패하면 뒤는 검사하지 않음), 그다음 가드
and private emitComponentsCondition (fb: FunctionBuilder) (env: Map<string, string>) (pairs: (Ast.Pattern * string) list) (guard: CExpr option) =
    let rec testAll pairs =
        match pairs |> List.filter (fun (p, _) -> not (irrefutable p)) with
        | [] -> emitConst fb "i1" 1L
        | [ p, v ] -> emitTest fb p v
        | (p, v) :: rest -> emitIfOf fb "i1" (emitTest fb p v) (fun () -> testAll rest) (fun () -> emitConst fb "i1" 0L)
    let test = testAll pairs
    match guard with
    | None -> test
    | Some g ->
        let guardTrue () = untagBool fb (emitExpr fb (emitBindAll fb pairs env) g)
        emitIfOf fb "i1" test guardTrue (fun () -> emitConst fb "i1" 0L)

and private emitBindAll (fb: FunctionBuilder) (pairs: (Ast.Pattern * string) list) (env: Map<string, string>) =
    pairs |> List.fold (fun env (p, v) -> emitBind fb p v env) env

/// let rec ... and ...: 모든 클로저를 먼저 할당한 뒤 다른 함수를 가리키는 칸을 채움 (서로를 가리키므로 순서대로 만들 수 없음).
/// 그룹의 이름을 더한 환경을 돌려줌
and private emitRecGroup (fb: FunctionBuilder) (env: Map<string, string>) (closures: (string * CExpr) list) =
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
    groupEnv

/// 패턴이 값 v 와 일치하는지 (i1). 하위 패턴은 앞이 일치할 때만 검사하고, 필드도 그때 읽음
and private emitTest (fb: FunctionBuilder) (pattern: Ast.Pattern) (v: string) : string =
    let equalsConst (c: int64) =
        let r = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i64" r v (emitConst fb "i64" c))
        r
    // 필드 index 의 하위 패턴들을 차례로: 앞이 실패하면 뒤는 검사하지 않음. read 는 index 번째 필드를 읽는 코드
    let rec fieldsBy (read: int -> string) (subpatterns: (Ast.Pattern * int) list) =
        match subpatterns |> List.filter (fun (p, _) -> not (irrefutable p)) with
        | [] -> emitConst fb "i1" 1L
        | [ p, index ] -> emitTest fb p (read index)
        | (p, index) :: rest ->
            let c = emitTest fb p (read index)
            emitIfOf fb "i1" c (fun () -> fieldsBy read rest) (fun () -> emitConst fb "i1" 0L)
    let fields = fieldsBy (emitField fb v)
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
        else emitIfOf fb "i1" isCons (fun () -> fieldsBy (emitConsField fb v) [ h, 0; t, 1 ]) (fun () -> emitConst fb "i1" 0L)
    // 타입 검사를 통과했으므로 원소 수는 항상 같음
    | Ast.PTuple items -> fields (items |> List.mapi (fun i p -> p, 1 + i))
    // Lower 가 선언한 순서의 모든 필드로 채웠으므로 i 번째가 칸 2 + i (0, 1 은 헤더와 필드 이름)
    | Ast.PRecord items -> fields (items |> List.mapi (fun i (_, p) -> p, 2 + i))
    // 왼쪽이 맞으면 true, 아니면 오른쪽
    | Ast.POr (a, b) ->
        let c = emitTest fb a v
        emitIfOf fb "i1" c (fun () -> emitConst fb "i1" 1L) (fun () -> emitTest fb b v)
    // 타입 검사를 통과했으므로 v 는 같은 타입의 생성자 값. 이름 포인터가 같으면 같은 생성자 (이름은 모듈에 하나)
    | Ast.PCtor (ctor, argPattern) ->
        let isCtor = fb.Fresh()
        fb.Emit(sprintf "%s = arith.cmpi eq, %s, %s : i64" isCtor (emitField fb v 1) (emitGlobalAddress fb (fb.Strings.CtorName ctor)))
        match argPattern with
        // 튜플 인자는 블록에 펴져 있음: i 번째 원소가 칸 2 + i
        | Some (Ast.PTuple items) when not (irrefutable (Ast.PTuple items)) ->
            emitIfOf fb "i1" isCtor (fun () -> fields (items |> List.mapi (fun i p -> p, 2 + i))) (fun () -> emitConst fb "i1" 0L)
        | Some p when not (irrefutable p) ->
            emitIfOf fb "i1" isCtor (fun () -> emitTest fb p (emitPayload fb v)) (fun () -> emitConst fb "i1" 0L)
        | _ -> isCtor

/// 일치한 패턴이 묶는 이름을 환경에 넣음. 이름을 묶는 하위 패턴의 필드만 읽음 (인터프리터처럼 앞에서부터, 같은 이름은 뒤가 이김)
and private emitBind (fb: FunctionBuilder) (pattern: Ast.Pattern) (v: string) (env: Map<string, string>) =
    let fieldBy (read: int -> string) (env: Map<string, string>) (p: Ast.Pattern, index: int) =
        if Set.isEmpty (patternVars p) then env else emitBind fb p (read index) env
    let field = fieldBy (emitField fb v)
    match pattern with
    | Ast.PVar name -> Map.add name v env
    | Ast.PCons (h, t) -> List.fold (fieldBy (emitConsField fb v)) env [ h, 0; t, 1 ]
    | Ast.PTuple items -> List.fold field env (items |> List.mapi (fun i p -> p, 1 + i))
    | Ast.PRecord items -> List.fold field env (items |> List.mapi (fun i (_, p) -> p, 2 + i))
    | Ast.PCtor (_, Some (Ast.PTuple items)) -> List.fold field env (items |> List.mapi (fun i p -> p, 2 + i))
    | Ast.PCtor (_, Some p) -> if Set.isEmpty (patternVars p) then env else emitBind fb p (emitPayload fb v) env
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

/// 함수 본문의 tail position: 값을 llvm.return 으로 돌려줌. 끝이 호출이면 tail 호출 바로 뒤에 llvm.return
/// (호출한 함수의 스택 프레임을 다시 씀. 함수가 모두 tailcc 라서 인자 수가 달라도 최적화 수준과 상관없이 LLVM 이 보장).
/// 그래서 tail position 의 if 와 match 는 scf.if 가 아니라 블록과 llvm.cond_br: scf.if 안의 호출 뒤에는
/// 결과를 모으는 분기가 있어서 tail call 이 될 수 없음. tail position 이 아닌 곳은 emitExpr (scf.if)
and private emitTail (fb: FunctionBuilder) (env: Map<string, string>) (expr: CExpr) : unit =
    let branch (cond: string) (thenBranch: unit -> unit) (elseBranch: unit -> unit) =
        let thenBlock = fb.FreshBlock()
        let elseBlock = fb.FreshBlock()
        fb.Emit(sprintf "llvm.cond_br %s, %s, %s" cond thenBlock elseBlock)
        fb.Label thenBlock
        thenBranch ()
        fb.Label elseBlock
        elseBranch ()
    match expr with
    | CLet (name, value, body) -> emitTail fb (Map.add name (emitExpr fb env value) env) body
    | CLetRecGroup (closures, body) -> emitTail fb (emitRecGroup fb env closures) body
    | CIf (cond, thenExpr, elseExpr) ->
        let c = untagBool fb (emitExpr fb env cond)
        branch c (fun () -> emitTail fb env thenExpr) (fun () -> emitTail fb env elseExpr)
    | CApply (f, a) -> fb.Emit(sprintf "llvm.return %s : i64" (emitApply fb env "tail " f a))
    | CCallDirect (funcName, closure, args) -> fb.Emit(sprintf "llvm.return %s : i64" (emitCallDirect fb env "tail " funcName closure args))
    | CMatch (CTuple items, cases) when tupleArms items.Length cases ->
        let vs = items |> List.map (emitExpr fb env)
        let rec emitCases cases =
            match cases with
            | [] ->
                fb.Emit(sprintf "llvm.call @calc_match_failure(%s) : (i64) -> ()" (emitBlock fb (tupleHeader vs.Length) vs))
                fb.Emit "llvm.unreachable"
            | (pattern, None, body) :: _ when irrefutable pattern -> emitTail fb (emitBindAll fb (components pattern vs) env) body
            | (pattern, guard, body) :: rest ->
                let cond = emitComponentsCondition fb env (components pattern vs) guard
                branch cond (fun () -> emitTail fb (emitBindAll fb (components pattern vs) env) body) (fun () -> emitCases rest)
        emitCases cases
    | CMatch (scrutinee, cases) ->
        let v = emitExpr fb env scrutinee
        let rec emitCases cases =
            match cases with
            | [] ->
                fb.Emit(sprintf "llvm.call @calc_match_failure(%s) : (i64) -> ()" v)
                fb.Emit "llvm.unreachable"
            | (pattern, None, body) :: _ when irrefutable pattern -> emitTail fb (emitBind fb pattern v env) body
            | (pattern, guard, body) :: rest ->
                let cond = emitCaseCondition fb env pattern guard v
                branch cond (fun () -> emitTail fb (emitBind fb pattern v env) body) (fun () -> emitCases rest)
        emitCases cases
    | _ -> fb.Emit(sprintf "llvm.return %s : i64" (emitExpr fb env expr))

/// 최상위 함수 하나: 입구에서 캡처한 값을 꺼내 환경을 만들고 본문을 생성
let private emitFunction (strings: StringTable) (sb: StringBuilder) (f: CFunction) =
    let fb = FunctionBuilder(strings)
    let captured =
        f.Captured
        |> List.mapi (fun i name ->
            let v = fb.Fresh()
            fb.Emit(sprintf "%s = llvm.load %s {tbaa = [#calc_tbaa_captured]} : !llvm.ptr -> i64" v (emitSlot fb "%closure" (2 + i)))
            name, v)
    let self =
        match f.Self with
        | Some name ->
            let v = fb.Fresh()
            fb.Emit(sprintf "%s = llvm.ptrtoint %%closure : !llvm.ptr to i64" v)
            [ name, v ]
        | None -> []
    // 클로저로 부르는 함수는 %arg, direct 함수는 %arg0, %arg1, ...
    let parameters =
        match f.Params with
        | [ p ] -> [ p, "%arg" ]
        | ps -> ps |> List.mapi (fun i p -> p, sprintf "%%arg%d" i)
    // 인자가 가장 나중 (뒤 인자가 앞 인자를 가림): let rec f f = ... 이면 본문의 f 는 인자 (인터프리터와 같음)
    let env = Map.ofList (captured @ self @ parameters)
    // 본문 끝(들)은 llvm.return. tail position 의 호출은 tail call
    emitTail fb env f.Body
    // internal: 모듈 밖에서 보이지 않음. 여러 프로그램의 오브젝트를 한 실행 파일로 링크해도 이름이 겹치지 않음.
    // tailcc: tail 호출을 인자 수가 다른 함수 사이에서도 보장하는 calling convention. C 는 calc_apply 를 거쳐 부름
    let signature = parameters |> List.map (fun (_, v) -> v + ": i64") |> String.concat ", "
    sb.AppendLine(sprintf "llvm.func internal tailcc @%s%s(%%closure: !llvm.ptr, %s) -> i64 {" strings.Prefix f.Name signature) |> ignore
    sb.Append(fb.Text) |> ignore
    sb.AppendLine("}").AppendLine() |> ignore

let private declarations =
    [ "// Calc 컴파일러가 만든 MLIR. 값은 i64 하나 (docs/compiler-design.md D3)"
      // TBAA: 클로저가 붙잡은 값(만든 뒤 바뀌지 않음), ref 의 내용, 배열 원소는 서로 다른 블록이라 겹치지 않음.
      // 이것을 알려야 LLVM 이 ref 나 배열에 쓰는 반복 안에서 클로저 칸 읽기를 반복 밖으로 뺌 (sieve 의 세는 반복)
      "#calc_tbaa_root = #llvm.tbaa_root<id = \"calc\">"
      "#calc_tbaa_captured_type = #llvm.tbaa_type_desc<id = \"captured\", members = {<#calc_tbaa_root, 0>}>"
      "#calc_tbaa_ref_type = #llvm.tbaa_type_desc<id = \"ref\", members = {<#calc_tbaa_root, 0>}>"
      "#calc_tbaa_array_type = #llvm.tbaa_type_desc<id = \"array\", members = {<#calc_tbaa_root, 0>}>"
      "#calc_tbaa_captured = #llvm.tbaa_tag<base_type = #calc_tbaa_captured_type, access_type = #calc_tbaa_captured_type, offset = 0>"
      "#calc_tbaa_ref = #llvm.tbaa_tag<base_type = #calc_tbaa_ref_type, access_type = #calc_tbaa_ref_type, offset = 0>"
      "#calc_tbaa_array = #llvm.tbaa_tag<base_type = #calc_tbaa_array_type, access_type = #calc_tbaa_array_type, offset = 0>"
      "llvm.func @calc_init()"
      "llvm.func @calc_alloc(i64) -> !llvm.ptr"
      "llvm.func @calc_print_result(i64)"
      // 오류 함수는 exit 로 끝나므로 noreturn: 반복 안의 범위 검사 실패 호출이 메모리를 바꾼다고 보지 않게 함
      "llvm.func @calc_error_divide_by_zero() attributes {passthrough = [\"noreturn\", \"nounwind\"]}"
      "llvm.func @calc_error_index() attributes {passthrough = [\"noreturn\", \"nounwind\"]}"
      "llvm.func @calc_make_data(i64, i64) -> i64"
      "llvm.func @calc_rev_append(i64, i64) -> i64"
      "llvm.func @calc_data_payload(i64) -> i64"
      "llvm.func @calc_error_overflow() attributes {passthrough = [\"noreturn\", \"nounwind\"]}"
      "llvm.func @calc_add(i64, i64) -> i64"
      "llvm.func @calc_eq(i64, i64) -> i64"
      "llvm.func @calc_compare(i64, i64) -> i32"
      "llvm.func @calc_match_failure(i64) attributes {passthrough = [\"noreturn\", \"nounwind\"]}"
      "llvm.func @calc_try(i64, i64) -> i64"
      // 런타임(C)이 클로저를 부르는 함수 (calc_try). 클로저의 함수는 tailcc 라 C 에서 바로 부를 수 없으므로 여기서 정의.
      // weak_odr: 모듈 여럿(테스트의 buildMany)을 한 실행 파일로 링크해도 하나만 남음 (linkonce_odr 는 모듈에서 쓰지 않으면 버려짐)
      "llvm.func weak_odr @calc_apply(%f: i64, %a: i64) -> i64 {"
      "  %block = llvm.inttoptr %f : i64 to !llvm.ptr"
      "  %slot = llvm.getelementptr %block[1] : (!llvm.ptr) -> !llvm.ptr, i64"
      "  %code = llvm.load %slot : !llvm.ptr -> !llvm.ptr"
      "  %r = llvm.call tailcc %code(%block, %a) : !llvm.ptr, (!llvm.ptr, i64) -> i64"
      "  llvm.return %r : i64"
      "}"
      // 내장 함수: runtime/builtins.c
      // sprintf, printf, printfn 은 Lower 가 Calc 코드로 펼치고, ref, !, := 는 Codegen 이 바로 만드므로 런타임 함수가 없음
      // 같은 C 함수를 쓰는 내장 함수([||] 와 Array.ofList)는 한 번만 선언
      for symbol, arity in
          Eval.builtinArity
          |> Seq.filter (fun (KeyValue (name, _)) -> not (Format.kinds.Contains name || inlineBuiltins.Contains name))
          |> Seq.map (fun (KeyValue (name, arity)) -> builtinSymbol name, arity)
          |> Seq.distinct do
          sprintf "llvm.func @%s(%s) -> i64" symbol (List.replicate arity "i64" |> String.concat ", ")
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
