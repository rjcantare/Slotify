#!/usr/bin/env python3
"""
Slotify coin box interface board: layout, design-rule check and exports.

One script, no dependencies (Python 3.8+). It defines every pad and trace,
checks that each net is fully connected and that different nets keep their
clearance, then writes:

  output/gerbers/*            Gerber RS-274X + Excellon, for JLCPCB/PCBWay etc.
  output/slotify-coin-board-gerbers.zip   the same files zipped for upload
  output/slotify-coin-board-print.pdf     1:1 printable copper + placement guide
  output/preview.svg          top-view preview
  output/bom.csv              parts list
  output/drc-report.txt       result of the checks

Circuit = docs/hardware/production-wiring.md (EL817 coin input on GPIO 18,
2N2222A LED-frame switch on GPIO 19, SET inhibit on GPIO 23 with R8 pulldown).
The ESP32 and the LM2596 module stay off-board and connect by wire.

Run:  python generate.py
"""
import csv
import math
import os
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "output")
GERB = os.path.join(OUT, "gerbers")
NAME = "slotify-coin-board"
REV = "V1"

# --------------------------------------------------------------- board ----
W, H = 80.0, 64.0                      # mm, origin bottom-left, y up
HOLES = [(3.5, 3.5), (76.5, 3.5), (3.5, 60.5), (76.5, 60.5)]   # M3 mounting
HOLE_D = 3.2
HOLE_KEEPOUT = 3.3                    # copper keeps this far from hole centre (screw/nut)
CLEAR = 0.30                          # copper-to-copper clearance
EDGE_CLEAR = 0.5
MASK_EXP = 0.10                       # solder-mask opening grows by this per side

pads = []        # dict(ref, pin, x, y, size, drill, shape, net)
traces = []      # dict(net, layer, width, pts)
silk = []        # polylines: list of (x, y) lists, width 0.15
silk_w = []      # matching widths
parts = []       # BOM rows


def pad(ref, pin, x, y, size, drill, net, shape="C"):
    pads.append(dict(ref=ref, pin=pin, x=x, y=y, size=size, drill=drill, shape=shape, net=net))


def trace(net, width, *pts, layer="B"):
    traces.append(dict(net=net, layer=layer, width=width, pts=list(pts)))


def sline(*pts, w=0.15):
    silk.append(list(pts))
    silk_w.append(w)


def srect(x0, y0, x1, y1, w=0.15):
    sline((x0, y0), (x1, y0), (x1, y1), (x0, y1), (x0, y0), w=w)


def scircle(cx, cy, r, w=0.15, a0=0, a1=360, n=40):
    pts = []
    for i in range(n + 1):
        a = math.radians(a0 + (a1 - a0) * i / n)
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    sline(*pts, w=w)


# ------------------------------------------------------- stroke font ------
# glyphs on a 4 wide x 6 tall grid
_O = [[(1, 0), (0, 1), (0, 5), (1, 6), (3, 6), (4, 5), (4, 1), (3, 0), (1, 0)]]
_P = [[(0, 0), (0, 6), (3, 6), (4, 5), (4, 4), (3, 3), (0, 3)]]
FONT = {
    "0": [[(0, 0), (4, 0), (4, 6), (0, 6), (0, 0)], [(0, 0), (4, 6)]],
    "1": [[(1, 5), (2, 6), (2, 0)], [(1, 0), (3, 0)]],
    "2": [[(0, 5), (1, 6), (3, 6), (4, 5), (4, 4), (0, 0), (4, 0)]],
    "3": [[(0, 6), (4, 6), (2, 3.5), (3, 3.5), (4, 2.5), (4, 1), (3, 0), (1, 0), (0, 1)]],
    "4": [[(3, 0), (3, 6), (0, 2), (4, 2)]],
    "5": [[(4, 6), (0, 6), (0, 3.5), (3, 3.5), (4, 2.5), (4, 1), (3, 0), (0, 0)]],
    "6": [[(4, 6), (1, 6), (0, 5), (0, 1), (1, 0), (3, 0), (4, 1), (4, 2.5), (3, 3.5), (0, 3.5)]],
    "7": [[(0, 6), (4, 6), (1, 0)]],
    "8": [[(1, 3.5), (0, 4.5), (0, 5), (1, 6), (3, 6), (4, 5), (4, 4.5), (3, 3.5), (1, 3.5),
           (0, 2.5), (0, 1), (1, 0), (3, 0), (4, 1), (4, 2.5), (3, 3.5)]],
    "9": [[(0, 0), (3, 0), (4, 1), (4, 5), (3, 6), (1, 6), (0, 5), (0, 3.5), (1, 2.5), (4, 2.5)]],
    "A": [[(0, 0), (0, 4), (2, 6), (4, 4), (4, 0)], [(0, 2.5), (4, 2.5)]],
    "B": [[(0, 0), (0, 6), (3, 6), (4, 5), (4, 4), (3, 3), (0, 3)], [(3, 3), (4, 2), (4, 1), (3, 0), (0, 0)]],
    "C": [[(4, 5), (3, 6), (1, 6), (0, 5), (0, 1), (1, 0), (3, 0), (4, 1)]],
    "D": [[(0, 0), (0, 6), (2.5, 6), (4, 4.5), (4, 1.5), (2.5, 0), (0, 0)]],
    "E": [[(4, 6), (0, 6), (0, 0), (4, 0)], [(0, 3), (3, 3)]],
    "F": [[(4, 6), (0, 6), (0, 0)], [(0, 3), (3, 3)]],
    "G": [[(4, 5), (3, 6), (1, 6), (0, 5), (0, 1), (1, 0), (3, 0), (4, 1), (4, 3), (2, 3)]],
    "H": [[(0, 0), (0, 6)], [(4, 0), (4, 6)], [(0, 3), (4, 3)]],
    "I": [[(1, 6), (3, 6)], [(2, 6), (2, 0)], [(1, 0), (3, 0)]],
    "J": [[(4, 6), (4, 1), (3, 0), (1, 0), (0, 1)]],
    "K": [[(0, 0), (0, 6)], [(4, 6), (0, 2)], [(1.5, 3.5), (4, 0)]],
    "L": [[(0, 6), (0, 0), (4, 0)]],
    "M": [[(0, 0), (0, 6), (2, 3), (4, 6), (4, 0)]],
    "N": [[(0, 0), (0, 6), (4, 0), (4, 6)]],
    "O": _O,
    "P": _P,
    "Q": _O + [[(2.5, 1.5), (4, 0)]],
    "R": _P + [[(2, 3), (4, 0)]],
    "S": [[(4, 5), (3, 6), (1, 6), (0, 5), (0, 4), (1, 3), (3, 3), (4, 2), (4, 1), (3, 0), (1, 0), (0, 1)]],
    "T": [[(0, 6), (4, 6)], [(2, 6), (2, 0)]],
    "U": [[(0, 6), (0, 1), (1, 0), (3, 0), (4, 1), (4, 6)]],
    "V": [[(0, 6), (2, 0), (4, 6)]],
    "W": [[(0, 6), (1, 0), (2, 3), (3, 0), (4, 6)]],
    "X": [[(0, 0), (4, 6)], [(0, 6), (4, 0)]],
    "Y": [[(0, 6), (2, 3), (4, 6)], [(2, 3), (2, 0)]],
    "Z": [[(0, 6), (4, 6), (0, 0), (4, 0)]],
    "+": [[(0, 3), (4, 3)], [(2, 1), (2, 5)]],
    "-": [[(0.5, 3), (3.5, 3)]],
    ".": [[(1.8, 0), (2.2, 0), (2.2, 0.4), (1.8, 0.4), (1.8, 0)]],
    "/": [[(0, 0), (4, 6)]],
    ":": [[(2, 1), (2, 1.4)], [(2, 4), (2, 4.4)]],
    "(": [[(3, 6), (2, 5), (2, 1), (3, 0)]],
    ")": [[(1, 6), (2, 5), (2, 1), (1, 0)]],
    "=": [[(0, 2), (4, 2)], [(0, 4), (4, 4)]],
    " ": [],
}


def text_width(s, h):
    return len(s) * 5.5 * h / 6 - 1.5 * h / 6


def text(s, x, y, h=1.0, anchor="start", w=0.15, rot=0):
    """Silkscreen text; (x, y) is the baseline-left (start), centre or right (end).
    rot=90 writes upwards (reads bottom to top)."""
    k = h / 6.0
    tw = text_width(s, h)
    off = {"middle": tw / 2, "end": tw}.get(anchor, 0.0)
    for i, ch in enumerate(s.upper()):
        for stroke in FONT[ch]:
            pts = []
            for px, py in stroke:
                dx, dy = (i * 5.5 + px) * k - off, py * k
                pts.append((x + dx, y + dy) if rot == 0 else (x - dy, y + dx))
            sline(*pts, w=w)


# ---------------------------------------------------------- footprints ----
TB = dict(size=2.4, drill=1.2)        # 5.08 mm screw terminal
HDR = dict(size=1.7, drill=1.0)       # 2.54 mm pin header
RES = dict(size=1.8, drill=0.9)       # axial resistor, 10.16 mm pitch
DIP = dict(size=1.6, drill=0.8)
T92 = dict(size=1.5, drill=0.8)
VIA = dict(size=1.6, drill=0.8)


def terminal_row(ref, x0, y, nets, labels, title, value):
    for i, (n, lab) in enumerate(zip(nets, labels)):
        x = x0 + i * 5.08
        pad(ref, str(i + 1), x, y, TB["size"], TB["drill"], n, "R" if i == 0 else "C")
        text(lab, x, y + 4.3, h=1.0, anchor="middle")
    x1 = x0 + (len(nets) - 1) * 5.08
    srect(x0 - 2.54, y - 3.8, x1 + 2.54, y + 3.6)
    text(title, (x0 + x1) / 2, y - 3.1, h=1.0, anchor="middle")
    parts.append((ref, value, f"Screw terminal 5.08 mm, {len(nets)}-pin", title))


def header_row(ref, x0, y, nets, labels, title, value):
    for i, (n, lab) in enumerate(zip(nets, labels)):
        x = x0 + i * 2.54
        pad(ref, str(i + 1), x, y, HDR["size"], HDR["drill"], n, "R" if i == 0 else "C")
        text(lab, x + 0.4, y + 1.7, h=0.8, anchor="start", w=0.13, rot=90)
    x1 = x0 + (len(nets) - 1) * 2.54
    srect(x0 - 1.27, y - 1.27, x1 + 1.27, y + 1.27)
    parts.append((ref, value, f"Pin header 2.54 mm, 1x{len(nets)}", title))


def header_col(ref, x, y0, nets, labels, title, value):
    """Vertical header, pin 1 at top."""
    for i, (n, lab) in enumerate(zip(nets, labels)):
        y = y0 - i * 2.54
        pad(ref, str(i + 1), x, y, HDR["size"], HDR["drill"], n, "R" if i == 0 else "C")
        text(lab, x + 1.6, y - 0.45, h=0.9, anchor="start", w=0.13)
    y1 = y0 - (len(nets) - 1) * 2.54
    srect(x - 1.27, y1 - 1.27, x + 1.27, y0 + 1.27)
    parts.append((ref, value, f"Pin header 2.54 mm, 1x{len(nets)}", title))


def resistor(ref, value, a, b, net_a, net_b, label_at=None, anchor="start"):
    pad(ref, "1", a[0], a[1], RES["size"], RES["drill"], net_a)
    pad(ref, "2", b[0], b[1], RES["size"], RES["drill"], net_b)
    cx, cy = (a[0] + b[0]) / 2, (a[1] + b[1]) / 2
    if abs(a[1] - b[1]) < 1e-6:          # horizontal
        srect(cx - 3.0, cy - 1.1, cx + 3.0, cy + 1.1)
        sline((a[0] + 1.1, cy), (cx - 3.0, cy))
        sline((cx + 3.0, cy), (b[0] - 1.1, cy))
    else:                                # vertical
        lo, hi = min(a[1], b[1]), max(a[1], b[1])
        srect(cx - 1.1, cy - 3.0, cx + 1.1, cy + 3.0)
        sline((cx, lo + 1.1), (cx, cy - 3.0))
        sline((cx, cy + 3.0), (cx, hi - 1.1))
    if label_at:
        text(f"{ref} {value}", label_at[0], label_at[1], h=1.0, anchor=anchor)
    parts.append((ref, value, "Resistor 1/4 W axial", ""))


def via(net, x, y):
    pad("LINK", "", x, y, VIA["size"], VIA["drill"], net)


# --------------------------------------------------------------- nets -----
V12, GND, V5, V33 = "+12V", "GND", "+5V", "+3V3"
COIN, OPTA, OPTO, D18, D19, D23, BASE, FRM = "COIN", "OPTO_A", "OPTO_OUT", "D18", "D19", "D23_SET", "Q1_BASE", "FRAME-"
NC = "NC"

# ------------------------------------------------------------ placement ---
Y_T = 8.0
terminal_row("J6", 8.0, Y_T, [V5, GND, GND, V12], ["OUT+", "OUT-", "IN-", "IN+"], "J6 LM2596", "4-pin")
terminal_row("J1", 31.0, Y_T, [GND, V12], ["GND", "+12V"], "J1 12V IN", "2-pin")
terminal_row("J2", 44.0, Y_T, [GND, V12, COIN, NC], ["BLK", "RED", "WHT", "GRY"], "J2 ACCEPTOR", "4-pin")
terminal_row("J4", 66.0, Y_T, [V12, FRM], ["RED", "BLK"], "J4 LED", "2-pin")
header_row("J5", 16.0, 56.0, [V5, GND, V33, D18, D19, D23], ["5V", "GND", "3V3", "D18", "D19", "D23"],
           "J5 to ESP32", "1x6 male")
text("J5 TO ESP32", 22.35, 53.6, h=1.0, anchor="middle")
header_col("J3", 74.0, 52.5, [D23, GND], ["SET", "GND"], "J3 to acceptor SET", "1x2 male")
text("J3", 74.0, 54.4, h=1.0, anchor="middle")

resistor("R1", "10K", (49.08, 15.0), (59.24, 15.0), V12, COIN, label_at=(47.6, 14.5), anchor="end")
resistor("R2", "1K", (49.08, 19.0), (49.08, 29.16), V12, OPTA, label_at=(47.6, 23.6), anchor="end")
resistor("R3", "5.1K", (47.0, 37.0), (57.16, 37.0), V33, OPTO, label_at=(52.08, 38.5), anchor="middle")
resistor("R4", "10K", (47.0, 42.0), (57.16, 42.0), D18, OPTO, label_at=(52.08, 43.5), anchor="middle")
resistor("R6", "1K", (63.5, 40.0), (63.5, 29.84), D19, BASE, label_at=(65.0, 34.5))
resistor("R7", "10K", (68.54, 17.5), (58.38, 17.5), BASE, GND, label_at=(63.46, 14.4), anchor="middle")
resistor("R8", "10K", (70.0, 52.5), (70.0, 42.34), D23, GND, label_at=(71.6, 45.6))

# U1 EL817 (DIP-4): 1 top-left anode, 2 bottom-left cathode, 3 bottom-right emitter, 4 top-right collector
pad("U1", "1", 52.0, 32.0, DIP["size"], DIP["drill"], OPTA, "R")
pad("U1", "2", 52.0, 29.46, DIP["size"], DIP["drill"], COIN)
pad("U1", "3", 59.62, 29.46, DIP["size"], DIP["drill"], GND)
pad("U1", "4", 59.62, 32.0, DIP["size"], DIP["drill"], OPTO)
srect(53.3, 27.6, 58.32, 33.85)
scircle(54.2, 32.9, 0.35)
text("U1 EL817", 55.81, 34.5, h=1.0, anchor="middle")
parts.append(("U1", "EL817 / PC817", "Optocoupler DIP-4", "dot = pin 1 (square pad)"))

# Q1 2N2222A TO-92: E B C left to right, flat side facing down (towards J4)
pad("Q1", "E", 66.0, 22.0, T92["size"], T92["drill"], GND)
pad("Q1", "B", 68.54, 22.0, T92["size"], T92["drill"], BASE)
pad("Q1", "C", 71.08, 22.0, T92["size"], T92["drill"], FRM)
scircle(68.54, 22.0, 2.6, a0=-25, a1=205)
sline((68.54 - 2.6 * math.cos(math.radians(25)), 22.0 - 2.6 * math.sin(math.radians(25))),
      (68.54 + 2.6 * math.cos(math.radians(25)), 22.0 - 2.6 * math.sin(math.radians(25))))
for lab, x in (("E", 66.0), ("B", 68.54), ("C", 71.08)):
    text(lab, x, 19.6, h=0.9, anchor="middle", w=0.13)
text("Q1 2N2222A", 72.6, 15.2, h=1.0, anchor="middle")
parts.append(("Q1", "2N2222A", "NPN transistor TO-92", "flat side towards J4; legs E B C"))

# wire link (top-layer trace on a factory board; a wire on a home-etched board)
via(GND, 61.5, 25.0)
via(GND, 77.5, 25.0)
trace(GND, 1.0, (61.5, 25.0), (77.5, 25.0), layer="T")
for i in range(16):                      # dashed silk line showing the link
    x = 62.8 + i * 0.95
    if i % 2 == 0 and x + 0.6 < 76.2:
        sline((x, 25.0), (x + 0.6, 25.0))
text("LINK", 69.5, 26.0, h=0.9, anchor="middle", w=0.13)

# title
text("SLOTIFY", 23.0, 30.5, h=2.6, w=0.25)
text("COIN BOX " + REV, 23.0, 27.0, h=1.4, w=0.18)
text("GPL-3.0", 23.0, 24.6, h=1.0)
text("PRINT 1:1", 23.0, 22.4, h=0.9, w=0.13)
for hx, hy in HOLES:
    scircle(hx, hy, 2.9)

# -------------------------------------------------------------- routing ---
# +12 V: bus along the bottom edge, spur up through R1 / R2
trace(V12, 1.2, (23.24, 8.0), (23.24, 3.0), (66.0, 3.0), (66.0, 8.0))
trace(V12, 1.2, (36.08, 8.0), (36.08, 3.0))
trace(V12, 1.2, (49.08, 8.0), (49.08, 3.0))
trace(V12, 0.8, (49.08, 8.0), (49.08, 15.0), (49.08, 19.0))
# GND island A (left) + trunk to the ESP32 header
trace(GND, 1.0, (13.08, 8.0), (13.08, 13.5), (44.0, 13.5), (44.0, 8.0))
trace(GND, 1.0, (18.16, 8.0), (18.16, 13.5))
trace(GND, 1.0, (31.0, 8.0), (31.0, 13.5))
trace(GND, 1.0, (18.54, 13.5), (18.54, 56.0))
# GND over the top and down the right edge
trace(GND, 1.0, (18.54, 56.0), (18.54, 61.0), (70.0, 61.0), (77.5, 53.5), (77.5, 25.0))
trace(GND, 1.0, (74.0, 49.96), (77.5, 49.96))
trace(GND, 1.0, (70.0, 42.34), (77.5, 42.34))
# GND island C (U1 emitter, Q1 emitter, R7) - joined by the link
trace(GND, 1.0, (59.62, 29.46), (61.5, 27.6), (61.5, 23.0), (62.5, 22.0), (66.0, 22.0))
trace(GND, 1.0, (58.38, 17.5), (61.5, 20.6), (61.5, 23.0))
# 5 V from the LM2596 to the ESP32 header
trace(V5, 1.0, (8.0, 8.0), (8.0, 53.0), (11.0, 56.0), (16.0, 56.0))
# coin signal
trace(COIN, 0.8, (54.16, 8.0), (54.16, 26.2), (52.0, 28.2), (52.0, 29.46))
trace(COIN, 0.8, (59.24, 15.0), (54.16, 15.0))
# optocoupler LED feed
trace(OPTA, 0.8, (49.08, 29.16), (49.08, 30.5), (50.58, 32.0), (52.0, 32.0))
# optocoupler output to R3 / R4
trace(OPTO, 0.6, (59.62, 32.0), (59.62, 42.0), (57.16, 42.0))
trace(OPTO, 0.6, (59.62, 37.0), (57.16, 37.0))
# 3V3 and D18 from the header
trace(V33, 0.6, (21.08, 56.0), (21.08, 39.0), (44.5, 39.0), (47.0, 37.0))
trace(D18, 0.6, (23.62, 56.0), (23.62, 44.5), (44.5, 44.5), (47.0, 42.0))
# D19 -> R6 -> Q1 base -> R7
trace(D19, 0.6, (26.16, 56.0), (26.16, 49.0), (63.5, 49.0), (63.5, 40.0))
trace(BASE, 0.6, (63.5, 29.84), (63.5, 27.5), (68.54, 27.5), (68.54, 22.0), (68.54, 17.5))
# D23 -> SET header, R8
trace(D23, 0.6, (28.7, 56.0), (28.7, 52.5), (70.0, 52.5), (74.0, 52.5))
# LED frame return -> Q1 collector
trace(FRM, 0.8, (71.08, 8.0), (71.08, 22.0))


# ------------------------------------------------------------- geometry ---
def seg_point_dist(p, a, b):
    ax, ay = a; bx, by = b; px, py = p
    dx, dy = bx - ax, by - ay
    L = dx * dx + dy * dy
    t = 0.0 if L == 0 else max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / L))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def segs_intersect(a, b, c, d):
    def orient(p, q, r):
        v = (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0])
        return 0 if abs(v) < 1e-12 else (1 if v > 0 else -1)
    o1, o2, o3, o4 = orient(a, b, c), orient(a, b, d), orient(c, d, a), orient(c, d, b)
    return o1 != o2 and o3 != o4 and 0 not in (o1, o2, o3, o4)


def seg_seg_dist(a, b, c, d):
    if segs_intersect(a, b, c, d):
        return 0.0
    return min(seg_point_dist(a, c, d), seg_point_dist(b, c, d), seg_point_dist(c, a, b), seg_point_dist(d, a, b))


def pad_radius(p):
    # squares are treated as their circumscribed circle: conservative for clearance
    return p["size"] / 2 * (math.sqrt(2) if p["shape"] == "R" else 1.0)


def segments(t):
    return list(zip(t["pts"][:-1], t["pts"][1:]))


# ------------------------------------------------------------------ DRC ---
def run_checks():
    errors, notes = [], []
    # 1) clearance between different nets, per layer (pads exist on both layers)
    items = []
    for t in traces:
        for a, b in segments(t):
            items.append(("seg", t["net"], t["layer"], a, b, t["width"] / 2))
    for p in pads:
        for layer in ("T", "B"):
            items.append(("pad", p["net"], layer, (p["x"], p["y"]), None, pad_radius(p) if p["shape"] == "R" else p["size"] / 2))
    worst = 99.0
    for i in range(len(items)):
        k1, n1, l1, a1, b1, r1 = items[i]
        for j in range(i + 1, len(items)):
            k2, n2, l2, a2, b2, r2 = items[j]
            if l1 != l2 or n1 == n2:
                continue
            if k1 == "seg" and k2 == "seg":
                d = seg_seg_dist(a1, b1, a2, b2)
            elif k1 == "seg":
                d = seg_point_dist(a2, a1, b1)
            elif k2 == "seg":
                d = seg_point_dist(a1, a2, b2)
            else:
                d = math.hypot(a1[0] - a2[0], a1[1] - a2[1])
            gap = d - r1 - r2
            worst = min(worst, gap)
            if gap < CLEAR - 1e-6:
                errors.append(f"clearance {gap:.2f} mm < {CLEAR} between {n1} and {n2} on layer {l1} near {a1}")
    notes.append(f"smallest copper gap between different nets: {worst:.2f} mm (rule {CLEAR} mm)")

    # 2) board edge and mounting-hole keep-outs
    for kind, net, layer, a, b, r in items:
        for p in ([a] if b is None else [a, b]):
            if min(p[0], p[1], W - p[0], H - p[1]) - r < EDGE_CLEAR - 1e-6:
                errors.append(f"{net} copper too close to board edge at {p}")
        for hx, hy in HOLES:
            d = math.hypot(a[0] - hx, a[1] - hy) if b is None else seg_point_dist((hx, hy), a, b)
            if d - r < HOLE_KEEPOUT - 1e-6:
                errors.append(f"{net} copper inside mounting-hole keep-out near ({hx},{hy})")

    # 3) every trace end lands on a pad or on another trace of the same net, and nets are complete
    parent = {}

    def find(x):
        parent.setdefault(x, x)
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a, b):
        parent[find(a)] = find(b)

    pad_ids = [("P", i) for i in range(len(pads))]
    for ti, t in enumerate(traces):
        for si, (a, b) in enumerate(segments(t)):
            union(("S", ti, si), ("S", ti, 0))
        for end in (t["pts"][0], t["pts"][-1]):
            hit = False
            for pi, p in enumerate(pads):
                if p["net"] == t["net"] and math.hypot(end[0] - p["x"], end[1] - p["y"]) <= p["size"] / 2:
                    union(("S", ti, 0), ("P", pi)); hit = True
            for tj, u in enumerate(traces):
                if tj == ti or u["net"] != t["net"] or u["layer"] != t["layer"]:
                    continue
                for a, b in segments(u):
                    if seg_point_dist(end, a, b) <= u["width"] / 2 + 1e-6:
                        union(("S", ti, 0), ("S", tj, 0)); hit = True
            if not hit:
                errors.append(f"dangling end of {t['net']} trace at {end}")
        # pads that a trace passes straight through
        for pi, p in enumerate(pads):
            if p["net"] != t["net"]:
                continue
            for a, b in segments(t):
                if seg_point_dist((p["x"], p["y"]), a, b) <= p["size"] / 2:
                    union(("S", ti, 0), ("P", pi))
    nets = sorted({p["net"] for p in pads if p["net"] != NC})
    for n in nets:
        groups = {find(pid) for pid, p in zip(pad_ids, pads) if p["net"] == n}
        members = [f"{p['ref']}.{p['pin']}" for p in pads if p["net"] == n]
        if len(groups) != 1:
            errors.append(f"net {n} is split into {len(groups)} pieces: {members}")
        else:
            notes.append(f"net {n:9s} connected: {', '.join(members)}")
    return errors, notes


# --------------------------------------------------------------- gerber ---
def fmt(v):
    return str(int(round(v * 1_000_000)))


class Gerber:
    def __init__(self, function, polarity="Positive"):
        self.function, self.polarity = function, polarity
        self.apertures, self.body = {}, []

    def ap(self, shape, size):
        key = (shape, round(size, 4))
        if key not in self.apertures:
            self.apertures[key] = 10 + len(self.apertures)
        return self.apertures[key]

    def flash(self, shape, size, x, y):
        self.body.append(f"D{self.ap(shape, size)}*")
        self.body.append(f"X{fmt(x)}Y{fmt(y)}D03*")

    def path(self, width, pts):
        self.body.append(f"D{self.ap('C', width)}*")
        self.body.append(f"X{fmt(pts[0][0])}Y{fmt(pts[0][1])}D02*")
        for x, y in pts[1:]:
            self.body.append(f"X{fmt(x)}Y{fmt(y)}D01*")

    def text(self):
        out = [f"%TF.GenerationSoftware,Slotify,generate.py,{REV}*%", f"%TF.FileFunction,{self.function}*%",
               f"%TF.FilePolarity,{self.polarity}*%", "%FSLAX46Y46*%", "%MOMM*%", "%LPD*%"]
        for (shape, size), code in self.apertures.items():
            out.append(f"%ADD{code}C,{size:.4f}*%" if shape == "C" else f"%ADD{code}R,{size:.4f}X{size:.4f}*%")
        out += ["G01*"] + self.body + ["M02*"]
        return "\n".join(out) + "\n"


def write_gerbers():
    os.makedirs(GERB, exist_ok=True)
    files = {}
    for layer, func, ext in (("T", "Copper,L1,Top", "GTL"), ("B", "Copper,L2,Bot", "GBL")):
        g = Gerber(func)
        for p in pads:
            g.flash(p["shape"], p["size"], p["x"], p["y"])
        for t in traces:
            if t["layer"] == layer:
                g.path(t["width"], t["pts"])
        files[f"{NAME}.{ext}"] = g.text()
    for func, ext in (("Soldermask,Top", "GTS"), ("Soldermask,Bot", "GBS")):
        g = Gerber(func, "Negative")
        for p in pads:
            g.flash(p["shape"], p["size"] + 2 * MASK_EXP, p["x"], p["y"])
        files[f"{NAME}.{ext}"] = g.text()
    g = Gerber("Legend,Top")
    for pts, w in zip(silk, silk_w):
        g.path(w, pts)
    files[f"{NAME}.GTO"] = g.text()
    g = Gerber("Profile,NP")
    g.path(0.1, [(0, 0), (W, 0), (W, H), (0, H), (0, 0)])
    files[f"{NAME}.GKO"] = g.text()
    files[f"{NAME}-PTH.DRL"] = excellon({p["drill"]: [] for p in pads}, [(p["drill"], p["x"], p["y"]) for p in pads], True)
    files[f"{NAME}-NPTH.DRL"] = excellon({HOLE_D: []}, [(HOLE_D, x, y) for x, y in HOLES], False)
    for name, body in files.items():
        with open(os.path.join(GERB, name), "w", newline="\n") as f:
            f.write(body)
    with zipfile.ZipFile(os.path.join(OUT, f"{NAME}-gerbers.zip"), "w", zipfile.ZIP_DEFLATED) as z:
        for name in sorted(files):
            z.write(os.path.join(GERB, name), name)
    return sorted(files)


def excellon(tools, holes, plated):
    sizes = sorted(tools)
    out = ["M48", "; Slotify coin box board " + REV, "; " + ("PLATED" if plated else "NON-PLATED") + " through holes",
           "METRIC,TZ"]
    for i, s in enumerate(sizes, 1):
        out.append(f"T{i}C{s:.3f}")
    out += ["%", "G90", "G05"]
    for i, s in enumerate(sizes, 1):
        out.append(f"T{i}")
        for d, x, y in holes:
            if abs(d - s) < 1e-9:
                out.append(f"X{x:.3f}Y{y:.3f}")
    out.append("M30")
    return "\n".join(out) + "\n"


# ------------------------------------------------------------------ PDF ---
PT = 72 / 25.4


class PDF:
    def __init__(self):
        self.pages = []

    def page(self):
        self.ops = []
        self.pages.append(self.ops)
        return self

    def raw(self, s):
        self.ops.append(s)

    def circle(self, x, y, r, fill=True):
        k = 0.5523 * r
        x, y, r, k = x * PT, y * PT, r * PT, k * PT
        self.raw(f"{x + r:.2f} {y:.2f} m {x + r:.2f} {y + k:.2f} {x + k:.2f} {y + r:.2f} {x:.2f} {y + r:.2f} c "
                 f"{x - k:.2f} {y + r:.2f} {x - r:.2f} {y + k:.2f} {x - r:.2f} {y:.2f} c "
                 f"{x - r:.2f} {y - k:.2f} {x - k:.2f} {y - r:.2f} {x:.2f} {y - r:.2f} c "
                 f"{x + k:.2f} {y - r:.2f} {x + r:.2f} {y - k:.2f} {x + r:.2f} {y:.2f} c {'f' if fill else 'S'}")

    def rect(self, x, y, w, h, fill=True):
        self.raw(f"{x * PT:.2f} {y * PT:.2f} {w * PT:.2f} {h * PT:.2f} re {'f' if fill else 'S'}")

    def poly(self, pts, width):
        self.raw(f"{width * PT:.3f} w 1 J 1 j")
        self.raw(" ".join([f"{pts[0][0] * PT:.2f} {pts[0][1] * PT:.2f} m"] +
                          [f"{x * PT:.2f} {y * PT:.2f} l" for x, y in pts[1:]]) + " S")

    def gray(self, g):
        self.raw(f"{g} g {g} G")

    def label(self, s, x, y, size=9, bold=False):
        s = s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
        self.raw(f"BT /{'F2' if bold else 'F1'} {size} Tf {x * PT:.2f} {y * PT:.2f} Td ({s}) Tj ET")

    def save(self, path):
        objs = ["<< /Type /Catalog /Pages 2 0 R >>", None,
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"]
        kids = []
        for ops in self.pages:
            stream = "\n".join(ops).encode("latin-1")
            objs.append(f"<< /Length {len(stream)} >>\nstream\n".encode("latin-1") + stream + b"\nendstream")
            content = len(objs)
            objs.append(f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595.28 841.89] /Contents {content} 0 R "
                        f"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> >>")
            kids.append(len(objs))
        objs[1] = f"<< /Type /Pages /Kids [{' '.join(f'{k} 0 R' for k in kids)}] /Count {len(kids)} >>"
        out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
        offsets = []
        for i, o in enumerate(objs, 1):
            offsets.append(len(out))
            body = o if isinstance(o, bytes) else o.encode("latin-1")
            out += f"{i} 0 obj\n".encode() + body + b"\nendobj\n"
        xref = len(out)
        out += f"xref\n0 {len(objs) + 1}\n0000000000 65535 f \n".encode()
        for off in offsets:
            out += f"{off:010d} 00000 n \n".encode()
        out += f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
        with open(path, "wb") as f:
            f.write(out)


def write_pdf():
    pdf = PDF()
    ox, oy = (210 - W) / 2, 297 - 40 - H - 30          # board origin on the A4 page (mm)

    def at(pts):
        return [(ox + x, oy + y) for x, y in pts]

    def scale_bar(y):
        pdf.gray(0)
        pdf.poly([(ox, y), (ox + 50, y)], 0.4)
        pdf.poly([(ox, y - 1.5), (ox, y + 1.5)], 0.3)
        pdf.poly([(ox + 50, y - 1.5), (ox + 50, y + 1.5)], 0.3)
        pdf.label("this bar must measure exactly 50 mm", ox + 53, y - 1.2, 8)

    # page 1: bottom copper for toner transfer / photo etching
    pdf.page()
    pdf.gray(0)
    pdf.label("Slotify coin box board " + REV + "  -  page 1: BOTTOM COPPER (actual size)", 20, 280, 12, True)
    pdf.label("Print at 100% / 'Actual size' (no 'fit to page'). Check the 50 mm bar below before transferring.", 20, 273, 9)
    pdf.label("Seen from the component side. For toner transfer print it as-is (NOT mirrored).", 20, 268, 9)
    pdf.label("White dots are the drill centres. The two LINK pads need a short wire on the component side.", 20, 263, 9)
    pdf.gray(0)
    pdf.rect(ox, oy, W, H, fill=False)
    for t in traces:
        if t["layer"] == "B":
            pdf.poly(at(t["pts"]), t["width"])
    for p in pads:
        if p["shape"] == "R":
            pdf.rect(ox + p["x"] - p["size"] / 2, oy + p["y"] - p["size"] / 2, p["size"], p["size"])
        else:
            pdf.circle(ox + p["x"], oy + p["y"], p["size"] / 2)
    pdf.gray(1)
    for p in pads:
        pdf.circle(ox + p["x"], oy + p["y"], 0.35)
    pdf.gray(0)
    for hx, hy in HOLES:
        pdf.circle(ox + hx, oy + hy, HOLE_D / 2, fill=False)
    scale_bar(oy - 12)
    y = oy - 26
    pdf.label("Drill sizes:", 20, y, 9, True)
    drills = sorted({p["drill"] for p in pads})
    for i, d in enumerate(drills):
        refs = sorted({p["ref"] for p in pads if p["drill"] == d})
        pdf.label(f"{d:.1f} mm  -  {', '.join(refs)}", 45, y - i * 5, 9)
    pdf.label(f"{HOLE_D:.1f} mm  -  4 mounting holes (corners)", 45, y - len(drills) * 5, 9)

    # page 2: component placement
    pdf.page()
    pdf.gray(0)
    pdf.label("Slotify coin box board " + REV + "  -  page 2: WHERE EACH PART GOES (actual size)", 20, 280, 12, True)
    pdf.label("Top (component) side. Square pad = pin 1. Wire link: solder a short wire between the two LINK pads.", 20, 273, 9)
    pdf.rect(ox, oy, W, H, fill=False)
    pdf.gray(0.75)
    for t in traces:
        pdf.poly(at(t["pts"]), t["width"])
    pdf.gray(0)
    for pts, w in zip(silk, silk_w):
        pdf.poly(at(pts), max(w, 0.18))
    for p in pads:
        if p["shape"] == "R":
            pdf.rect(ox + p["x"] - p["size"] / 2, oy + p["y"] - p["size"] / 2, p["size"], p["size"], fill=False)
        else:
            pdf.circle(ox + p["x"], oy + p["y"], p["size"] / 2, fill=False)
    scale_bar(oy - 12)
    y = oy - 26
    pdf.label("Parts list", 20, y, 10, True)
    for i, (ref, value, kind, note) in enumerate(parts):
        pdf.label(f"{ref:4s}  {value:14s}  {kind}{'  -  ' + note if note else ''}", 20, y - 6 - i * 4.6, 8.5)
    pdf.save(os.path.join(OUT, f"{NAME}-print.pdf"))


# ------------------------------------------------------------------ SVG ---
NET_COLOURS = {V12: "#e0483e", GND: "#8aa0b8", V5: "#ff9d3d", V33: "#5aa8ff", COIN: "#2fd0b8", OPTA: "#e0483e",
               OPTO: "#5aa8ff", D18: "#2fd0b8", D19: "#c58aff", D23: "#55c46e", BASE: "#c58aff", FRM: "#c58aff"}


def write_svg():
    s = 10
    o = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="-4 -4 {W * s + 8} {H * s + 8}" width="{W * s + 8}" height="{H * s + 8}">',
         '<style>text{font:bold 11px system-ui,sans-serif}</style>',
         f'<rect x="0" y="0" width="{W * s}" height="{H * s}" rx="6" fill="#123d2a" stroke="#0b2a1c" stroke-width="3"/>']

    def Y(y):
        return (H - y) * s

    for t in traces:
        pts = " ".join(f"{x * s:.1f},{Y(y):.1f}" for x, y in t["pts"])
        c = NET_COLOURS.get(t["net"], "#c8a24a")
        dash = ' stroke-dasharray="6 4"' if t["layer"] == "T" else ""
        o.append(f'<polyline points="{pts}" fill="none" stroke="{c}" stroke-opacity="{1 if t["layer"] == "T" else .55}" '
                 f'stroke-width="{t["width"] * s:.1f}" stroke-linecap="round" stroke-linejoin="round"{dash}/>')
    for pts, w in zip(silk, silk_w):
        o.append(f'<polyline points="{" ".join(f"{x * s:.1f},{Y(y):.1f}" for x, y in pts)}" fill="none" stroke="#f4f4f0" '
                 f'stroke-width="{max(w, .15) * s:.1f}" stroke-linecap="round" stroke-linejoin="round"/>')
    for p in pads:
        r = p["size"] / 2 * s
        if p["shape"] == "R":
            o.append(f'<rect x="{p["x"] * s - r:.1f}" y="{Y(p["y"]) - r:.1f}" width="{2 * r:.1f}" height="{2 * r:.1f}" fill="#d8b35a"/>')
        else:
            o.append(f'<circle cx="{p["x"] * s:.1f}" cy="{Y(p["y"]):.1f}" r="{r:.1f}" fill="#d8b35a"/>')
        o.append(f'<circle cx="{p["x"] * s:.1f}" cy="{Y(p["y"]):.1f}" r="{p["drill"] / 2 * s:.1f}" fill="#0b1a12"/>')
    for hx, hy in HOLES:
        o.append(f'<circle cx="{hx * s}" cy="{Y(hy)}" r="{HOLE_D / 2 * s}" fill="#0b1a12"/>')
    o.append("</svg>")
    with open(os.path.join(OUT, "preview.svg"), "w", encoding="utf-8") as f:
        f.write("\n".join(o))


def write_bom():
    with open(os.path.join(OUT, "bom.csv"), "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["Ref", "Value", "Part", "Note"])
        for row in parts:
            w.writerow(row)


# ----------------------------------------------------------------- main ---
def main():
    os.makedirs(OUT, exist_ok=True)
    errors, notes = run_checks()
    report = [f"Slotify coin box board {REV}: {W} x {H} mm, {len(pads)} pads, {len(traces)} traces",
              f"top-layer links: {sum(1 for t in traces if t['layer'] == 'T')}", ""] + notes + [""]
    report += [f"ERROR: {e}" for e in errors] or ["All checks passed."]
    with open(os.path.join(OUT, "drc-report.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(report) + "\n")
    print("\n".join(report))
    if errors:
        sys.exit(1)
    files = write_gerbers()
    write_pdf()
    write_svg()
    write_bom()
    print("\nwrote:", ", ".join(files + [f"{NAME}-gerbers.zip", f"{NAME}-print.pdf", "preview.svg", "bom.csv"]))


if __name__ == "__main__":
    main()
