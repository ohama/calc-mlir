# 큰 입력판 (2백만 번 넣고 2백만 번 찾기): benchmarks/bst.py 와 같고 크기만 키움
# 이진 탐색 트리: LCG 키 20만 개를 넣고 크기, 높이, 찾기
import sys
sys.setrecursionlimit(10000)

# Leaf = None, Node = (l, v, r) (불변 튜플: Calc 의 ADT 처럼 새 노드를 만듦)
def insert(x, t):
    if t is None:
        return (None, x, None)
    l, v, r = t
    if x < v:
        return (insert(x, l), v, r)
    elif x > v:
        return (l, v, insert(x, r))
    return t

def contains(x, t):
    while t is not None:
        l, v, r = t
        if x == v:
            return True
        t = l if x < v else r
    return False

def size(t):
    return 0 if t is None else size(t[0]) + 1 + size(t[2])

def height(t):
    return 0 if t is None else 1 + max(height(t[0]), height(t[2]))

t, x = None, 1
for _ in range(2000000):
    t = insert(x, t)
    x = (x * 75 + 74) % 65537

hits, x = 0, 5
for _ in range(2000000):
    if contains(x, t):
        hits += 1
    x = (x * 13 + 7) % 100003

print((size(t), height(t), hits))
