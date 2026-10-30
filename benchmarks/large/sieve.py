# 큰 입력판 (1억): benchmarks/sieve.py 와 같고 크기만 키움
# 에라토스테네스의 체: 리스트를 배열로, while
n = 100000000
is_prime = [True] * (n + 1)
is_prime[0] = is_prime[1] = False
i = 2
while i * i <= n:
    if is_prime[i]:
        j = i * i
        while j <= n:
            is_prime[j] = False
            j += i
    i += 1
count = 0
for k in range(n + 1):
    if is_prime[k]:
        count += 1
print(count)
