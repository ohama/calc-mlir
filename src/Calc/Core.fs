/// 컴파일러의 중간 표현 (docs/compiler-design.md 5장). 클로저 변환이 끝나서 모든 함수가 최상위에 있음
module Core

open Ast

/// 아직 컴파일러가 지원하지 않는 기능 (단계별로 줄어듦)
exception NotSupported of feature: string with
    override this.Message = "Not supported by the compiler yet: " + this.feature

/// 인라인 arith 또는 런타임 호출로 만드는 연산
type Prim =
    | PAdd
    | PSubtract
    | PMultiply
    | PDivide
    | PModulo
    | PNegate
    | PEqual
    | PLess
    | PGreater
    | PLessEqual
    | PGreaterEqual
    | PNot

type CExpr =
    | CInt of int
    | CBool of bool
    /// () (즉시값 7)
    | CUnit
    /// 문자열 상수 (전역 상수 블록이 됨)
    | CString of string
    /// 지역 변수, 함수 인자, 캡처한 변수, let rec 의 자기 자신. 어느 것인지는 함수 입구에서 정함
    | CVar of string
    | CLet of string * CExpr * CExpr
    /// && 와 || 도 CIf 로 바뀜 (단락 평가)
    | CIf of CExpr * CExpr * CExpr
    | CPrim of Prim * CExpr list
    /// 최상위 함수 funcName 과 지금 범위의 변수 captured 로 클로저를 만듦
    | CMakeClosure of funcName: string * captured: string list
    /// 클로저 간접 호출
    | CApply of CExpr * CExpr
    /// 이름이 알려진 함수에 인자를 모두 주는 호출: 최상위 함수 funcName 을 (클로저, 인자...) 로 직접 부름.
    /// 클로저는 그 함수의 바깥 클로저 (캡처한 값을 꺼내는 곳). 중간 클로저를 만들지 않음
    | CCallDirect of funcName: string * closure: CExpr * args: CExpr list
    /// let rec ... and ...: 그룹의 클로저(CMakeClosure)를 모두 만든 뒤 서로를 가리키는 칸을 채움
    | CLetRecGroup of closures: (string * CExpr) list * body: CExpr
    /// [] (즉시값)
    | CNil
    /// head :: tail (블록 [헤더, head, tail])
    | CCons of CExpr * CExpr
    /// (a, b, ...) (블록 [헤더, 원소...])
    | CTuple of CExpr list
    /// 인자가 다 모인 내장 함수 호출 (런타임 함수 calc_builtin_<이름>). 인자는 왼쪽부터 계산
    | CBuiltin of name: string * args: CExpr list
    /// 레코드 (블록 [헤더, 필드 이름들, 값...]). 필드와 값은 선언한 순서, 값은 이미 계산한 것 (Lower 가 let 으로 묶음)
    | CRecord of fieldNames: string list * values: CExpr list
    /// 레코드의 index 번째 필드 (선언한 순서, 0 부터)
    | CField of CExpr * index: int
    /// 생성자로 만든 값 (블록 [헤더, 생성자 이름, 인자]). 인자 없는 생성자는 전역 상수 블록
    | CData of ctor: string * payload: CExpr option
    /// 위에서부터 패턴과 가드가 모두 맞는 첫 분기. 없으면 Match failure. 패턴은 Ast 그대로
    | CMatch of scrutinee: CExpr * cases: (Pattern * CExpr option * CExpr) list
    /// try: 런타임의 calc_try 가 _setjmp 한 뒤 body 클로저를 () 로 부르고, raise 되면 handler 클로저를 예외 값으로 부름
    | CTry of body: CExpr * handler: CExpr

/// fun param -> body 를 최상위로 옮긴 것. 호출 규약은 (클로저, 인자...) -> 결과 (D4).
/// 클로저로 부르는 함수는 인자가 하나, 인자를 모두 받는 direct 함수(CCallDirect)는 여럿
type CFunction =
    { Name: string
      Params: string list
      /// let rec 으로 정의한 함수의 이름. 본문에서 이 이름은 클로저 자신
      Self: string option
      /// 클로저에 저장한 변수. 순서가 클로저의 칸 순서
      Captured: string list
      Body: CExpr }

type CProgram = { Functions: CFunction list; Main: CExpr }

/// 패턴이 묶는 이름
let rec patternVars (pattern: Pattern) : Set<string> =
    match pattern with
    | PVar name -> Set.singleton name
    | PCons (h, t) -> Set.union (patternVars h) (patternVars t)
    | PTuple items -> items |> List.map patternVars |> Set.unionMany
    | PCtor (_, Some p) -> patternVars p
    | POr (a, _) -> patternVars a
    | PWildcard | PInt _ | PBool _ | PString _ | PNil | PUnit | PCtor (_, None) -> Set.empty

/// 식의 자유 변수 (CMakeClosure 는 캡처하는 변수가 곧 자유 변수)
let rec freeVars (expr: CExpr) : Set<string> =
    match expr with
    | CInt _
    | CBool _
    | CUnit
    | CString _ -> Set.empty
    | CVar name -> Set.singleton name
    | CLet (name, value, body) -> Set.union (freeVars value) (Set.remove name (freeVars body))
    | CIf (c, t, e) -> Set.unionMany [ freeVars c; freeVars t; freeVars e ]
    | CPrim (_, args) -> args |> List.map freeVars |> Set.unionMany
    | CMakeClosure (_, captured) -> Set.ofList captured
    | CApply (f, a) -> Set.union (freeVars f) (freeVars a)
    | CCallDirect (_, closure, args) -> (closure :: args) |> List.map freeVars |> Set.unionMany
    | CLetRecGroup (closures, body) ->
        let names = closures |> List.map fst |> Set.ofList
        Set.difference (Set.unionMany (freeVars body :: (closures |> List.map (snd >> freeVars)))) names
    | CNil -> Set.empty
    | CCons (h, t) -> Set.union (freeVars h) (freeVars t)
    | CTuple items -> items |> List.map freeVars |> Set.unionMany
    | CRecord (_, values) -> values |> List.map freeVars |> Set.unionMany
    | CField (e, _) -> freeVars e
    | CData (_, payload) -> payload |> Option.map freeVars |> Option.defaultValue Set.empty
    | CBuiltin (_, args) -> args |> List.map freeVars |> Set.unionMany
    | CMatch (scrutinee, cases) ->
        let caseVars (pattern, guard, body) =
            let used = Set.union (guard |> Option.map freeVars |> Option.defaultValue Set.empty) (freeVars body)
            Set.difference used (patternVars pattern)
        Set.unionMany (freeVars scrutinee :: List.map caseVars cases)
    | CTry (body, handler) -> Set.union (freeVars body) (freeVars handler)
