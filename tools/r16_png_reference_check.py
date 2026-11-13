#!/usr/bin/env python3
"""Independent standard-library PNG test-vector check.
This validates fixture expectations/PNG math; it does NOT execute C#.
"""
import binascii
import io
import struct
import zlib

SIG = b'\x89PNG\r\n\x1a\n'
PASSES = [(0,0,8,8),(4,0,8,8),(0,4,4,8),(2,0,4,4),(0,2,2,4),(1,0,2,2),(0,1,1,2)]

def chunk(name, body):
    return struct.pack('>I', len(body)) + name + body + struct.pack('>I', binascii.crc32(name+body))

def scanlines(w, h, channels, depth, interlace):
    result = bytearray()
    passes = PASSES if interlace else [(0,0,1,1)]
    for xs,ys,dx,dy in passes:
        pw = max(0, (w-xs+dx-1)//dx)
        ph = max(0, (h-ys+dy-1)//dy)
        if not pw or not ph:
            continue
        pitch = (pw*channels*depth+7)//8
        result.extend((b'\x00'+bytes(pitch))*ph)
    return bytes(result)

def parse_png(data):
    assert data[:8] == SIG
    at=8; chunks=[]
    while at<len(data):
        size, = struct.unpack_from('>I',data,at)
        name=data[at+4:at+8]
        body=data[at+8:at+8+size]
        crc,=struct.unpack_from('>I',data,at+8+size)
        assert crc==binascii.crc32(name+body), name
        chunks.append((name,body))
        at+=size+12
        if name==b'IEND': break
    assert at==len(data)
    return chunks

def main():
    vectors=[
        ('rgba-idat3',97,89,6,8,False,True),
        ('rgb-adam7',97,89,2,8,True,True),
        ('gray16',97,89,0,16,False,False),
        ('palette4',97,89,3,4,False,True),
        ('gray1-adam7',97,89,0,1,True,False),
    ]
    for name,w,h,color,depth,adam7,multi in vectors:
        channels={0:1,2:3,3:1,4:2,6:4}[color]
        raw=scanlines(w,h,channels,depth,adam7)
        ihdr=struct.pack('>IIBBBBB', w,h,depth,color,0,0,int(adam7))
        zipped=zlib.compress(raw,level=0)
        parts=[zipped] if not multi else [zipped[:len(zipped)//3], zipped[len(zipped)//3:len(zipped)*2//3], zipped[len(zipped)*2//3:]]
        ch=[chunk(b'IHDR',ihdr)]
        if color==3:
            ch+=[chunk(b'PLTE',b'\0\0\0\xff\xff\xff'),chunk(b'tRNS',b'\0\xff')]
        ch.append(chunk(b'tEXt', b'Author\x00R16 fixture'))
        ch.extend(chunk(b'IDAT', p) for p in parts)
        ch.append(chunk(b'IEND',b''))
        original=SIG+b''.join(ch)
        parsed=parse_png(original)
        observed=b''.join(body for kind,body in parsed if kind==b'IDAT')
        assert zlib.decompress(observed)==raw
        optimized=zlib.compress(raw,level=9)
        candidate=SIG+b''.join(chunk(kind,body) for kind,body in parsed if kind!=b'IDAT' and kind!=b'IEND')+chunk(b'IDAT',optimized)+chunk(b'IEND',b'')
        parsed2=parse_png(candidate)
        assert zlib.decompress(b''.join(body for kind,body in parsed2 if kind==b'IDAT'))==raw
        assert [x for x in parsed if x[0]!=b'IDAT']==[x for x in parsed2 if x[0]!=b'IDAT']
        assert len(candidate)<len(original)
        print(f'PASS {name}: {len(original)} -> {len(candidate)} bytes; raw={len(raw)}')
    print('Reference PNG vectors: 5/5 PASS (Python independent check only)')

if __name__=='__main__': main()
