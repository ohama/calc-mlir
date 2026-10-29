# 피보나치: 함수 호출과 tail call 이 아닌 재귀
def fib(n):
    return n if n < 2 else fib(n - 1) + fib(n - 2)
print(fib(32))
