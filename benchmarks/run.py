"""Python, Calc 인터프리터, Calc 컴파일러의 출력과 실행 시간을 비교한다.

    dotnet build src/Calc -c Release
    python3 benchmarks/run.py            # 5번씩
    python3 benchmarks/run.py 10         # 10번씩
    python3 benchmarks/run.py --large 3  # 큰 입력판(benchmarks/large/), 3번씩
    python3 benchmarks/run.py --large --compiled-only 3   # 실행 파일만 (Python, 인터프리터는 건너뜀)

CALC 환경 변수로 calc 실행 파일을 바꿀 수 있다. 시간은 프로세스 시작을 포함한 wall time 이고,
한 번 데운 뒤 잰 값의 중앙값이다. 컴파일한 실행 파일은 임시 디렉터리에 만든다.
"""
import os, statistics, subprocess, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
# CALC 는 실행한 곳 기준의 경로 (프로그램은 benchmarks/ 안에서 실행하므로 절대 경로로 바꿔 둠)
CALC = os.path.abspath(os.environ.get("CALC", os.path.join(ROOT, "src/Calc/bin/Release/net10.0/calc")))
PROGRAMS = ["fib", "queens", "sieve", "msort", "bst"]
ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]
LARGE = "--large" in sys.argv
COMPILED_ONLY = "--compiled-only" in sys.argv
N = int(ARGS[0]) if ARGS else 5
DIR = os.path.join(HERE, "large") if LARGE else HERE


def run(cmd):
    start = time.perf_counter()
    p = subprocess.run(cmd, capture_output=True, text=True, cwd=DIR)
    elapsed = time.perf_counter() - start
    if p.returncode != 0:
        sys.exit(f"failed: {' '.join(cmd)}\n{p.stderr}")
    return elapsed, p.stdout.strip()


def bench(cmd):
    run(cmd)  # 한 번 데우기 (디스크 캐시, macOS 의 새 실행 파일 검사)
    results = [run(cmd) for _ in range(N)]
    outputs = {out for _, out in results}
    if len(outputs) != 1:
        sys.exit(f"output changed between runs: {' '.join(cmd)}")
    return statistics.median(t for t, _ in results), outputs.pop()


def main():
    if not os.path.exists(CALC):
        sys.exit(f"calc not found: {CALC}\nrun: dotnet build src/Calc -c Release")
    print(f"{N} runs each, median seconds\n")
    print("| 예제 | 출력 | Python | 인터프리터 | 실행 파일 | 컴파일 시간 |")
    print("|---|---|---|---|---|---|")
    with tempfile.TemporaryDirectory() as tmp:
        for name in PROGRAMS:
            exe = os.path.join(tmp, name)
            compile_time, _ = run([CALC, f"--compile={exe}", f"{name}.calc"])
            ex_time, ex_out = bench([exe])
            if COMPILED_ONLY:
                py_time = it_time = float("nan")
                py_out = it_out = ex_out
            else:
                py_time, py_out = bench(["python3", f"{name}.py"])
                it_time, it_out = bench([CALC, f"{name}.calc"])
            if not py_out == it_out == ex_out:
                sys.exit(f"{name}: outputs differ\n  python: {py_out}\n  interpreter: {it_out}\n  compiled: {ex_out}")
            out = py_out.replace("\n", ", ")
            print(f"| {name} | `{out}` | {py_time:.3f} | {it_time:.3f} | {ex_time:.3f} | {compile_time:.2f} |", flush=True)

        if LARGE or COMPILED_ONLY:
            return
        # 아무것도 하지 않는 프로그램: 시작 비용
        empty = os.path.join(tmp, "empty.calc")
        with open(empty, "w") as f:
            f.write("()\n")
        empty_exe = os.path.join(tmp, "empty")
        run([CALC, f"--compile={empty_exe}", empty])
        print(f"| (시작 비용) | | {bench(['python3', '-c', 'pass'])[0]:.3f} "
              f"| {bench([CALC, empty])[0]:.3f} | {bench([empty_exe])[0]:.3f} | |")


main()
