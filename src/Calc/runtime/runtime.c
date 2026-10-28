#include "runtime.h"

#include <gc.h>
#include <stdio.h>
#include <stdlib.h>

void calc_init(void) { GC_INIT(); }

void *calc_alloc(int64_t bytes) { return GC_MALLOC((size_t)bytes); }

void calc_error(const char *message) {
    fflush(stdout);
    fprintf(stderr, "Error: %s\n", message);
    exit(1);
}

void calc_print_result(value v) {
    if (CALC_IS_INT(v)) {
        printf("%d\n", CALC_INT_VAL(v));
    } else if (CALC_IS_BOOL(v)) {
        printf("%s\n", CALC_BOOL_VAL(v) ? "true" : "false");
    } else {
        calc_error("calc_print_result: unsupported value");
    }
}
