"""TI-01 binary evidence: PE import table + string scan + source fetch.

Run:  python tools/CSharp.UI.ImeProbe/scripts/binary_evidence.py
Writes raw output to artifacts/ime-work/ (see --out).
"""
from __future__ import annotations

import argparse
import json
import os
import struct
import sys
import urllib.request

PRINTED = []


def log(line: str = "") -> None:
    PRINTED.append(line)
    print(line, flush=True)


def read_pe_imports(path: str) -> dict:
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:2] != b"MZ":
        return {"error": "not a PE file"}
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e_lfanew : e_lfanew + 4] != b"PE\0\0":
        return {"error": "missing PE signature"}
    coff = e_lfanew + 4
    machine, n_sections, timestamp = struct.unpack_from("<HHI", data, coff)
    size_opt = struct.unpack_from("<H", data, coff + 16)[0]
    opt = coff + 20
    magic = struct.unpack_from("<H", data, opt)[0]
    is_pe32_plus = magic == 0x20B
    dd_off = opt + (112 if is_pe32_plus else 96)
    import_rva, import_size = struct.unpack_from("<II", data, dd_off + 8)
    sections = []
    sec_off = opt + size_opt
    for i in range(n_sections):
        base = sec_off + i * 40
        name = data[base : base + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, base + 8)
        sections.append((name, vaddr, vsize, rawptr, rawsize))

    def rva_to_off(rva: int):
        for name, vaddr, vsize, rawptr, rawsize in sections:
            if vaddr <= rva < vaddr + max(vsize, rawsize):
                return rawptr + (rva - vaddr)
        return None

    if import_rva == 0:
        return {"imports": [], "note": "no import directory", "sections": [s[0] for s in sections]}

    off = rva_to_off(import_rva)
    dlls = []
    while off is not None:
        entry = struct.unpack_from("<IIIII", data, off)
        if entry == (0, 0, 0, 0, 0):
            break
        name_off = rva_to_off(entry[3])
        if name_off is None:
            break
        end = data.index(b"\0", name_off)
        dlls.append(data[name_off:end].decode("ascii", "replace"))
        off += 20
    return {
        "machine": hex(machine),
        "pe32_plus": is_pe32_plus,
        "sections": [s[0] for s in sections],
        "imports": dlls,
    }


def scan_ascii(path: str, needles) -> dict:
    with open(path, "rb") as fh:
        data = fh.read()
    lowered = data.lower()
    result = {}
    for needle in needles:
        hits = 0
        start = 0
        nb = needle.lower().encode("ascii")
        while True:
            idx = lowered.find(nb, start)
            if idx < 0:
                break
            hits += 1
            start = idx + 1
        result[needle] = hits
    return result


def scan_utf16(path: str, needles) -> dict:
    with open(path, "rb") as fh:
        data = fh.read()
    lowered = data.lower()
    result = {}
    for needle in needles:
        nb = needle.encode("utf-16-le").lower()
        hits = 0
        start = 0
        while True:
            idx = lowered.find(nb, start)
            if idx < 0:
                break
            hits += 1
            start = idx + 1
        result[needle] = hits
    return result


FETCH = {
    "glfw-3.4-win32_window.c": "https://raw.githubusercontent.com/glfw/glfw/3.4/src/win32_window.c",
    "glfw-3.4-win32_init.c": "https://raw.githubusercontent.com/glfw/glfw/3.4/src/win32_init.c",
    "glfw-3.4-cocoa_window.m": "https://raw.githubusercontent.com/glfw/glfw/3.4/src/cocoa_window.m",
    "glfw-3.4-x11_window.c": "https://raw.githubusercontent.com/glfw/glfw/3.4/src/x11_window.c",
    "glfw-3.4-wl_window.c": "https://raw.githubusercontent.com/glfw/glfw/3.4/src/wl_window.c",
    "glfw-3.4-include-glfw3.h": "https://raw.githubusercontent.com/glfw/glfw/3.4/include/GLFW/glfw3.h",
    "opentk-4.9.4-NativeWindow.cs": "https://raw.githubusercontent.com/opentk/opentk/4.9.4/src/OpenTK.Windowing.Desktop/NativeWindow.cs",
    "opentk-4.9.4-GLFWNative.cs": "https://raw.githubusercontent.com/opentk/opentk/4.9.4/src/OpenTK.Windowing.GraphicsLibraryFramework/GLFWNative.cs",
}


def fetch_sources(out_dir: str) -> None:
    os.makedirs(out_dir, exist_ok=True)
    for name, url in FETCH.items():
        target = os.path.join(out_dir, name)
        if os.path.exists(target) and os.path.getsize(target) > 0:
            log(f"FETCH {name}: cached ({os.path.getsize(target)} bytes)")
            continue
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "ti01-ime-probe"})
            with urllib.request.urlopen(req, timeout=60) as resp:
                body = resp.read()
            with open(target, "wb") as fh:
                fh.write(body)
            log(f"FETCH {name}: {len(body)} bytes from {url}")
        except Exception as exc:  # noqa: BLE001
            log(f"FETCH {name}: FAILED {type(exc).__name__}: {exc}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--glfw", required=True)
    ap.add_argument("--opentk-dir", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--fetch-sources", action="store_true")
    args = ap.parse_args()

    os.makedirs(args.out, exist_ok=True)
    report = {}

    log(f"=== PE import table: {args.glfw} ===")
    glfw = read_pe_imports(args.glfw)
    report["glfw_imports"] = glfw
    for dll in glfw.get("imports", []):
        log(f"  imports {dll}")
    log(f"  sections={glfw.get('sections')} machine={glfw.get('machine')} pe32plus={glfw.get('pe32_plus')}")

    ime_needles = [
        "imm32.dll",
        "IMM32.dll",
        "ImmGetContext",
        "ImmGetCompositionString",
        "ImmSetCandidateWindow",
        "ImmAssociateContext",
        "ImmReleaseContext",
        "WM_IME_COMPOSITION",
        "ImmNotifyIME",
    ]
    log()
    log(f"=== raw byte scan (ascii) in glfw3.dll ===")
    ascii_hits = scan_ascii(args.glfw, ime_needles)
    report["glfw_ascii_hits"] = ascii_hits
    for k, v in ascii_hits.items():
        log(f"  {k}: {v}")
    log(f"=== raw byte scan (utf-16le) in glfw3.dll ===")
    utf16_hits = scan_utf16(args.glfw, ime_needles)
    report["glfw_utf16_hits"] = utf16_hits
    for k, v in utf16_hits.items():
        log(f"  {k}: {v}")

    log()
    log(f"=== managed assembly scan: {args.opentk_dir} ===")
    managed = {}
    for root, _dirs, files in os.walk(args.opentk_dir):
        for fname in files:
            if not fname.lower().endswith(".dll"):
                continue
            full = os.path.join(root, fname)
            try:
                hits = scan_ascii(full, ime_needles)
            except Exception as exc:  # noqa: BLE001
                hits = {"error": str(exc)}
            managed[os.path.relpath(full, args.opentk_dir)] = hits
            if any(hits.get(n, 0) for n in ime_needles):
                log(f"  {fname}: {json.dumps(hits, ensure_ascii=False)}")
    report["managed_hits"] = managed
    log(f"  scanned {len(managed)} managed dlls")

    if args.fetch_sources:
        log()
        log("=== fetch pinned sources (python TLS) ===")
        fetch_sources(os.path.join(args.out, "source-refs"))

    with open(os.path.join(args.out, "binary-evidence.json"), "w", encoding="utf-8") as fh:
        json.dump(report, fh, indent=2, ensure_ascii=False)
    with open(os.path.join(args.out, "binary-evidence.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(PRINTED) + "\n")
    log()
    log(f"wrote {os.path.join(args.out, 'binary-evidence.json')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
