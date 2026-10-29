# N-Queens: 리스트와 backtracking
def safe(q, d, qs):
    for x in qs:
        if x == q or x == q + d or x == q - d:
            return False
        d += 1
    return True

def place(n, row, qs):
    if row == n:
        return 1
    acc = 0
    for c in range(n):
        if safe(c, 1, qs):
            acc += place(n, row + 1, [c] + qs)
    return acc

print(place(10, 0, []))
