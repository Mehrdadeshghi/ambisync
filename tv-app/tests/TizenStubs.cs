// Nur für die Typprüfung außerhalb von Tizen. Wird NICHT in die TV-App gebaut.
namespace Tizen.Applications
{
    public class DirectoryInfo { public string Data => "/tmp/data/"; public string Resource => System.Environment.GetEnvironmentVariable("AMB_RES") ?? "/tmp/res/"; }
    public class Application { public static Application Current { get; set; } public DirectoryInfo DirectoryInfo { get; } = new DirectoryInfo(); }
    public abstract class CoreApplication : Application { public void Run(string[] a) { } public void Exit() { } }
    public class ServiceApplication : CoreApplication { protected virtual void OnCreate() { } protected virtual void OnTerminate() { } }
    public static class Preference
    {
        public static bool Contains(string k) => false;
        public static T Get<T>(string k) => default(T);
        public static void Set(string k, object v) { }
    }
}

namespace TizenAmbilight
{
    // Bluetooth-Attrappe für die PC-Tests: merkt sich alle geschriebenen Stücke.
    sealed class FakeBle : IBleTransport
    {
        public static int MtuSim = 517;
        public static bool FailConnect;
        public static readonly System.Collections.Generic.List<byte[]> Written = new System.Collections.Generic.List<byte[]>();
        bool _c;
        public int Mtu => MtuSim;
        public bool Connected => _c;
        public bool Connect(string mac, out string err) { err = FailConnect ? "Attrappe: aus" : null; _c = !FailConnect; return _c; }
        public bool Write(byte[] d, out string err) { err = null; lock (Written) Written.Add(d); return true; }
        public void Close() { _c = false; }
    }
    static partial class BleLink
    {
        static IBleTransport CreateTransport() { return new FakeBle(); }
    }
}
