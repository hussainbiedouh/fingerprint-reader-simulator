"""Integration smoke test for the fake ZKTeco shim (stdlib only)."""
import base64
import ctypes
import os
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SHIM = ROOT / "zkshim" / "x64" / "Release"
DLL = SHIM / "libzkfp.dll"
CFG = SHIM / "zkshim-config.txt"

if not DLL.is_file():
    print(f"FAIL: shim DLL not found: {DLL}")
    sys.exit(2)

try:
    os.add_dll_directory(str(SHIM))
    lib = ctypes.WinDLL(str(DLL))
except OSError as exc:
    print("FAIL: cannot load shim: %s" % exc)
    sys.exit(2)


def sym(name):
    fn = getattr(lib, name, None) or getattr(lib, "ZKFPM_" + name, None)
    if fn is None:
        print("FAIL: missing export: " + name)
        sys.exit(2)
    return fn


Init = sym("ZKFPM_Init")
Term = sym("Terminate")
Count = sym("GetDeviceCount")
Open = sym("OpenDevice")
Close = sym("CloseDevice")
Acquire = sym("AcquireFingerprint")
DBInit = sym("DBInit")
DBFree = sym("DBFree")
DBAdd = sym("DBAdd")
DBIdent = sym("DBIdentify")
DBMatch = sym("DBMatch")
DBMerge = sym("DBMerge")

U = ctypes.c_uint
PU = ctypes.POINTER(U)
PB = ctypes.POINTER(ctypes.c_ubyte)
H = ctypes.c_void_p


def sig(fn, restype, *argtypes):
    fn.restype = restype
    fn.argtypes = list(argtypes)


sig(Init, ctypes.c_int)
sig(Term, ctypes.c_int)
sig(Count, ctypes.c_int)
sig(Open, H, ctypes.c_int)
sig(Close, ctypes.c_int, H)
sig(Acquire, ctypes.c_int, H, PB, U, PB, PU)
sig(DBInit, H)
sig(DBFree, ctypes.c_int, H)
sig(DBAdd, ctypes.c_int, H, U, PB, U)
sig(DBIdent, ctypes.c_int, H, PB, U, PU, PU)
sig(DBMatch, ctypes.c_int, H, PB, U, PB, U)
sig(DBMerge, ctypes.c_int, H, PB, PB, PB, PB, PU)

TPL = b"MZKF" + struct.pack("<I", 64) + bytes(range(64))


def check(name, cond):
    print(("PASS " if cond else "FAIL ") + name)
    if not cond:
        raise AssertionError(name)


def uarr(size, data=b""):
    buf = (ctypes.c_ubyte * size)()
    if data:
        ctypes.memmove(buf, data, len(data))
    return buf


def main():
    old_cfg = CFG.read_text(encoding="utf-8") if CFG.exists() else None
    dev = db = None
    inited = False
    try:
        CFG.write_text("ACQUIRE 01 " + base64.b64encode(TPL).decode() + "\n", encoding="utf-8")
        inited = Init() >= 0
        check("Init", inited)
        check("GetDeviceCount", Count() >= 1)
        dev = Open(0)
        check("OpenDevice", bool(dev))

        tpl = uarr(2048)
        img = uarr(65536)
        first_frame = None
        for i in range(3):
            n = U(2048)
            rc = Acquire(dev, img, 65536, tpl, ctypes.byref(n))
            frame = bytes(tpl[:n.value]) if rc == 0 and n.value <= 2048 else b""
            valid = (frame[:4] == b"MZKF" and len(frame) >= 8
                     and struct.unpack_from("<I", frame, 4)[0] + 8 == len(frame))
            if i == 0:
                first_frame = frame
            check("AcquireFingerprint[%d]" % i, valid and frame == first_frame)

        db = DBInit()
        check("DBInit", bool(db))

        inputs = [uarr(72, TPL) for _ in range(3)]
        merged = uarr(256)
        mlen = U(256)
        rc = DBMerge(db, inputs[0], inputs[1], inputs[2], merged, ctypes.byref(mlen))
        check("DBMerge", rc == 0 and mlen.value == 3 * len(TPL)
              and bytes(merged[:mlen.value]) == TPL * 3)

        check("DBAdd", DBAdd(db, 7, tpl, len(first_frame)) == 0)
        fid, score = U(0), U(0)
        rc = DBIdent(db, tpl, len(first_frame), ctypes.byref(fid), ctypes.byref(score))
        check("DBIdentify", rc == 0 and fid.value == 7 and score.value == 99)

        check("DBMatch", DBMatch(db, merged, mlen.value, merged, mlen.value) == 0)
        print("PASS all")
    finally:
        if db:
            DBFree(db)
        if dev:
            Close(dev)
        if inited:
            Term()
        if old_cfg is None:
            if CFG.exists():
                CFG.unlink()
        else:
            CFG.write_text(old_cfg, encoding="utf-8")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print("FAIL: %s: %s" % (type(exc).__name__, exc))
        sys.exit(1)
