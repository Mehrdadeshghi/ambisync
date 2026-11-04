using System;
using System.Threading;
using TizenAmbilight;

static class UiTests
{
    static int fails = 0;
    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  (" + detail + ")" : ""));
        if (!ok) fails++;
    }
    static PngImage Img(int w, int h, Func<int, int, (byte r, byte g, byte b, byte a)> f)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { var c = f(x, y); int i = (y * w + x) * 4; px[i] = c.r; px[i + 1] = c.g; px[i + 2] = c.b; px[i + 3] = c.a; }
        return new PngImage { Width = w, Height = h, Rgba = px };
    }
    static ZoneColors Video(int n, byte r, byte g, byte b)
    {
        var z = new ZoneColors(n); for (int i = 0; i < n; i++) { z.R[i] = r; z.G[i] = g; z.B[i] = b; } return z;
    }
    static void Main()
    {
        const int W = 640, H = 360, N = 73;
        var layout = new ZoneLayout(W, H, N, 0.08);
        int topEnd = layout.TopCount, rightEnd = topEnd + layout.RightCount, bottomEnd = rightEnd + layout.BottomCount;
        UiOverlay.StaleMs = 0;

        // 1) Alpha: komplett durchsichtig -> Video bleibt
        UiOverlay.Mode = UiOverlay.ModeAuto;
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 0, 0, 0)), layout, UiOverlay.Mode);
        var v = Video(N, 10, 200, 10);
        double cov = UiOverlay.BlendInto(v, true);
        Check("transparente UI laesst Video durch", cov < 0.01 && v.G[5] == 200 && v.R[5] == 10, "cov=" + cov.ToString("0.00"));
        Check("Alpha automatisch erkannt", UiOverlay.AlphaSeen);

        // 2) Alpha: deckendes rotes Vollbild-Menue -> alles rot
        UiOverlay.Analyse(Img(W, H, (x, y) => (220, 0, 0, 255)), layout, UiOverlay.Mode);
        v = Video(N, 10, 200, 10);
        cov = UiOverlay.BlendInto(v, true);
        Check("deckendes Menue ueberdeckt Video", cov > 0.99 && v.R[0] == 220 && v.G[0] == 0, "cov=" + cov.ToString("0.00"));

        // 3) Alpha: blaue Seitenleiste rechts (30 %), Rest durchsichtig
        UiOverlay.Analyse(Img(W, H, (x, y) => x >= W * 0.7 ? ((byte)0, (byte)0, (byte)230, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0)), layout, UiOverlay.Mode);
        v = Video(N, 200, 200, 0);
        UiOverlay.BlendInto(v, true);
        int rightMid = topEnd + layout.RightCount / 2;
        int leftMid = bottomEnd + layout.LeftCount / 2;
        Check("Seitenleiste rechts -> rechte Zonen blau", v.B[rightMid] == 230 && v.R[rightMid] == 0, "rgb=" + v.R[rightMid] + "," + v.G[rightMid] + "," + v.B[rightMid]);
        Check("linke Zonen bleiben Video", v.R[leftMid] == 200 && v.B[leftMid] == 0, "rgb=" + v.R[leftMid] + "," + v.G[leftMid] + "," + v.B[leftMid]);
        Check("obere Zone links bleibt Video, oben rechts blau", v.R[0] == 200 && v.B[topEnd - 1] == 230);

        // 4) Halbdurchsichtiges Overlay (alpha 128) mischt 50/50
        UiOverlay.Analyse(Img(W, H, (x, y) => (255, 255, 255, 128)), layout, UiOverlay.Mode);
        v = Video(N, 0, 0, 0);
        UiOverlay.BlendInto(v, true);
        Check("halbdurchsichtig mischt ~50 %", v.R[10] >= 124 && v.R[10] <= 131, "r=" + v.R[10]);

        // 5) Nicht-Schwarz-Modus: deckend schwarz mit heller Leiste unten
        UiOverlay.Mode = UiOverlay.ModeNonBlack;
        UiOverlay.Analyse(Img(W, H, (x, y) => y >= H * 0.85 ? ((byte)250, (byte)120, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)255)), layout, UiOverlay.Mode);
        v = Video(N, 0, 0, 255);
        UiOverlay.BlendInto(v, true);
        int bottomMid = rightEnd + layout.BottomCount / 2;
        Check("Nicht-Schwarz: Leiste unten -> untere Zonen orange", v.R[bottomMid] == 250 && v.G[bottomMid] == 120, "rgb=" + v.R[bottomMid] + "," + v.G[bottomMid] + "," + v.B[bottomMid]);
        Check("Nicht-Schwarz: schwarzes Loch -> oben bleibt Video", v.B[2] == 255 && v.R[2] == 0, "rgb=" + v.R[2] + "," + v.G[2] + "," + v.B[2]);

        // 6) Startseite ohne Video (nur UI)
        UiOverlay.Mode = UiOverlay.ModeAuto;
        UiOverlay.Analyse(Img(W, H, (x, y) => ((byte)(x * 255 / W), (byte)40, (byte)(255 - x * 255 / W), (byte)255)), layout, UiOverlay.Mode);
        Check("frische UI vorhanden", UiOverlay.HasFresh(N));
        var only = new ZoneColors(N);
        bool okOnly = UiOverlay.CopyUiOnly(only, false);
        Check("Startseite: links blau, rechts rot", okOnly && only.B[leftMid] > 200 && only.R[rightMid] > 200,
              "links=" + only.R[leftMid] + "," + only.B[leftMid] + " rechts=" + only.R[rightMid] + "," + only.B[rightMid]);
        Check("falsche Zonenzahl wird abgelehnt", !UiOverlay.HasFresh(50));

        // 7) eingefrorene UI bei laufendem Video wird ausgeblendet
        UiOverlay.StaleMs = 100;
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 255, 0, 255)), layout, UiOverlay.Mode);
        Thread.Sleep(1300);
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 255, 0, 255)), layout, UiOverlay.Mode); // gleiches Bild, frisch
        v = Video(N, 255, 0, 0);
        cov = UiOverlay.BlendInto(v, true);
        Check("eingefrorene UI + laufendes Video -> Video", cov < 0.01 && v.R[0] == 255, "cov=" + cov.ToString("0.00"));
        // Stehende UI über stehendem Video (z. B. Startseite mit altem Videobild), nie "live" gesehen -> UI bleibt
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 200, 200, 255)), layout, UiOverlay.Mode);
        Thread.Sleep(1300);
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 200, 200, 255)), layout, UiOverlay.Mode);
        v = Video(N, 255, 0, 0);
        cov = UiOverlay.BlendInto(v, false);
        Check("stehende UI + stehendes Video -> UI bleibt", cov > 0.99 && v.G[0] == 200, "cov=" + cov.ToString("0.00"));
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 255, 0, 255)), layout, UiOverlay.Mode);
        Thread.Sleep(1300);
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 255, 0, 255)), layout, UiOverlay.Mode);

        // 7b) einmal als eingefroren erkannt -> bleibt aus, auch wenn das Video pausiert (kein Zurückspringen)
        v = Video(N, 255, 0, 0);
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 255, 0, 255)), layout, UiOverlay.Mode);
        cov = UiOverlay.BlendInto(v, true);
        v = Video(N, 255, 0, 0);
        cov = UiOverlay.BlendInto(v, false);
        Check("eingefrorene UI springt bei Video-Pause nicht zurück", cov < 0.01 && v.R[0] == 255, "cov=" + cov.ToString("0.00"));
        // ... aber eine echte Änderung (z. B. Pause-Menü) erscheint wieder - weich über 300 ms, nicht schlagartig
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 0, 255, 255)), layout, UiOverlay.Mode);
        v = Video(N, 255, 0, 0);
        double cov0 = UiOverlay.BlendInto(v, false);
        Check("neue UI nach Sperre blendet weich ein (nicht schlagartig)", cov0 < 0.5, "cov=" + cov0.ToString("0.00"));
        System.Threading.Thread.Sleep(350);
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 0, 255, 255)), layout, UiOverlay.Mode);
        v = Video(N, 255, 0, 0);
        cov = UiOverlay.BlendInto(v, false);
        Check("neue UI nach Sperre wieder sichtbar", cov > 0.99 && v.B[0] == 255, "cov=" + cov.ToString("0.00"));
        // 7c) geschützter Film (DRM) + stehende UI -> CopyUiOnly blendet aus; ohne DRM bleibt Startseite
        UiOverlay.StaleMs = 100;
        Thread.Sleep(1300);
        UiOverlay.Analyse(Img(W, H, (x, y) => (0, 0, 255, 255)), layout, UiOverlay.Mode);
        var o2 = new ZoneColors(N);
        UiOverlay.CopyUiOnly(o2, true);
        Check("DRM-Film + stehende UI -> ausgeblendet", o2.B[0] == 0, "b=" + o2.B[0]);
        UiOverlay.CopyUiOnly(o2, false);
        Check("Startseite ohne Film bleibt sichtbar", o2.B[0] == 255, "b=" + o2.B[0]);

        // 7d) Menü-Zonen passen zu den Video-Zonen, auch bei anderem Seitenverhältnis des Menübildes
        var vidL = new ZoneLayout(960, 540, N, 0.08);
        var uiL = UiOverlay.ScaledLayout(960, 540, 640, 540, N, 0.08);
        bool same = vidL.TopCount == uiL.TopCount && vidL.RightCount == uiL.RightCount && vidL.BottomCount == uiL.BottomCount;
        bool inside = true;
        foreach (var zz in uiL.Zones) if (zz.X0 < 0 || zz.X1 > 640 || zz.Y0 < 0 || zz.Y1 > 540 || zz.X1 <= zz.X0 || zz.Y1 <= zz.Y0) inside = false;
        Check("Menü-Zonen = Video-Zonen (Kantenaufteilung)", same && inside, vidL.TopCount + "/" + uiL.TopCount);

        // 8) veraltete Daten (> 1,5 s) werden ignoriert
        UiOverlay.StaleMs = 0;
        Thread.Sleep(1600);
        Check("alte UI-Daten werden ignoriert", !UiOverlay.HasFresh(N));

        // 9) UI aus
        UiOverlay.Mode = UiOverlay.ModeOff;
        Check("Modus aus -> nichts", UiOverlay.BlendInto(Video(N, 1, 2, 3), true) == 0);

        // 10) Zeitbudget: Analyse eines 640x360-Bildes
        UiOverlay.Mode = UiOverlay.ModeAuto;
        var big = Img(W, H, (x, y) => ((byte)x, (byte)y, 0, 255));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) UiOverlay.Analyse(big, layout, UiOverlay.Mode);
        sw.Stop();
        Check("Analyse schnell genug", sw.Elapsed.TotalMilliseconds / 50 < 20, (sw.Elapsed.TotalMilliseconds / 50).ToString("0.00") + " ms/Bild (PC)");

        // ---- Balken-Übernahme wie HyperHDR (BorderTracker) ----
        {
            const int BH = 540;
            var tr = new TizenAmbilight.BorderTracker(BH);
            int changes = 0;
            for (int i = 0; i < 11; i++) if (tr.Update(false, 70, 470, BH)) changes++;
            Check("erste stabile Letterbox gilt nach ~11 Bildern", tr.Known && tr.Start == 70 && tr.End == 470 && changes == 1, "start=" + tr.Start);
            for (int i = 0; i < 8; i++) tr.Update(false, 0, BH, BH);           // heller Schnitt: 8 Ausreißer
            for (int i = 0; i < 30; i++) tr.Update(false, 71, 469, BH);
            Check("kurze Ausreisser aendern nichts", tr.Start == 70 && tr.End == 470);
            for (int i = 0; i < 300; i++) tr.Update(true, 0, BH, BH);          // 12 s Abblende auf Schwarz
            Check("schwarzes Bild behaelt die Balken", tr.Known && tr.Start == 70);
            for (int i = 0; i < 40; i++) tr.Update(false, 0, BH, BH);
            Check("Vollbild erst nach 50 stabilen Bildern (40: noch nicht)", tr.Start == 70);
            for (int i = 0; i < 25; i++) tr.Update(false, 0, BH, BH);
            Check("Vollbild nach ~2 s stabil", tr.Start == 0 && tr.End == BH);
            for (int i = 0; i < 700; i++) tr.Update(true, 0, BH, BH);
            Check("sehr lange schwarz -> unbekannt (volle Flaeche)", !tr.Known && tr.Start == 0 && tr.End == BH);
        }
        // ---- Balkenerkennung: schwarzes Bild = unbekannt, Rand wird weich abgeschnitten ----
        {
            int BW = 960, BH2 = 540;
            var y = new byte[BW * BH2];
            for (int i = 0; i < y.Length; i++) y[i] = 16;
            int t, b; bool unk;
            TizenAmbilight.VideoPlaneCapture.DetectActiveArea(y, BW, BH2, 18, out t, out b, out unk);
            Check("schwarzes Bild -> unbekannt", unk && t == 0 && b == BH2);
            for (int r = 70; r < 470; r++) for (int x = 0; x < BW; x++) y[r * BW + x] = (byte)(40 + (x % 50));
            TizenAmbilight.VideoPlaneCapture.DetectActiveArea(y, BW, BH2, 18, out t, out b, out unk);
            Check("Letterbox erkannt, +2 px Rand", !unk && t == 72 && b == 468, "t=" + t + " b=" + b);
        }

        Console.WriteLine(fails == 0 ? "ALLE TESTS BESTANDEN" : fails + " TEST(S) FEHLGESCHLAGEN");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
