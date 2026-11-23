# Sums an IL2WASM_COUNT_RC run's site counts (the host's listing, IL2WASM_COUNT_RC_TOP large) by function, charging
# each struct helper's own count operations to the functions that call it (in proportion to their calls).
# usage: python tools/rcsum.py counts.txt [top=40]
import re, sys
from collections import defaultdict

own = defaultdict(int)       # function -> count operations in its own body
calls = defaultdict(lambda: defaultdict(int))  # helper -> caller -> calls
line_re = re.compile(r'^\s*(\d+)\s+(.*) @\d+\s*$')
for line in open(sys.argv[1], encoding='utf-8', errors='replace'):
    m = line_re.match(line)
    if not m: continue
    n, rest = int(m.group(1)), m.group(2)
    if rest.startswith('['):
        helper, caller = rest[1:].split('] ', 1)
        calls[helper][caller] += n
    else:
        kind, fn = rest.split(' ', 1)
        own[fn] += n

# a helper's cost per call, its callees' included (helpers call helpers), memoized
cost = {}
def per_call(h, depth=0):
    if h in cost: return cost[h]
    total_calls = sum(calls[h].values())
    if total_calls == 0 or depth > 20: return 0.0
    inner = own.get(h, 0) + sum(per_call(g, depth + 1) * c for g, cs in calls.items() for caller, c in cs.items() if caller == h)
    cost[h] = inner / total_calls
    return cost[h]

total = defaultdict(float)
helpers = set(calls)
for fn, n in own.items():
    if fn not in helpers: total[fn] += n
for h, cs in calls.items():
    k = per_call(h)
    for caller, c in cs.items():
        if caller not in helpers: total[caller] += k * c
grand = sum(own.values())
print(f'{grand:,} count operations')
for fn, n in sorted(total.items(), key=lambda x: -x[1])[: int(sys.argv[2]) if len(sys.argv) > 2 else 40]:
    print(f'{int(n):>12,}  {n / grand * 100:5.1f}%  {fn[:170]}')
