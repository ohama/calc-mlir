#include "model.h"
#ifdef USE_GCM
#define WRITE(slot, v) gcm_write((slot), (v))
#else
#define WRITE(slot, v) (*(slot) = (v))
#endif
/* 큰 배열(old 에 바로)과 ref(오래 살아 old 로 감)에 새 리스트를 계속 씀: old -> young 포인터 */
int main(void) {
    init();
    int64_t n = 100000;
    value *arr = alloc(8 * (1 + n));
    arr[0] = 8 | (n << 8);
    for (int64_t i = 1; i <= n; i++) arr[i] = INT(0);
    value *refs[64];
    for (int i = 0; i < 64; i++) { refs[i] = alloc(16); refs[i][0] = 6 | (1 << 8); refs[i][1] = NIL; }
    for (int64_t k = 0; k < 3000000; k++) {
        int64_t i = 1 + (k * 7919) % n;
        value old = arr[i];
        value l = cons(INT(k), (old & 7) == 2 ? old : NIL);
        if (((k / 7) & 3) == 0) l = cons(INT(k), NIL); /* 길이를 짧게 유지 */
        WRITE(&arr[i], l);
        value *r = refs[k & 63];
        WRITE(&r[1], cons(INT(k), (k & 1023) ? r[1] : NIL));
    }
    int64_t s = 0, cells = 0;
    for (int64_t i = 1; i <= n; i++) for (value p = arr[i]; (p & 7) == 2; p = TL(p)) { s = (s + VAL(HD(p))) % 1000003; cells++; }
    for (int i = 0; i < 64; i++) for (value p = refs[i][1]; p != NIL; p = TL(p)) { s = (s + VAL(HD(p))) % 1000003; cells++; }
    printf("%lld %lld\n", (long long)s, (long long)cells); done();
}
