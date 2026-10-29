# merge sort: Calc 와 같은 알고리즘이지만 자료 구조는 Python 리스트(배열)와 slicing
def gen(n, x):
    acc = []
    for _ in range(n):
        acc.append(x)
        x = (x * 75 + 74) % 65537
    acc.reverse()
    return acc

def split(xs):
    return xs[1::2][::-1], xs[0::2][::-1]   # Calc 의 split 과 같은 두 반쪽 (순서는 정렬 결과에 영향 없음)

def merge(xs, ys):
    out, i, j = [], 0, 0
    while i < len(xs) and j < len(ys):
        if xs[i] <= ys[j]:
            out.append(xs[i]); i += 1
        else:
            out.append(ys[j]); j += 1
    out.extend(xs[i:]); out.extend(ys[j:])
    return out

def msort(xs):
    if len(xs) <= 1:
        return xs
    a, b = split(xs)
    return merge(msort(a), msort(b))

s = msort(gen(200000, 1))
acc = 0
for x in s:
    acc = (acc * 31 + x) % 1000003
print(acc)
print((s[0], s[100000]))
