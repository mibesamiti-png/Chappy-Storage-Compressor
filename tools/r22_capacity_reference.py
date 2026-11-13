#!/usr/bin/env python3
"""R2.2 independent reference for the advisory capacity formula only.
NOT a compiled C# test and NOT a proof that original replacement is safe.
"""
MARGIN = 64 * 1024 * 1024

def advisory(source, candidate, free_source, free_backup, different_volume, margin=MARGIN):
    if any(n < 0 for n in (source, candidate, free_source, free_backup, margin)) or not source or not candidate:
        return False
    return (candidate < source and different_volume and
            free_source >= margin and candidate <= free_source - margin and
            free_backup >= margin and source <= free_backup - margin)

CASES = [
    ('exact boundary', (100, 70, MARGIN + 70, MARGIN + 100, True), True),
    ('source one byte short', (100, 70, MARGIN + 69, MARGIN + 100, True), False),
    ('backup one byte short', (100, 70, MARGIN + 70, MARGIN + 99, True), False),
    ('same volume', (100, 70, MARGIN + 70, MARGIN + 100, False), False),
    ('not smaller', (100, 100, MARGIN + 200, MARGIN + 200, True), False),
    ('larger candidate', (100, 110, MARGIN + 200, MARGIN + 200, True), False),
    ('zero size', (0, 10, MARGIN + 200, MARGIN + 200, True), False),
    ('negative free', (100, 70, -1, MARGIN + 100, True), False),
    ('zero reserve boundary', (100, 70, 70, 100, True), False),
    ('huge sizes', (2**63-1, 2**63-2, 2**63-1, 2**63-1, True), False),
]
for name, args, expected in CASES:
    got = advisory(*args)
    assert got == expected, f'{name} expected={expected} got={got}'
    print('PASS', name)
print(f'R2.2 capacity formula reference: {len(CASES)}/{len(CASES)} PASS')
