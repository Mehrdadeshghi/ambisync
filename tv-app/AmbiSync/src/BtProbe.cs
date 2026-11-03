using System;
using System.Collections.Generic;
using System.Reflection;

namespace TizenAmbilight
{
    /// <summary>
    /// AmbiSync: prüft einmal, ob dieser Fernseher Bluetooth Low Energy für Apps anbietet
    /// (Grundlage für eine mögliche Verbindung TV -> ESP32 per Bluetooth). Nur lesen, nichts einschalten.
    ///
    /// Samsung führt die Bluetooth-API für das TV-Profil als "optional": ob ein Modell sie hat, sagen nur
    /// die Feature-Schlüssel des Geräts. Gelesen wird über Tizen.System.Information, per Reflection wie
    /// <see cref="Ambilight.ReadPlatformInfo"/>, damit eine geänderte API den Build nicht bricht.
    /// Ergebnis z. B. "bt=1 le=1 gattc=1 hci=1" (1 = ja, 0 = nein, ? = nicht lesbar).
    /// </summary>
    static class BtProbe
    {
        static string _result;
        public static string Result { get { return _result ?? (_result = Run()); } }

        static string Run()
        {
            var parts = new List<string>();
            try
            {
                parts.Add("bt=" + Feature("http://tizen.org/feature/network.bluetooth"));
                parts.Add("le=" + Feature("http://tizen.org/feature/network.bluetooth.le"));
                parts.Add("gattc=" + Feature("http://tizen.org/feature/network.bluetooth.le.gatt.client"));
                bool hci = false;
                try { hci = System.IO.Directory.Exists("/sys/class/bluetooth") && System.IO.Directory.GetDirectories("/sys/class/bluetooth").Length > 0; } catch { }
                parts.Add("hci=" + (hci ? "1" : "0"));
            }
            catch (Exception e) { parts.Add("err=" + e.GetType().Name); }
            string r = string.Join(" ", parts.ToArray());
            Ambilight.L("bluetooth: " + r);
            return r;
        }

        static MethodInfo _tryGet;

        static string Feature(string key)
        {
            try
            {
                if (_tryGet == null)
                {
                    try { Assembly.Load("Tizen.System.Information"); } catch { }
                    try { Assembly.Load("Tizen.System"); } catch { }
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = null;
                        try { t = asm.GetType("Tizen.System.Information", false); } catch { }
                        if (t == null) continue;
                        foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            if (m.Name == "TryGetValue" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
                            { _tryGet = m.MakeGenericMethod(typeof(bool)); break; }
                        if (_tryGet != null) break;
                    }
                }
                if (_tryGet == null) return "?";
                object[] args = { key, false };
                bool ok = (bool)_tryGet.Invoke(null, args);
                if (!ok) return "?";
                return (bool)args[1] ? "1" : "0";
            }
            catch { return "?"; }
        }
    }
}
