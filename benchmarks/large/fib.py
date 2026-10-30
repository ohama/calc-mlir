# 큰 입력판 (fib 41): benchmarks/fib.py 와 같고 크기만 키움
# 피보나치: 함수 호출과 tail call 이 아닌 재귀
def fib(n):
    return n if n < 2 else fib(n - 1) + fib(n - 2)
print(fib(41))
