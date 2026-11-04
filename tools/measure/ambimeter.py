#!/usr/bin/env python3
"""AmbiSync-Messwerkzeug: misst aus einem Handy-Video, wie gut das Licht dem Bild folgt.

Aufnahme: Handy ruhig hinstellen (oder anlehnen), Fernseher und die beleuchtete Wand drumherum im Bild,
10-60 s mit Szenenwechseln, Belichtung möglichst fest (AE/AF-Sperre: lange auf den Bildschirm tippen).

Aufruf:
    python3 ambimeter.py VIDEO --screen "x1,y1 x2,y2 x3,y3 x4,y4" [--out ordner] [--label 1.2.5]
    python3 ambimeter.py VIDEO --preview          # schreibt ein Bild mit Raster, um die Ecken abzulesen

Die Ecken sind die vier Ecken des leuchtenden Bildes (nicht des Rahmens) im Video, in Pixeln der
aufrecht gedrehten Aufnahme, Reihenfolge: oben links, oben rechts, unten rechts, unten links.

Ergebnis (JSON + Markdown + Grafik):
    Verzögerung      Versatz Bildrand -> Wandlicht mit der besten Übereinstimmung (ms)
    Schnitt 50/90 %  wie lange das Wandlicht nach einem harten Schnitt braucht, bis es 50/90 % der neuen
                     Farbe erreicht hat (ms, Median über alle Schnitte)
    Stufigkeit       größter Anteil der Änderung nach einem Schnitt, der in 50 ms passiert
                     (1.0 = harter Sprung, ~0.3 = weiches Gleiten über 160 ms)
    Unruhe           Zittern der Wandfarbe, während das Bild ruhig ist (kleiner = ruhiger)
    Farbfolge        wie gut die Farbwechsel an der Wand denen am Bildrand folgen (Korrelation, 1 = perfekt)

Kamera-Belichtung: Alle Farbmaße arbeiten mit Farbanteilen (r, g, b durch Summe), Helligkeit nur als
Verhältnis Wand/Bild - so stört die automatische Belichtung des Handys kaum.
"""
import argparse, json, os, subprocess, sys
import numpy as np

SIDES = ["oben", "rechts", "unten", "links"]


def probe(path):
    out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
                          "stream=width,height:stream_side_data=rotation:format=duration", "-of", "json", path],
                         capture_output=True, text=True).stdout
    j = json.loads(out)
    st = j["streams"][0]
    w, h = st["width"], st["height"]
    rot = 0
    for sd in st.get("side_data_list", []) or []:
        if "rotation" in sd:
            rot = int(sd["rotation"])
    if abs(rot) % 180 == 90:
        w, h = h, w
    return w, h, float(j["format"]["duration"])


def bilerp(q, u, v):
    """Punkt im Viereck q (TL, TR, BR, BL) bei u (0..1 links->rechts), v (0..1 oben->unten)."""
    tl, tr, br, bl = q
    top = tl + (tr - tl) * u
    bot = bl + (br - bl) * u
    return top + (bot - top) * v


def poly_mask(W, H, pts):
    """Maske für ein Polygon (ganzzahlig, ohne zusätzliche Bibliotheken)."""
    yy, xx = np.mgrid[0:H, 0:W]
    inside = np.zeros((H, W), bool)
    n = len(pts)
    j = n - 1
    for i in range(n):
        xi, yi = pts[i]; xj, yj = pts[j]
        cond = ((yi > yy) != (yj > yy)) & (xx < (xj - xi) * (yy - yi) / ((yj - yi) + 1e-9) + xi)
        inside ^= cond
        j = i
    return inside


def regions(q, W, H, band=0.07, wall0=0.06, wall1=0.30):
    """Je Seite: Rand-Streifen im Bild und Wand-Streifen außerhalb, als Masken."""
    q = np.asarray(q, float)
    m = {}
    # Innenstreifen
    inner = {
        "oben": [(0.1, 0.0), (0.9, 0.0), (0.9, band), (0.1, band)],
        "unten": [(0.1, 1 - band), (0.9, 1 - band), (0.9, 1.0), (0.1, 1.0)],
        "links": [(0.0, 0.1), (band, 0.1), (band, 0.9), (0.0, 0.9)],
        "rechts": [(1 - band, 0.1), (1.0, 0.1), (1.0, 0.9), (1 - band, 0.9)],
    }
    outer = {
        "oben": [(0.1, -wall1), (0.9, -wall1), (0.9, -wall0), (0.1, -wall0)],
        "unten": [(0.1, 1 + wall0), (0.9, 1 + wall0), (0.9, 1 + wall1), (0.1, 1 + wall1)],
        "links": [(-wall1, 0.1), (-wall0, 0.1), (-wall0, 0.9), (-wall1, 0.9)],
        "rechts": [(1 + wall0, 0.1), (1 + wall1, 0.1), (1 + wall1, 0.9), (1 + wall0, 0.9)],
    }
    for s in SIDES:
        m["s_" + s] = poly_mask(W, H, [bilerp(q, u, v) for u, v in inner[s]])
        m["w_" + s] = poly_mask(W, H, [bilerp(q, u, v) for u, v in outer[s]])
    return m


def extract(path, q, scale_w=480):
    W0, H0, dur = probe(path)
    sc = scale_w / W0
    W, H = scale_w, int(round(H0 * sc / 2) * 2)
    qs = np.asarray(q, float) * [scale_w / W0, H / H0]
    masks = regions(qs, W, H)
    usable = {k: v for k, v in masks.items() if v.sum() >= 20}
    idx = {k: np.nonzero(v.ravel())[0] for k, v in usable.items()}
    p = subprocess.Popen(["ffmpeg", "-v", "error", "-i", path, "-vf", f"scale={W}:{H}", "-f", "rawvideo",
                          "-pix_fmt", "rgb24", "-"], stdout=subprocess.PIPE)
    sz = W * H * 3
    series = {k: [] for k in idx}
    n = 0
    while True:
        b = p.stdout.read(sz)
        if len(b) < sz:
            break
        a = np.frombuffer(b, np.uint8).reshape(-1, 3).astype(np.float32)
        for k, ii in idx.items():
            series[k].append(a[ii].mean(0))
        n += 1
    fps = n / dur if dur > 0 else 30.0
    return {k: np.array(v) for k, v in series.items()}, fps, (W, H), masks


def chroma(x):
    s = x.sum(1, keepdims=True) + 1e-6
    return x / s


def analyse(series, fps):
    res = {"fps_video": round(fps, 1), "seiten": {}}
    sides = [s for s in SIDES if "s_" + s in series and "w_" + s in series]
    # --- Farbfolge + Verzögerung: Korrelation der (geglätteten, von Drift befreiten) Farbanteile ---
    def prep(v):
        k = max(3, int(fps * 3)); v = v - np.convolve(v, np.ones(k) / k, "same")
        k2 = max(1, int(fps * 0.1)); return np.convolve(v, np.ones(k2) / k2, "same")
    lags, rs = [], []
    for s in sides:
        a = chroma(series["s_" + s]); b = chroma(series["w_" + s])
        best = None
        for ch in (0, 2):
            x, y = prep(a[:, ch]), prep(b[:, ch])
            for L in range(0, int(fps * 1.5)):
                c = np.corrcoef(x[:len(x) - L], y[L:])[0, 1]
                if best is None or c > best[1]:
                    best = (L, float(c))
        res["seiten"][s] = {"verzoegerung_ms": round(best[0] * 1000 / fps), "farbfolge": round(best[1], 2)}
        rs.append(best[1])
        if best[1] > 0.3:
            lags.append(best[0] * 1000 / fps)
    res["verzoegerung_ms"] = round(float(np.median(lags))) if lags else None
    res["farbfolge"] = round(float(np.mean(rs)), 2) if rs else None

    # --- Schnitte ---
    scr = np.hstack([chroma(series["s_" + s]) for s in sides])
    wal = np.hstack([chroma(series["w_" + s]) for s in sides])
    ds = np.linalg.norm(np.diff(scr, axis=0), axis=1)
    thr = max(np.percentile(ds, 99.0), 6 * np.median(ds) + 1e-4)
    cand = np.where(ds > thr)[0] + 1
    gap = int(fps * 1.2)
    cuts = []
    for c in cand:
        if not cuts or c - cuts[-1] > gap:
            cuts.append(int(c))
    pre_n, post0, post1 = int(fps * 0.3), int(fps * 1.6), int(fps * 2.0)
    t50s, t90s, steps, per_cut = [], [], [], []
    for c in cuts:
        if c - pre_n < 0 or c + post1 >= len(wal):
            continue
        # das Bild muss nach dem Schnitt ruhig bleiben, sonst ist das Ziel unklar
        if np.linalg.norm(scr[c + post0:c + post1].std(0)) > 0.02:
            continue
        pre = wal[c - pre_n:c - 1].mean(0); post = wal[c + post0:c + post1].mean(0)
        tot = np.linalg.norm(post - pre)
        if tot < 0.012:
            continue
        prog = np.array([np.dot(wal[c + k] - pre, post - pre) / tot ** 2 for k in range(post1)])
        f = lambda th: next((k for k, p in enumerate(prog) if p >= th), None)
        k50, k90 = f(0.5), f(0.9)
        if k50 is None or k90 is None:
            continue
        # Stufigkeit unabhängig von der Videobildrate: größter Anteil der Änderung innerhalb von 50 ms
        w = max(1, int(round(fps * 0.05)))
        seg = np.clip(prog[:k90 + w + 1], 0, 1)
        step = float(max((seg[i + w] - seg[i]) for i in range(len(seg) - w))) if len(seg) > w else 1.0
        t50s.append(k50 * 1000 / fps); t90s.append(k90 * 1000 / fps); steps.append(step)
        per_cut.append({"zeit_s": round(c / fps, 2), "t50_ms": round(k50 * 1000 / fps), "t90_ms": round(k90 * 1000 / fps),
                        "groesster_sprung": round(step, 2)})
    res["schnitte"] = per_cut
    res["schnitt_t50_ms"] = round(float(np.median(t50s))) if t50s else None
    res["schnitt_t90_ms"] = round(float(np.median(t90s))) if t90s else None
    # Stufigkeit relativ zur Videobildrate: bei 120 Bildern/s ist ein weiches Gleiten über 160 ms
    # ~0.05 je Bild; ein harter Sprung bleibt nahe 1.
    res["stufigkeit"] = round(float(np.median(steps)), 2) if steps else None   # 1 = Sprung, ~0.3 = 160 ms Gleiten

    # --- Unruhe in ruhigen Bildphasen ---
    win = max(3, int(fps * 0.5))
    calm = []
    for i in range(0, len(scr) - win, win):
        if np.linalg.norm(scr[i:i + win].std(0)) < 0.004:
            w = wal[i:i + win]
            calm.append(float(np.linalg.norm(np.diff(w, axis=0), axis=1).mean()))
    res["unruhe"] = round(float(np.median(calm)) * 1000, 2) if calm else None

    return res


def plot(series, fps, out_png, res):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    sides = [s for s in SIDES if "s_" + s in series]
    fig, ax = plt.subplots(len(sides), 1, figsize=(13, 2.4 * len(sides)), sharex=True)
    if len(sides) == 1:
        ax = [ax]
    t = np.arange(len(series["s_" + sides[0]])) / fps
    for i, s in enumerate(sides):
        a = chroma(series["s_" + s]); b = chroma(series["w_" + s])
        for ch, col in ((0, "#d33"), (2, "#36c")):
            ax[i].plot(t, a[:, ch], color=col, lw=1, label=f"Bild {s} {'rot' if ch == 0 else 'blau'}")
            ax[i].plot(t, b[:, ch], color=col, lw=1, ls="--", label=f"Wand {s}")
        for c in res.get("schnitte", []):
            ax[i].axvline(c["zeit_s"], color="#999", lw=0.6)
        ax[i].legend(fontsize=7, ncol=4, loc="upper right")
        ax[i].set_ylabel("Farbanteil")
    ax[-1].set_xlabel("Sekunden")
    plt.tight_layout()
    plt.savefig(out_png, dpi=80)


def preview(path, out):
    W, H, _ = probe(path)
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-ss", "2", "-i", path, "-frames:v", "1",
                    "-vf", "drawgrid=w=iw/20:h=ih/20:t=1:c=yellow@0.5", out], check=True)
    print(f"{out} geschrieben ({W}x{H}). Rasterlinien alle {W // 20} px waagerecht, {H // 20} px senkrecht.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("video")
    ap.add_argument("--screen", help='"x1,y1 x2,y2 x3,y3 x4,y4" (TL TR BR BL) in Pixeln der Aufnahme')
    ap.add_argument("--out", default="messung")
    ap.add_argument("--label", default="")
    ap.add_argument("--preview", action="store_true")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    base = os.path.join(a.out, os.path.splitext(os.path.basename(a.video))[0])
    if a.preview:
        preview(a.video, base + "_raster.jpg"); return
    if not a.screen:
        sys.exit("--screen fehlt (erst --preview, dann Ecken ablesen)")
    q = [tuple(float(v) for v in p.split(",")) for p in a.screen.split()]
    series, fps, _, _ = extract(a.video, q)
    res = analyse(series, fps)
    res["video"] = os.path.basename(a.video); res["version"] = a.label
    json.dump(res, open(base + ".json", "w"), indent=1, ensure_ascii=False)
    plot(series, fps, base + ".png", res)
    md = [f"## {res['video']} {('(' + a.label + ')') if a.label else ''}", "",
          "| Maß | Wert |", "|---|---|",
          f"| Verzögerung (Korrelation) | {res['verzoegerung_ms']} ms |",
          f"| Schnitt → 50 % | {res['schnitt_t50_ms']} ms |",
          f"| Schnitt → 90 % | {res['schnitt_t90_ms']} ms |",
          f"| Stufigkeit (Anteil in 50 ms; 1 = Sprung, 0,3 = weich) | {res['stufigkeit']} |",
          f"| Unruhe bei ruhigem Bild (×1000) | {res['unruhe']} |",
          f"| Farbfolge (Korrelation Bildrand/Wand, 1 = perfekt) | {res['farbfolge']} |",
          f"| ausgewertete Schnitte | {len(res['schnitte'])} |", ""]
    open(base + ".md", "w").write("\n".join(md))
    print("\n".join(md))


if __name__ == "__main__":
    main()
