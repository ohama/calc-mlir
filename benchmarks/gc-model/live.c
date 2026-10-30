#include "model.h"
int main(void) {
    init();
    value big = NIL;
    for (int64_t i = 1; i <= 2000000; i++) big = cons(tuple2(INT(i), data1(1, INT(i))), big);
    int64_t acc = 0;
    for (int64_t n = 20000000; n > 0; n--) {
        value o = data1(1, tuple2(INT(n), cons(INT(n), NIL)));   /* Some (n, [n]) */
        value t = FIELD(o, 1);
        acc += VAL(FIELD(t, 1)) + (TL(FIELD(t, 2)) == NIL ? 1 : 0);
    }
    int64_t len = 0; for (value p = big; p != NIL; p = TL(p)) len++;
    printf("%lld %lld\n", (long long)acc, (long long)len); done();
}
