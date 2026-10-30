using System;
using System.Reflection;
using TizenAmbilight;

static class ProtoTest
{
    static int fails = 0;
    static void Check(string n, bool ok, string d = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (d != "" ? "  (" + d + ")" : "")); if (!ok) fails++; }
    static void Main()
    {
        var amb = typeof(UiOverlay).Assembly.GetType("TizenAmbilight.Ambilight");
        BindingFlags SF = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        object cfg = amb.GetMethod("ReadConfig", SF).Invoke(null, null);
        var ct = cfg.GetType();
        Func<string, object> get = f => ct.GetField(f).GetValue(cfg);
        Check("Standard: wledIp = auto", (string)get("WledIp") == "auto", (string)get("WledIp"));

        amb.GetMethod("AutoDiscover", SF).Invoke(null, new object[] { cfg, false, false });
        Check("ESP32 per Broadcast gefunden", (string)get("WledIp") == "127.0.0.1" || ((string)get("WledIp")).StartsWith("1"), (string)get("WledIp"));
        Check("LED-Anzahl vom ESP32 übernommen", (int)get("LedCount") == 123 && (int)get("ZoneCount") == 123, get("LedCount") + "/" + get("ZoneCount"));
        Check("Steuer-URL gesetzt", ((string)get("ControlUrl")).EndsWith("/tv/control"), (string)get("ControlUrl"));
        ct.GetField("WledIp").SetValue(cfg, "127.0.0.1");   // im Test lokal weiter

        // Statuspaket
        var statsT = amb.GetNestedType("Stats", BindingFlags.NonPublic);
        object st = Activator.CreateInstance(statsT, true);
        statsT.GetField("BackendActive").SetValue(st, "ui_only");
        statsT.GetField("LastStatus").SetValue(st, "Test \"mit\" Anführungszeichen\\");
        var su = amb.GetNestedType("StatusUdp", BindingFlags.NonPublic);
        su.GetMethod("Maybe", SF).Invoke(null, new object[] { cfg, st });

        // Farbpaket (wie die Hauptschleife es baut)
        var z = new ZoneColors(123);
        for (int i = 0; i < 123; i++) { z.R[i] = (byte)i; z.G[i] = 100; z.B[i] = 200; }
        byte[] p = WledOutput.BuildDrgbMapped(z, 1, 1.0, 1.0, 0, 0, false, 123);
        Check("DRGB-Paket: Kopf + 123 LEDs", p.Length == 2 + 123 * 3 && p[0] == 2 && p[1] == 1, "len=" + p.Length);
        string err = WledOutput.Send("127.0.0.1", 21324, p);
        Check("DRGB gesendet", err == null, err ?? "");

        int leds, port;
        bool okp = (bool)amb.GetMethod("ParseDiscoveryReply", SF).Invoke(null, new object[] { "Fremdes Paket", 0, 0 });
        Check("fremde Antworten werden ignoriert", !okp);
        System.Threading.Thread.Sleep(500);
        Console.WriteLine(fails == 0 ? "TV-SEITE OK" : fails + " FEHLER");
        Environment.Exit(fails);
    }
}
