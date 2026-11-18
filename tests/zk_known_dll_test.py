"""Prove the global bare-name libzkfp.dll override (KnownDLLs).

Loads libzkfp.dll by BARE NAME from a temp cwd that also holds an app-local
copy, then uses GetModuleHandleW/GetModuleFileNameW to prove Windows actually
loaded the shim from System32 instead of that cwd copy.

NOTE: a FULL-PATH LoadLibrary bypasses KnownDLLs entirely; this test only
exercises bare-name resolution.  The registration takes effect at boot, so a
REBOOT IS REQUIRED after installing the override before this test sees it.

Must run elevated: the shim's fallback file zkshim-config.txt lives next to the
loaded DLL in System32, so writing it needs admin rights.  Skips with a clear
message when that write is not permitted.
"""
import base64
import ctypes
import hashlib
import os
import shutil
import struct
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / "zkshim" / "x64" / "Release" / "libzkfp.dll"
BARE_NAME = "libzkfp.dll"
CFG_NAME = "zkshim-config.txt"

TPL = b"MZKF" + struct.pack("<I", 64) + bytes(range(64))
ID16 = format(42, "016x")

if not DLL.is_file():
    print("FAIL: shim DLL not found: %s" % DLL)
    sys.exit(2)

# A 32-bit process resolves System32 to SysWOW64, invalidating the assertion.
if struct.calcsize("P") != 8:
    print("SKIP: x64 Python is required for the System32 assertion")
    sys.exit(0)


def check(name, cond):
    print(("PASS " if cond else "FAIL ") + name)
    if not cond:
        raise AssertionError(name)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main():
    system32 = Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32"
    expected_hash = sha256(DLL)

    tmp = tempfile.TemporaryDirectory(prefix="zk-known-dll-")
    tmpdir = tmp.name
    old_cwd = os.getcwd()
    dll_dir = None
    lib = None
    cfg_path = None
    old_cfg = None
    wrote_cfg = False
    inited = False
    dev = None
    Init = Term = Close = Acquire = None

    try:
        shutil.copy2(str(DLL), os.path.join(tmpdir, BARE_NAME))
        os.chdir(tmpdir)
        # The app-local copy is a real search candidate; KnownDLLs must win.
        dll_dir = os.add_dll_directory(tmpdir)

        k32 = ctypes.windll.kernel32
        k32.GetModuleHandleW.restype = ctypes.c_void_p
        k32.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
        k32.GetModuleFileNameW.restype = ctypes.c_uint
        k32.GetModuleFileNameW.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_wchar), ctypes.c_uint]

        lib = ctypes.WinDLL(BARE_NAME)  # bare name, never an absolute path
        hmod = k32.GetModuleHandleW(BARE_NAME)
        check("GetModuleHandleW", bool(hmod))
        buf = ctypes.create_unicode_buffer(32768)
        length = k32.GetModuleFileNameW(hmod, buf, len(buf))
        check("GetModuleFileNameW", 0 < length < len(buf))
        loaded = Path(buf.value)
        got_hash = sha256(loaded)
        print("LOADED: %s" % loaded)
        print("SHA256: %s" % got_hash)
        print("EXPECT: %s" % expected_hash)

        check("loaded from System32",
              str(loaded.resolve()).lower().startswith(
                  str(system32.resolve()).lower() + os.sep))
        check("hash matches zkshim/x64/Release/libzkfp.dll", got_hash == expected_hash)

        # Kept local: importing zk_shim_test would full-path load the app-local
        # DLL first, and its loaded-module entry would defeat the KnownDLLs check.
        def sym(name):
            fn = getattr(lib, name, None) or getattr(lib, "ZKFPM_" + name, None)
            if fn is None:
                raise AssertionError("missing export: " + name)
            return fn

        Init = sym("ZKFPM_Init")
        Term = sym("ZKFPM_Terminate")
        Count = sym("GetDeviceCount")
        Open = sym("OpenDevice")
        Close = sym("CloseDevice")
        Acquire = sym("AcquireFingerprint")

        U = ctypes.c_uint
        PB = ctypes.POINTER(ctypes.c_ubyte)

        def sig(fn, restype, *argtypes):
            fn.restype = restype
            fn.argtypes = list(argtypes)

        sig(Init, ctypes.c_int)
        sig(Term, ctypes.c_int)
        sig(Count, ctypes.c_int)
        sig(Open, ctypes.c_void_p, ctypes.c_int)
        sig(Close, ctypes.c_int, ctypes.c_void_p)
        sig(Acquire, ctypes.c_int, ctypes.c_void_p, PB, U, PB, ctypes.POINTER(U))

        # Fallback sits beside the loaded DLL (System32): writing needs elevation.
        cfg_path = loaded.parent / CFG_NAME
        try:
            old_cfg = cfg_path.read_text(encoding="utf-8") if cfg_path.exists() else None
            line = "ACQUIRE %s %s\n" % (ID16, base64.b64encode(TPL).decode())
            cfg_path.write_text(line, encoding="utf-8")
            wrote_cfg = True
        except OSError as exc:
            print("SKIP: cannot write fallback %s (run elevated): %s" % (cfg_path, exc))
            return

        inited = Init() >= 0
        check("Init", inited)
        check("GetDeviceCount", Count() >= 1)
        dev = Open(0)
        check("OpenDevice", bool(dev))

        tpl = (ctypes.c_ubyte * 2048)()
        img = (ctypes.c_ubyte * 65536)()
        n = U(2048)  # shim reads this as the output buffer capacity
        rc = Acquire(dev, img, 65536, tpl, ctypes.byref(n))
        frame = bytes(tpl[:n.value])
        check("AcquireFingerprint rc == 0", rc == 0)
        check("template starts with MZKF", frame[:4] == b"MZKF")
        check("template length field", len(frame) >= 8
              and struct.unpack_from("<I", frame, 4)[0] + 8 == len(frame))
        print("PASS all")
    finally:
        if dev and Close:
            Close(dev)
        if inited and Term:
            Term()
        if wrote_cfg:
            try:
                if old_cfg is None:
                    cfg_path.unlink()
                else:
                    cfg_path.write_text(old_cfg, encoding="utf-8")
            except OSError as exc:
                print("WARN: could not restore fallback: %s" % exc)
        if dll_dir is not None:
            dll_dir.close()
        os.chdir(old_cwd)
        try:
            tmp.cleanup()
        except OSError as exc:
            # A locked tmpdir copy means the shim did not come from System32;
            # keep the original assertion as the reported failure.
            print("WARN: could not clean temp dir: %s" % exc)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print("FAIL: %s: %s" % (type(exc).__name__, exc))
        sys.exit(1)
