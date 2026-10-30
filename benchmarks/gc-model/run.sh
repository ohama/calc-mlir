#!/bin/sh
# 세대 구분 GC 모델(gcm.c)과 지금 런타임과 같은 Boehm 설정을 같은 C 모델로 비교 (docs/generational-gc-design.md 11장).
# 결과가 Boehm 과 같은지 확인하고, 5번 잰 중앙값(초, 최대 메모리 MB)을 출력. GCM_NURSERY_KB 로 nursery 크기를 바꿈
set -e
cd "$(dirname "$0")"
GC_PREFIX="${CALC_GC_PREFIX:-/opt/homebrew/opt/bdw-gc}"
OUT="${TMPDIR:-/tmp}/calc-gc-model"
mkdir -p "$OUT"
STACK="-Wl,-stack_size,0x20000000"
median() {
    for k in 1 2 3 4 5; do
        /usr/bin/time -l sh -c "$1 >/dev/null 2>&1" 2>&1 | awk '/real/{r=$1} /maximum resident/{m=$1} END{printf "%s %d\n", r, m/1048576}'
    done | sort -n | sed -n 3p
}
for m in msort live bst mut deep; do
    clang -O2 "$m.c" -I"$GC_PREFIX/include" -L"$GC_PREFIX/lib" -lgc $STACK -o "$OUT/${m}_boehm"
    clang -O2 -DUSE_GCM "$m.c" gcm.c $STACK -o "$OUT/${m}_gcm"
    "$OUT/${m}_boehm" > "$OUT/$m.boehm.txt"
    "$OUT/${m}_gcm" > "$OUT/$m.gcm.txt"
    cmp -s "$OUT/$m.boehm.txt" "$OUT/$m.gcm.txt" || { echo "$m: 결과가 다름"; exit 1; }
    printf "%-6s boehm %ss %sMB   gcm %ss %sMB\n" "$m" $(median "$OUT/${m}_boehm") $(median "$OUT/${m}_gcm")
done
