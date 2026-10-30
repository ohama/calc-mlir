#include "model.h"
#define LEAF ((value)((1ll << 32) | 6))
static value insert(value t, int64_t x) {
    if (t == LEAF) return data3(2, LEAF, INT(x), LEAF);
    int64_t v = VAL(FIELD(t, 2));
    if (x < v) return data3(2, insert(FIELD(t, 1), x), FIELD(t, 2), FIELD(t, 3));
    if (x > v) return data3(2, FIELD(t, 1), FIELD(t, 2), insert(FIELD(t, 3), x));
    return t;
}
static int contains(value t, int64_t x) { while (t != LEAF) { int64_t v = VAL(FIELD(t, 2)); if (x < v) t = FIELD(t, 1); else if (x > v) t = FIELD(t, 3); else return 1; } return 0; }
int main(void) {
    init();
    value t = LEAF;
    for (int64_t i = 1000000; i > 0; i--) t = insert(t, (i * 7919) % 1000003);
    int64_t c = 0; for (int64_t i = 1000000; i > 0; i--) c += contains(t, i);
    printf("%lld\n", (long long)c); done();
}
