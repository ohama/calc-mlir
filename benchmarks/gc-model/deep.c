#include "model.h"
static int64_t churn(int64_t n) { int64_t acc = 0; for (; n > 0; n--) { value o = data1(1, tuple2(INT(n), cons(INT(n), NIL))); acc += VAL(FIELD(FIELD(o, 1), 1)) & 1; } return acc; }
/* 깊이 d 까지 tail call 이 아닌 재귀 (프레임마다 리스트 하나를 쥠). 바닥에서 할당을 많이 함 */
static int64_t __attribute__((noinline)) dive(int64_t d, value keep) {
    if (d == 0) return churn(20000000);
    value mine = cons(INT(d), keep);
    int64_t r = dive(d - 1, mine);
    return r + (HD(mine) & 1);
}
int main(void) { init(); printf("%lld\n", (long long)dive(1000000, NIL)); done(); }
