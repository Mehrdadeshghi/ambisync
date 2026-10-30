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
