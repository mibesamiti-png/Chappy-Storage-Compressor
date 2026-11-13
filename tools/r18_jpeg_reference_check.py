#!/usr/bin/env python3
"""Independent Python JPEG reference checks. Not C# or Windows runtime verification."""
import io
import struct
from PIL import Image


def jpeg(rgb):
    im = Image.new('RGB', (9, 7), rgb)
    buf = io.BytesIO()
    im.save(buf, 'JPEG', quality=90, progressive=False, exif=b'Exif\0\0R18reference')
    return buf.getvalue()


def parse_metadata(payload):
    if payload[:2] != b'\xff\xd8':
        raise ValueError('No SOI')
    pos = 2
    protected = []
    saw_sos = False
    while pos < len(payload):
        if payload[pos] != 255:
            raise ValueError('Invalid marker')
        pos += 1
        while pos < len(payload) and payload[pos] == 255:
            pos += 1
        if pos >= len(payload):
            raise ValueError('Missing marker')
        marker = payload[pos]
        pos += 1
        if marker == 217:
            if pos != len(payload):
                raise ValueError('Trailer')
            return protected, saw_sos
        if pos + 2 > len(payload):
            raise ValueError('Missing length')
        n = struct.unpack('>H', payload[pos:pos + 2])[0]
        if n < 2 or pos + n > len(payload):
            raise ValueError('Bad length')
        raw = payload[pos - 2:pos + n]
        pos += n
        if 0xe0 <= marker <= 0xef or marker in (0xfe, 0xdb, 0xdd, 0xdc, 0xc0):
            protected.append(raw)
        if marker == 0xda:
            if saw_sos:
                raise ValueError('Multiscan')
            saw_sos = True
            while pos < len(payload):
                if payload[pos] != 255:
                    pos += 1
                    continue
                pos += 1
                while pos < len(payload) and payload[pos] == 255:
                    pos += 1
                marker = payload[pos]
                pos += 1
                if marker == 0 or 0xd0 <= marker <= 0xd7:
                    continue
                pos -= 2
                break
    raise ValueError('No EOI')


img = jpeg((80, 120, 160))
other = jpeg((80, 120, 170))
checks = [
    ('baseline JPEG parses', parse_metadata(img)[1]),
    ('same JPEG decoded pixels are equal', Image.open(io.BytesIO(img)).tobytes() == Image.open(io.BytesIO(img)).tobytes()),
    ('different JPEG pixels rejected', Image.open(io.BytesIO(img)).tobytes() != Image.open(io.BytesIO(other)).tobytes()),
]
metadata, _ = parse_metadata(img)
checks.append(('metadata markers captured', any(seg[:2] == b'\xff\xe1' for seg in metadata)))
app = b'\xff\xe2\0\x08ABCDEF'
changed, _ = parse_metadata(img[:2] + app + img[2:])
checks.append(('changed APP marker detected', changed != metadata))
try:
    parse_metadata(img + b'garbage')
    trailer_rejected = False
except ValueError:
    trailer_rejected = True
checks.append(('trailing bytes rejected', trailer_rejected))
for label, ok in checks:
    print(('PASS' if ok else 'FAIL') + ': ' + label)
if not all(ok for _, ok in checks):
    raise SystemExit(1)
print(f'{len(checks)}/{len(checks)} independent reference checks passed. C# runtime not tested.')
