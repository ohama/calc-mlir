/// 컴파일러의 중간 표현 (docs/compiler-design.md 5장). 클로저 변환이 끝나서 모든 함수가 최상위에 있음
module Core

/// 아직 컴파일러가 지원하지 않는 기능 (단계별로 줄어듦)
exception NotSupported of feature: string with
    override this.Message = "Not supported by the compiler yet: " + this.feature

/// 인라인 arith 또는 런타임 호출로 만드는 연산
type Prim =
    | PAdd
    | PSubtract
    | PMultiply
    | PDivide
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

/// fun param -> body 를 최상위로 옮긴 것. 호출 규약은 (클로저, 인자) -> 결과 (D4)
type CFunction =
    { Name: string
      Param: string
      /// let rec 으로 정의한 함수의 이름. 본문에서 이 이름은 클로저 자신
      Self: string option
      /// 클로저에 저장한 변수. 순서가 클로저의 칸 순서
      Captured: string list
      Body: CExpr }

type CProgram = { Functions: CFunction list; Main: CExpr }

/// 식의 자유 변수 (CMakeClosure 는 캡처하는 변수가 곧 자유 변수)
let rec freeVars (expr: CExpr) : Set<string> =
    match expr with
    | CInt _
    | CBool _
    | CString _ -> Set.empty
    | CVar name -> Set.singleton name
    | CLet (name, value, body) -> Set.union (freeVars value) (Set.remove name (freeVars body))
    | CIf (c, t, e) -> Set.unionMany [ freeVars c; freeVars t; freeVars e ]
    | CPrim (_, args) -> args |> List.map freeVars |> Set.unionMany
    | CMakeClosure (_, captured) -> Set.ofList captured
    | CApply (f, a) -> Set.union (freeVars f) (freeVars a)
