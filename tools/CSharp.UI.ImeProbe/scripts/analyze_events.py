"""TI-01 evidence analysis: turn an events.jsonl probe log into a compact report.

Usage:
  python tools/CSharp.UI.ImeProbe/scripts/analyze_events.py <events.jsonl> [--max-order 40]
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter


def load(path: str) -> list[dict]:
    records = []
    with open(path, "r", encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            records.append(json.loads(line))
    return records


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--max-order", type=int, default=48)
    args = ap.parse_args()

    records = load(args.path)
    out: list[str] = []

    def emit(line: str = "") -> None:
        out.append(line)
        print(line)

    emit(f"events: {len(records)}  file: {args.path}")
    stage_counts = Counter(r.get("stage") for r in records)
    emit(f"stages: {dict(stage_counts)}")
    kinds = Counter(r.get("kind") for r in records)
    emit(f"kinds: {dict(kinds)}")

    tids = Counter(r.get("tid") for r in records)
    emit(f"threads (managed tid): {dict(tids)}")
    emit(f"process/session: pid={records[0].get('pid')} session={records[0].get('session')}")

    messages = [r for r in records if r.get("kind") == "message"]
    emit()
    emit("message histogram (wndproc):")
    for name, count in Counter(m.get("msg") for m in messages).most_common():
        emit(f"  {name}: {count}")

    ime_messages = [m for m in messages if str(m.get("msg", "")).startswith("WM_IME_")]
    emit()
    emit(f"WM_IME_* messages: {len(ime_messages)}")
    for m in ime_messages[:20]:
        emit(f"  seq={m['seq']} t_ms={m['t_ms']} {m['msg']} wparam={m['wparam']} lparam={m['lparam']}"
             + (f" gcs={m.get('gcs_flags')}" if m.get("gcs_flags") else "")
             + (f" imn={m.get('imn')}" if m.get("imn") else ""))

    comps = [r for r in records if r.get("kind") == "composition-update"]
    starts = [r for r in records if r.get("kind") == "composition-start"]
    ends = [r for r in records if r.get("kind") == "composition-end"]
    emit()
    emit(f"composition-start={len(starts)} composition-update={len(comps)} composition-end={len(ends)}")
    for c in comps[:10]:
        emit(f"  seq={c['seq']} flags={c.get('flags')} comp={c.get('comp_str_utf16')} "
             f"result={c.get('result_str_utf16')} cursor_return={c.get('cursor_pos_return')} "
             f"attrs={c.get('comp_attr_hex')} clauses={c.get('comp_clause_dwords')}")

    emit()
    emit("IMM32 context states:")
    for r in (x for x in records if x.get("kind") == "ime-state"):
        emit(f"  seq={r['seq']} trigger={r.get('trigger')} himc={r.get('himc')} "
             f"ime_desc={r.get('ime_description')!r} ime_file={r.get('ime_file_name')!r} "
             f"open={r.get('open_status')} conversion={r.get('conversion')} "
             f"native={r.get('cmode_native')} candidates={r.get('candidate_count')} "
             f"default_ime_wnd={r.get('default_ime_window')}")

    emit()
    emit("text input events (GLFW char callback -> OpenTK TextInput):")
    for r in (x for x in records if x.get("kind") == "text-input"):
        emit(f"  seq={r['seq']} t_ms={r['t_ms']} unicode={r.get('unicode_hex')} text={r.get('text')!r} "
             f"utf16={r.get('utf16')} units={r.get('utf16_units')} code_points={r.get('code_points')}")

    emit()
    emit("candidate anchor requests (requested vs IMM readback):")
    for r in (x for x in records if x.get("kind") == "candidate-anchor"):
        emit(f"  seq={r['seq']} trigger={r.get('trigger')} requested_client={r.get('requested_client')} "
             f"requested_screen={r.get('requested_screen')} dpi={r.get('window_dpi')} "
             f"set_ok={r.get('set_candidate_ok')} get_ok={r.get('get_candidate_ok')} "
             f"ime_reported={r.get('ime_reported_client_or_screen')} style={r.get('ime_reported_style')} "
             f"rect={r.get('window_rect_screen')}")

    emit()
    emit("TSF records:")
    for r in (x for x in records if x.get("stage") in ("tsf", "ActivateProfile")):
        emit(f"  seq={r['seq']} {r.get('kind')} " + json.dumps(
            {k: v for k, v in r.items() if k not in ("seq", "t_ms", "wall", "tid", "pid", "session", "stage", "source", "kind")},
            ensure_ascii=False))

    emit()
    emit("synthetic / boundary records:")
    for r in (x for x in records if "synthetic" in x or x.get("kind") in (
            "callback-exception", "fault-injection-armed", "aborted-foreground-lost", "associate-own-context")):
        emit(f"  seq={r['seq']} {r.get('kind')} " + json.dumps(
            {k: v for k, v in r.items() if k not in ("seq", "t_ms", "wall", "tid", "pid", "session", "stage", "source", "kind")},
            ensure_ascii=False))

    emit()
    emit("interleaving around the first key press (native message vs managed callback):")
    first_key = next((r["seq"] for r in records if r.get("kind") == "message" and r.get("msg") == "WM_KEYDOWN"), None)
    if first_key is None:
        emit("  (no WM_KEYDOWN recorded)")
    else:
        window = [r for r in records if r["seq"] >= first_key and r["seq"] <= first_key + args.max_order]
        for r in window:
            detail = r.get("msg") or r.get("kind")
            extra = ""
            if r.get("kind") == "text-input":
                extra = f" text={r.get('text')!r} unicode={r.get('unicode_hex')}"
            elif r.get("kind") == "key-event":
                extra = f" step={r.get('step')}"
            elif r.get("kind") == "key-down":
                extra = f" key={r.get('key')} repeat={r.get('is_repeat')}"
            elif r.get("kind") == "message" and r.get("code_unit") is not None:
                extra = f" code_unit=0x{r.get('code_unit'):X}" if isinstance(r.get("code_unit"), int) else ""
            emit(f"  seq={r['seq']:<5} {r.get('stage'):<8} {r.get('source'):<24} {detail}{extra}")

    emit()
    emit("counters:")
    counters = {
        "wm_char_messages": sum(1 for r in records if r.get("msg") == "WM_CHAR"),
        "wm_unichar_messages": sum(1 for r in records if r.get("msg") == "WM_UNICHAR"),
        "wm_ime_char_messages": sum(1 for r in records if r.get("msg") == "WM_IME_CHAR"),
        "wm_ime_startcomposition": sum(1 for r in records if r.get("msg") == "WM_IME_STARTCOMPOSITION"),
        "wm_ime_composition": sum(1 for r in records if r.get("msg") == "WM_IME_COMPOSITION"),
        "wm_ime_setcontext": sum(1 for r in records if r.get("msg") == "WM_IME_SETCONTEXT"),
        "wm_ime_request": sum(1 for r in records if r.get("msg") == "WM_IME_REQUEST"),
        "wm_ime_notify": sum(1 for r in records if r.get("msg") == "WM_IME_NOTIFY"),
        "glfw_key_down": sum(1 for r in records if r.get("kind") == "key-down"),
        "glfw_key_down_repeat": sum(1 for r in records if r.get("kind") == "key-down" and r.get("is_repeat")),
        "glfw_text_input": sum(1 for r in records if r.get("kind") == "text-input"),
        "callback_exceptions": sum(1 for r in records if r.get("kind") == "callback-exception"),
        "screenshots": sum(1 for r in records if r.get("kind") == "screenshot-saved"),
        "glfw_errors": sum(1 for r in records if r.get("kind") == "glfw-error"),
    }
    for key, value in counters.items():
        emit(f"  {key}: {value}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
