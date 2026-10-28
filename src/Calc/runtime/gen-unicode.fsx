// runtime/unicode.h 를 만드는 스크립트: .NET 의 대소문자 변환(ToUpperInvariant, ToLowerInvariant)과 공백(Char.IsWhiteSpace)을 C 표로.
// 런타임의 upper, lower, trim 이 인터프리터와 같은 결과를 내도록 함. 실행: dotnet fsi src/Calc/runtime/gen-unicode.fsx
// CompileTests 의 "Unicode 표" 테스트가 이 표와 지금 .NET 이 같은지 확인함
open System
open System.IO

/// 코드 포인트마다 바뀌는 것만 (원래, 바뀐) 쌍으로. 길이가 바뀌는 변환은 없음 (생성할 때 확인)
let mapping (convert: string -> string) =
    [ for cp in 0 .. 0x10FFFF do
          if cp < 0xD800 || cp > 0xDFFF then
              let s = Char.ConvertFromUtf32 cp
              let t = convert s
              if t.Length <> s.Length then failwithf "length changes: U+%04X" cp
              if t <> s then yield cp, Char.ConvertToUtf32(t, 0) ]

let whitespace = [ for c in 0 .. 0xFFFF do if Char.IsWhiteSpace(char c) then yield c ]

let table (name: string) (pairs: (int * int) list) =
    [ yield sprintf "static const uint32_t %s[%d][2] = {" name pairs.Length
      for chunk in List.chunkBySize 6 pairs do
          yield "    " + (chunk |> List.map (fun (a, b) -> sprintf "{0x%04X, 0x%04X}," a b) |> String.concat " ")
      yield "};" ]

let lines =
    [ yield "/* gen-unicode.fsx 가 만든 파일. 직접 고치지 말 것 */"
      yield sprintf "/* .NET %s: ToUpperInvariant, ToLowerInvariant (코드 포인트마다, 길이 불변), Char.IsWhiteSpace */" (Environment.Version.ToString())
      yield "#ifndef CALC_UNICODE_H"
      yield "#define CALC_UNICODE_H"
      yield "#include <stdint.h>"
      yield! table "calc_upper_map" (mapping (fun s -> s.ToUpperInvariant()))
      yield! table "calc_lower_map" (mapping (fun s -> s.ToLowerInvariant()))
      yield sprintf "static const uint16_t calc_whitespace[%d] = {" whitespace.Length
      yield "    " + (whitespace |> List.map (sprintf "0x%04X,") |> String.concat " ")
      yield "};"
      yield "#endif" ]

File.WriteAllLines(Path.Combine(__SOURCE_DIRECTORY__, "unicode.h"), lines)
printfn "unicode.h: %d lines" lines.Length
