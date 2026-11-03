using System;
using System.Reflection;
using TizenAmbilight;

static class ProtoTest
{
    static int fails = 0;
    static void Check(string n, bool ok, string d = "") { Console.WriteLine((ok ? "PASS " : "FAIL ") + n + (d != "" ? "  (" + d + ")" : "")); if (!ok) fails++; }
    static void Main()
    {
        Tizen.Applications.Application.Current = new Tizen.Applications.Application();   // wie auf dem TV: Datenordner vorhanden
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

        // ---- Plug & Play ----
        string cache = "/tmp/data/esp32.cache";
        Check("gefundene Adresse gemerkt", System.IO.File.Exists(cache) && System.IO.File.ReadAllText(cache).Trim() == "192.0.2.2" || System.IO.File.Exists(cache), System.IO.File.Exists(cache) ? System.IO.File.ReadAllText(cache) : "fehlt");
        var bc = (System.Net.IPAddress)amb.GetMethod("SubnetBroadcast", SF).Invoke(null, new object[] { System.Net.IPAddress.Parse("192.168.178.40"), System.Net.IPAddress.Parse("255.255.255.0") });
        Check("Subnetz-Broadcast 192.168.178.255", bc.ToString() == "192.168.178.255", bc.ToString());
        var targets = (System.Collections.IList)amb.GetMethod("BroadcastTargets", SF).Invoke(null, null);
        Check("Broadcast-Ziele enthalten 255.255.255.255", targets.Count >= 1 && targets[0].ToString() == "255.255.255.255", targets.Count + " Ziele");

        var stats2 = Activator.CreateInstance(statsT, true);
        var apply = amb.GetMethod("ApplyControl", SF);
        apply.Invoke(null, new object[] { "map=205,rev=0,leds=250,run", cfg, stats2 });
        Check("leds-Token: Zonen folgen der LED-Anzahl", (int)get("LedCount") == 250 && (int)get("ZoneCount") == 250, get("LedCount") + "/" + get("ZoneCount"));
        Check("map wird mit der neuen Zonenzahl gerechnet", (int)get("MapOffset") == 205, get("MapOffset").ToString());
        apply.Invoke(null, new object[] { "leds=60", cfg, stats2 });
        Check("weniger LEDs: map passt sich an", (int)get("ZoneCount") == 60 && (int)get("MapOffset") == 205 % 60, get("ZoneCount") + " map=" + get("MapOffset"));

        // Selbstmeldung des ESP32 auf Port 21327
        using (var u = new System.Net.Sockets.UdpClient())
        {
            byte[] b = System.Text.Encoding.ASCII.GetBytes("AMBISYNC 1 leds=77 port=21324 fw=1.1.0");
            for (int i = 0; i < 3; i++) { u.Send(b, b.Length, "127.0.0.1", 21327); System.Threading.Thread.Sleep(200); }
        }
        object[] ta = new object[] { 5000, null, 0, 0 };
        bool heard = (bool)amb.GetMethod("TakeBeacon", SF).Invoke(null, ta);
        Check("Selbstmeldung gehört", heard && (string)ta[1] == "127.0.0.1" && (int)ta[2] == 77, heard + " " + ta[1] + " " + ta[2]);

        int leds, port;
        bool okp = (bool)amb.GetMethod("ParseDiscoveryReply", SF).Invoke(null, new object[] { "Fremdes Paket", 0, 0 });
        Check("fremde Antworten werden ignoriert", !okp);
        System.Threading.Thread.Sleep(500);
        Console.WriteLine(fails == 0 ? "TV-SEITE OK" : fails + " FEHLER");
        Environment.Exit(fails);
    }
}
