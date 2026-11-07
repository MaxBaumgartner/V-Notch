"""Films the notch while it opens and closes and turns the film into numbers and pictures.

Still screenshots hide everything that makes motion feel good or bad: stutter, overshoot, a curve that
starts too slowly. This records the top of the primary screen with the Desktop Duplication API (one
picture per screen refresh), measures the black notch shape in every picture and writes:

  report.txt  duration, frames shown vs. frames the display could show, longest stall, overshoot, CPU
  frames.csv  time, width and height of the notch for every captured picture
  curve.png   progress over time per transition, one dot per picture (gaps = stutter)
  strip.png   evenly spaced pictures of each transition

Usage (Windows, ffmpeg 6+ with ddagrab, numpy, Pillow):
  python Tools/NotchMotion/notch_motion.py                  click open, then close (click or move away)
  python Tools/NotchMotion/notch_motion.py --cycles 3       three open/close rounds
  python Tools/NotchMotion/notch_motion.py --action interrupt   click shut again 120 ms into opening
  python Tools/NotchMotion/notch_motion.py --action none --seconds 4   just film, you trigger
  python Tools/NotchMotion/notch_motion.py --analyze DIR    measure an earlier capture again

While filming, a plain white window sits behind the notch so the dark shape (and its hover glow) stands
out from whatever is open; the mouse moves on its own and is put back afterwards. The notch must be on
the primary screen.
"""

from __future__ import annotations

import argparse
import csv
import ctypes
import ctypes.wintypes as wt
import json
import os
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

DARK_ON_WHITE = 100    # luma; on the white backdrop the notch (even hover-lit) stays far below this
DARK_ON_DARK = 6       # without the backdrop: notch fill 0-6, its shadow starts at 7, dark apps are ~12
STILL_GAP_MS = 160     # this long without geometry change ends a transition
# Physical px. The widest page is 720 DIP (1440 px at 200 %). At 360 Hz a smaller area keeps the encoder
# ahead of the screen; --capture sets it.
CAPTURE_W, CAPTURE_H = 1600, 900

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32


# ---------------------------------------------------------------- capture

def make_dpi_aware() -> None:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except OSError:
        user32.SetProcessDPIAware()


def refresh_hz() -> int:
    hdc = user32.GetDC(0)
    hz = ctypes.windll.gdi32.GetDeviceCaps(hdc, 116)  # VREFRESH
    user32.ReleaseDC(0, hdc)
    return hz if hz > 1 else 60


def notch_pid() -> int | None:
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq V-Notch.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True, errors="replace").stdout or ""
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and parts[0].lower() == "v-notch.exe":
            return int(parts[1])
    return None


def process_cpu_seconds(pid: int) -> float | None:
    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        times = [wt.FILETIME() for _ in range(4)]
        if not kernel32.GetProcessTimes(handle, *[ctypes.byref(t) for t in times]):
            return None
        return sum((t.dwHighDateTime << 32 | t.dwLowDateTime) for t in times[2:]) / 1e7
    finally:
        kernel32.CloseHandle(handle)


def notch_settings() -> dict:
    try:
        path = Path(os.environ["APPDATA"]) / "V-Notch" / "settings.json"
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError, KeyError):
        return {}


def click(x: int, y: int) -> None:
    user32.SetCursorPos(x, y)
    time.sleep(0.03)
    user32.mouse_event(0x0002, 0, 0, 0, 0)  # left down
    time.sleep(0.04)
    user32.mouse_event(0x0004, 0, 0, 0, 0)  # left up


def drive(action: str, cycles: int, seconds: float, interrupt_ms: int, screen_w: int) -> None:
    """Clicks the notch like a person would, then puts the mouse back where it was."""
    cursor = wt.POINT()
    user32.GetCursorPos(ctypes.byref(cursor))
    cx = screen_w // 2
    settings = notch_settings()
    try:
        if action == "cycle":
            for _ in range(cycles):
                user32.SetCursorPos(cx, 24)
                time.sleep(0.35)
                click(cx, 24)
                time.sleep(1.4)
                if settings.get("DisableMouseLeaveAutoClose", False):
                    click(cx, 24)  # the empty middle of the top row closes an open notch
                    time.sleep(0.2)
                user32.SetCursorPos(cx, CAPTURE_H + 260)
                time.sleep(settings.get("HoverCollapseDelay", 500) / 1000 + 1.3)
        elif action == "interrupt":
            # Click it open and click again while it is still growing: does it turn around smoothly?
            for _ in range(cycles):
                user32.SetCursorPos(cx, 24)
                time.sleep(0.35)
                click(cx, 24)
                time.sleep(interrupt_ms / 1000)
                click(cx, 24)
                time.sleep(1.2)
                if not settings.get("DisableMouseLeaveAutoClose", False):
                    user32.SetCursorPos(cx, CAPTURE_H + 260)
                    time.sleep(settings.get("HoverCollapseDelay", 500) / 1000 + 1.0)
        else:
            time.sleep(seconds)
    finally:
        user32.SetCursorPos(cursor.x, cursor.y)


BACKDROP = r'''
import ctypes, sys, tkinter as tk
ctypes.windll.shcore.SetProcessDpiAwareness(2)
x, y, w, h = map(int, sys.argv[1:5])
root = tk.Tk()
root.overrideredirect(True)
root.configure(bg="white")
root.geometry(f"{w}x{h}+{x}+{y}")
root.lift()
root.mainloop()
'''


def record(out: Path, action: str, cycles: int, seconds: float, backdrop: bool, interrupt_ms: int) -> dict:
    screen_w = user32.GetSystemMetrics(0)
    left = max(0, screen_w // 2 - CAPTURE_W // 2)
    hz = refresh_hz()
    backdrop_window = ffmpeg = None
    try:
        if backdrop:
            backdrop_window = subprocess.Popen([sys.executable, "-c", BACKDROP, str(left), "0", str(CAPTURE_W), str(CAPTURE_H)])
            time.sleep(0.8)
        video = out / "capture.mkv"
        graph = (f"ddagrab=output_idx=0:framerate={hz}:offset_x={left}:offset_y=0:"
                 f"video_size={CAPTURE_W}x{CAPTURE_H}:dup_frames=0:draw_mouse=0,hwdownload,format=bgra")
        ffmpeg = subprocess.Popen(
            ["ffmpeg", "-hide_banner", "-loglevel", "info", "-f", "lavfi", "-i", graph,
             "-fps_mode", "passthrough", "-c:v", "ffv1", "-level", "3", "-slices", "16", "-threads", "8",
             "-y", str(video)],
            stdin=subprocess.PIPE, stderr=subprocess.PIPE, stdout=subprocess.DEVNULL)

        started = threading.Event()

        def watch_stderr() -> None:
            for raw in iter(ffmpeg.stderr.readline, b""):
                if b"Output #0" in raw or b"frame=" in raw:
                    started.set()
        threading.Thread(target=watch_stderr, daemon=True).start()
        if not started.wait(10):
            sys.exit("ffmpeg did not start capturing (needs ffmpeg 6+ with ddagrab)")
        time.sleep(0.4)  # ddagrab's first frames arrive a little after the output opens

        pid = notch_pid()
        cpu_before = process_cpu_seconds(pid) if pid else None
        wall_before = time.perf_counter()
        drive(action, cycles, seconds, interrupt_ms, screen_w)
        wall = time.perf_counter() - wall_before
        cpu_after = process_cpu_seconds(pid) if pid else None
    finally:
        # Whatever happened, leave nothing filming or covering the screen.
        if ffmpeg and ffmpeg.poll() is None:
            try:
                ffmpeg.stdin.write(b"q")
                ffmpeg.stdin.flush()
                ffmpeg.wait(20)
            except (OSError, subprocess.TimeoutExpired):
                ffmpeg.kill()
        if backdrop_window:
            backdrop_window.terminate()

    meta = {"refresh_hz": hz, "capture_left": left, "capture_size": [CAPTURE_W, CAPTURE_H],
            "wall_seconds": round(wall, 3), "action": action,
            "cycles": cycles,
            "cpu_core_percent": None if cpu_before is None or cpu_after is None
            else round(100 * (cpu_after - cpu_before) / wall, 1)}
    (out / "meta.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
    return meta


# ---------------------------------------------------------------- measuring

def frame_times(video: Path) -> np.ndarray:
    out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v", "-show_entries",
                          "frame=pts_time", "-of", "csv=p=0", str(video)],
                         capture_output=True, text=True, check=True).stdout
    return np.array([float(v) for v in out.split() if v.strip() and v != "N/A"]) * 1000.0


def decode(video: Path, pix_fmt: str, channels: int):
    # passthrough: one output picture per captured picture (the default would pad to a constant rate)
    proc = subprocess.Popen(["ffmpeg", "-v", "error", "-i", str(video), "-fps_mode", "passthrough",
                             "-f", "rawvideo", "-pix_fmt", pix_fmt, "-"], stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL)  # ddagrab's repeated timestamps make it chatty
    size = CAPTURE_W * CAPTURE_H * channels
    while True:
        buf = proc.stdout.read(size)
        if len(buf) < size:
            break
        shape = (CAPTURE_H, CAPTURE_W) if channels == 1 else (CAPTURE_H, CAPTURE_W, channels)
        yield np.frombuffer(buf, np.uint8).reshape(shape)
    proc.wait()


def measure(gray: np.ndarray, dark_max: int) -> tuple[float, float, float]:
    """Width, height and centre x of the notch body in physical pixels."""
    mask = gray <= dark_max
    top = mask[3]  # the first rows carry a faint highlight
    if not top.any() or mask.mean() > 0.9:
        return 0.0, 0.0, 0.0  # no notch, or the blank first picture ddagrab sometimes sends
    # The notch is the longest black run along the top edge (its outer "ears" included).
    edges = np.diff(np.concatenate(([0], top.astype(np.int8), [0])))
    starts, ends = np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)
    i = int(np.argmax(ends - starts))
    a, b = starts[i], ends[i]
    cols = mask[:, a:b]
    any_black = cols.any(axis=0)
    bottom = np.where(any_black, CAPTURE_H - np.argmax(cols[::-1], axis=0), 0)
    height = float(bottom.max())
    body = np.flatnonzero(bottom >= 0.6 * height)
    if body.size == 0:
        return 0.0, height, 0.0
    return float(body[-1] - body[0] + 1), height, float(a + (body[0] + body[-1]) / 2)


@dataclass
class Transition:
    kind: str
    i0: int          # last still frame before the motion
    i1: int          # first frame of the new rest state
    t: np.ndarray
    w: np.ndarray
    h: np.ndarray

    @property
    def duration(self) -> float:
        return float(self.t[self.i1] - self.t[self.i0])


def find_transitions(t: np.ndarray, w: np.ndarray, h: np.ndarray) -> list[Transition]:
    moving = np.flatnonzero((np.abs(np.diff(w)) > 0.5) | (np.abs(np.diff(h)) > 0.5)) + 1
    if moving.size == 0:
        return []
    groups, current = [], [moving[0]]
    for i in moving[1:]:
        if t[i] - t[current[-1]] > STILL_GAP_MS:
            groups.append(current)
            current = [i]
        else:
            current.append(i)
    groups.append(current)
    result = []
    for g in groups:
        i0, i1 = max(g[0] - 1, 0), g[-1]
        dh, dw = h[i1] - h[i0], w[i1] - w[i0]
        kind = "open" if dh > 8 else "close" if dh < -8 else "resize"
        if abs(dh) < 3 and abs(dw) < 3:
            continue  # jitter, e.g. anti-aliasing of a moving glyph at the edge
        result.append(Transition(kind, i0, i1, t, w, h))
    return result


def overshoot(values: np.ndarray, start: float, end: float) -> float:
    travel = end - start
    if abs(travel) < 1:
        return 0.0
    beyond = (values - end) * np.sign(travel)
    return float(max(beyond.max(), 0.0) / abs(travel) * 100)


def describe(tr: Transition, hz: int) -> dict:
    sl = slice(tr.i0, tr.i1 + 1)
    t, w, h = tr.t[sl], tr.w[sl], tr.h[sl]
    gaps = np.diff(t)
    steps = (np.abs(np.diff(w)) > 0.5) | (np.abs(np.diff(h)) > 0.5)
    period = 1000.0 / hz
    # 90 % of the way there: what the eye reads as "done".
    main = h if abs(h[-1] - h[0]) >= abs(w[-1] - w[0]) else w
    progress = (main - main[0]) / (main[-1] - main[0]) if main[-1] != main[0] else np.ones_like(main)
    reached = np.flatnonzero(progress >= 0.9)
    # While the shape still travels (up to 98 %), every picture should move it. Other things on screen
    # (fading content) keep pictures coming, so a frozen shape shows up as time between progress steps.
    moving_until = np.flatnonzero(progress >= 0.98)
    stop = moving_until[0] if moving_until.size else len(t) - 1
    progress_times = t[1:][steps][: max(int(steps[:stop].sum()), 1)]
    freezes = np.diff(np.concatenate(([t[0]], progress_times)))
    return {
        "kind": tr.kind,
        "from": f"{w[0]:.0f}x{h[0]:.0f}",
        "to": f"{w[-1]:.0f}x{h[-1]:.0f}",
        "duration_ms": round(tr.duration, 1),
        "to_90_percent_ms": round(float(t[reached[0]] - t[0]), 1) if reached.size else None,
        "pictures_with_progress": int(steps.sum()),
        "pictures_shown": int(len(t) - 1),
        "pictures_possible": int(round(tr.duration / period)),
        "longest_freeze_ms": round(float(freezes.max()), 1) if freezes.size else 0.0,
        "freezes_over_2_frames": int((freezes > 2.5 * period).sum()),
        "longest_screen_gap_ms": round(float(gaps.max()), 1) if gaps.size else 0.0,
        "overshoot_width_pct": round(overshoot(w, w[0], w[-1]), 1),
        "overshoot_height_pct": round(overshoot(h, h[0], h[-1]), 1),
    }


# ---------------------------------------------------------------- pictures

def font(size: int):
    try:
        return ImageFont.truetype("segoeui.ttf", size)
    except OSError:
        return ImageFont.load_default()


def draw_curves(transitions: list[Transition], path: Path) -> None:
    rows = len(transitions)
    W, H, pad = 1200, 340, 60
    img = Image.new("RGB", (W, H * rows), (250, 250, 250))
    d = ImageDraw.Draw(img)
    f, small = font(20), font(15)
    for r, tr in enumerate(transitions):
        y0 = r * H
        sl = slice(tr.i0, tr.i1 + 1)
        t = tr.t[sl] - tr.t[tr.i0]
        span = max(t[-1], 1.0)
        span_ms = max(100.0, np.ceil(span / 100) * 100)
        px = lambda ms: pad + (W - 2 * pad) * ms / span_ms
        py = lambda p: y0 + H - pad - (H - 2 * pad) * p
        for ms in np.arange(0, span_ms + 1, 50):
            d.line([(px(ms), y0 + pad), (px(ms), y0 + H - pad)], fill=(228, 228, 228) if ms % 100 else (205, 205, 205))
            if ms % 100 == 0:
                d.text((px(ms) - 12, y0 + H - pad + 6), f"{ms:.0f}", fill=(110, 110, 110), font=small)
        for p in (0.0, 1.0):
            d.line([(pad, py(p)), (W - pad, py(p))], fill=(150, 150, 150))
        for values, colour in ((tr.w[sl], (230, 120, 30)), (tr.h[sl], (40, 110, 220))):
            travel = values[-1] - values[0]
            if abs(travel) < 1:
                continue
            prog = (values - values[0]) / travel
            pts = [(px(a), py(b)) for a, b in zip(t, prog)]
            d.line(pts, fill=colour, width=2)
            for x, y in pts:
                d.ellipse([x - 2.5, y - 2.5, x + 2.5, y + 2.5], fill=colour)
        d.text((pad, y0 + 14), f"{tr.kind}  {tr.duration:.0f} ms   orange = width, blue = height, "
                               f"one dot per picture on screen, x axis in ms", fill=(30, 30, 30), font=f)
    img.save(path)


def draw_strip(video: Path, transitions: list[Transition], t_all: np.ndarray, centres: np.ndarray,
               frame_index: np.ndarray, path: Path, per_row: int = 6) -> None:
    """frame_index maps a measured picture to its position in the video (blank pictures are skipped)."""
    wanted: dict[int, list[tuple[int, int, float]]] = {}
    boxes = []
    for k, tr in enumerate(transitions):
        targets = np.linspace(tr.t[tr.i0], tr.t[tr.i1], per_row * 2)
        idx = [int(np.argmin(np.abs(t_all - x))) for x in targets]
        for slot, i in enumerate(idx):
            wanted.setdefault(int(frame_index[i]), []).append((k, slot, float(t_all[i] - tr.t[tr.i0])))
        sl = slice(tr.i0, tr.i1 + 1)
        seen = centres[sl][centres[sl] > 0]
        cx = float(np.median(seen)) if seen.size else CAPTURE_W / 2
        half = min(CAPTURE_W / 2, tr.w[sl].max() / 2 + 40)
        boxes.append((int(max(0, cx - half)), 0, int(min(CAPTURE_W, cx + half)), int(min(CAPTURE_H, tr.h[sl].max() + 30))))
    tiles: dict[tuple[int, int], tuple[Image.Image, float]] = {}
    for i, frame in enumerate(decode(video, "rgb24", 3)):
        for k, slot, ms in wanted.get(i, []):
            tiles[(k, slot)] = (Image.fromarray(frame).crop(boxes[k]), ms)
    if not tiles:
        return
    scale = 0.5
    tile_w = max(int((b[2] - b[0]) * scale) for b in boxes)
    tile_h = max(int((b[3] - b[1]) * scale) for b in boxes) + 24
    rows = len(transitions) * 2
    sheet = Image.new("RGB", (tile_w * per_row, tile_h * rows), (255, 255, 255))
    d = ImageDraw.Draw(sheet)
    f = font(16)
    for (k, slot), (tile, ms) in tiles.items():
        tile = tile.resize((int(tile.width * scale), int(tile.height * scale)))
        x, y = (slot % per_row) * tile_w, (k * 2 + slot // per_row) * tile_h
        sheet.paste(tile, (x, y + 22))
        d.text((x + 6, y + 2), f"{transitions[k].kind} +{ms:.0f} ms", fill=(40, 40, 40), font=f)
    sheet.save(path)


def analyze(out: Path) -> None:
    global CAPTURE_W, CAPTURE_H
    video = out / "capture.mkv"
    meta = json.loads((out / "meta.json").read_text(encoding="utf-8")) if (out / "meta.json").exists() else {}
    if "capture_size" in meta:
        CAPTURE_W, CAPTURE_H = meta["capture_size"]
    hz = int(meta.get("refresh_hz") or refresh_hz())
    t = frame_times(video)
    rows, dark_max, previous = [], None, None
    for g in decode(video, "gray", 1):
        if dark_max is None:  # the corners show the backdrop, or whatever app was open
            dark_max = DARK_ON_WHITE if g[4:40, 4:60].mean() > 150 else DARK_ON_DARK
        # ddagrab repeats the last picture when nothing new was presented; those are not frames.
        fresh = previous is None or not np.array_equal(g, previous)
        previous = g
        rows.append(measure(g, dark_max) if fresh else (0.0, 0.0, 0.0))
    n = min(len(t), len(rows))
    valid = np.array([r[0] > 0 for r in rows[:n]])
    t = t[:n][valid]
    t = t - t[0]
    w = np.array([r[0] for r in rows[:n]])[valid]
    h = np.array([r[1] for r in rows[:n]])[valid]
    centres = np.array([r[2] for r in rows[:n]])[valid]
    frame_index = np.flatnonzero(valid)

    with (out / "frames.csv").open("w", newline="") as fh:
        writer = csv.writer(fh)
        writer.writerow(["t_ms", "width_px", "height_px", "centre_x_px"])
        for row in zip(t.round(2), w, h, centres):
            writer.writerow(row)

    transitions = find_transitions(t, w, h)
    lines = [f"Recording: {len(t)} new pictures in {t[-1] / 1000:.2f} s, display {hz} Hz "
             f"(one picture every {1000 / hz:.1f} ms)"]
    if meta.get("cpu_core_percent") is not None:
        lines.append(f"V-Notch CPU while filming: {meta['cpu_core_percent']} % of one core")
    if not transitions:
        lines.append("No notch transition found.")
    for tr in transitions:
        info = describe(tr, hz)
        lines.append("")
        lines.append(f"{info['kind'].upper()}  {info['from']} -> {info['to']} px")
        lines.append(f"  duration {info['duration_ms']} ms, 90 % reached after {info['to_90_percent_ms']} ms")
        lines.append(f"  {info['pictures_shown']} new pictures of {info['pictures_possible']} possible "
                     f"({info['pictures_shown'] * 1000 / max(info['duration_ms'], 1):.0f} fps), "
                     f"{info['pictures_with_progress']} of them moved the shape")
        lines.append(f"  shape froze for up to {info['longest_freeze_ms']} ms; freezes longer than 2 frames: "
                     f"{info['freezes_over_2_frames']} (longest gap between any two pictures: "
                     f"{info['longest_screen_gap_ms']} ms)")
        lines.append(f"  overshoot width {info['overshoot_width_pct']} %, height {info['overshoot_height_pct']} %")
    report = "\n".join(lines)
    (out / "report.txt").write_text(report + "\n", encoding="utf-8")
    print(report)
    if transitions:
        draw_curves(transitions, out / "curve.png")
        draw_strip(video, transitions, t, centres, frame_index, out / "strip.png")
    print(f"\nFiles in {out}")


def main() -> None:
    global CAPTURE_W, CAPTURE_H
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--action", choices=["cycle", "interrupt", "none"], default="cycle")
    parser.add_argument("--interrupt-ms", type=int, default=120, help="for --action interrupt: delay of the second click")
    parser.add_argument("--cycles", type=int, default=1)
    parser.add_argument("--seconds", type=float, default=4.0, help="filming time for --action none")
    parser.add_argument("--no-backdrop", action="store_true", help="film over whatever is open")
    parser.add_argument("--capture", default=f"{CAPTURE_W}x{CAPTURE_H}", metavar="WxH",
                        help="filmed area at the top centre, physical px (smaller keeps up at high refresh rates)")
    parser.add_argument("--out", type=Path, help="output folder (default: %%TEMP%%/notch-motion/<time>)")
    parser.add_argument("--analyze", type=Path, metavar="DIR", help="only measure an existing capture")
    args = parser.parse_args()

    make_dpi_aware()
    CAPTURE_W, CAPTURE_H = (int(v) for v in args.capture.lower().split("x"))
    if args.analyze:
        analyze(args.analyze)
        return
    out = args.out or Path(os.environ.get("TEMP", ".")) / "notch-motion" / time.strftime("%Y%m%d-%H%M%S")
    out.mkdir(parents=True, exist_ok=True)
    record(out, args.action, args.cycles, args.seconds, backdrop=not args.no_backdrop, interrupt_ms=args.interrupt_ms)
    analyze(out)


if __name__ == "__main__":
    main()
