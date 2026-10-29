# benchmarks

같은 일을 하는 Calc 프로그램(`.calc`)과 Python 프로그램(`.py`) 다섯 쌍, 그리고 측정 스크립트입니다.

```bash
dotnet build src/Calc -c Release
python3 benchmarks/run.py

# ReadyToRun 으로 publish 한 calc 로 재기 (docs/build.md 의 "빠른 실행 파일")
dotnet publish src/Calc -c Release -r osx-arm64 -p:PublishReadyToRun=true -o dist
CALC=dist/calc python3 benchmarks/run.py
```

예제 설명, 결과, 분석은 [docs/benchmarks.md](../docs/benchmarks.md)에 있습니다.
