#include "model.h"
static value rev_append(value xs, value ys) { for (; xs != NIL; xs = TL(xs)) ys = cons(HD(xs), ys); return ys; }
static value merge(value xs, value ys) {
    value acc = NIL;
    while (xs != NIL && ys != NIL) {
        if (HD(xs) <= HD(ys)) { acc = cons(HD(xs), acc); xs = TL(xs); } else { acc = cons(HD(ys), acc); ys = TL(ys); }
    }
    return rev_append(acc, xs != NIL ? xs : ys);
}
static value msort(value xs) {
    if (xs == NIL || TL(xs) == NIL) return xs;
    value l = NIL, r = NIL;
    for (; xs != NIL; xs = TL(xs)) { value t = l; l = cons(HD(xs), r); r = t; }
    return merge(msort(l), msort(r));
}
int main(void) {
    init();
    value xs = NIL; int64_t x = 1;
    for (int i = 0; i < 2000000; i++) { xs = cons(INT(x), xs); x = (x * 75 + 74) % 65537; }
    value s = msort(xs); int64_t h = 0;
    for (; s != NIL; s = TL(s)) h = (h * 31 + VAL(HD(s))) % 1000003;
    printf("%lld\n", (long long)h); done();
}
