using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class UiSmokeTest
{
    private static Type type;
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Get(Form f, string name) { return type.GetField(name, Flags).GetValue(f); }
    private static void Call(Form f, string name) { type.GetMethod(name, Flags).Invoke(f, null); }
    private static void Check(bool okay, string message) { if (!okay) throw new Exception(message); }
    private static void Until(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < 9000) { Application.DoEvents(); Thread.Sleep(30); }
        Check(condition(), message);
    }
    [STAThread] private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        type = Assembly.LoadFrom(args[0]).GetType("MicWindow", true);
        string parameters = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "parameters.local.json");
        using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset))
        {
            var f = (Form)Activator.CreateInstance(type, new object[] {true, signal});
            f.Opacity = 0; f.ShowInTaskbar = false; f.Show();
            try
            {
                Call(f, "StartBridge");
                Until(delegate { Call(f, "PollWorker"); return (string)Get(f, "state") == "ready"; }, "Worker did not become ready.");
                string config = File.ReadAllText(parameters);
                Check(config.Contains("\"TargetHotkeyMode\":\"toggle\""), "NetEase must receive tap mode.");
                Check(config.Contains("\"KeyMode\":\"hold\""), "The mouse must retain hold-to-speak mode.");
                Check(config.Contains("\"OutputDeviceName\":\"CABLE Input\""), "Audio route changed.");
                int workerId = ((Process)Get(f, "worker")).Id;
                string status = (string)Get(f, "stopFile") + ".status";
                File.WriteAllText(status, "waiting"); Call(f, "PollWorker");
                Check((string)Get(f, "state") == "waiting", "Sleep state was not displayed.");
                Check(((Button)Get(f, "action")).Enabled, "Waiting could not be paused.");
                File.WriteAllText(status, "connecting"); Call(f, "PollWorker");
                Check((string)Get(f, "state") == "connecting", "Reconnect state was not displayed.");
                File.WriteAllText(status, "ready"); Call(f, "PollWorker");
                Check((string)Get(f, "state") == "ready" && ((Process)Get(f, "worker")).Id == workerId,
                    "Reconnect restarted the supervisor or failed to restore ready state.");
                Call(f, "HideToTray"); Check(!f.Visible && ((NotifyIcon)Get(f, "tray")).Visible, "Tray hide failed.");
                Call(f, "RestoreWindow"); Check(f.Visible && !((NotifyIcon)Get(f, "tray")).Visible, "Tray restore failed.");
                Call(f, "StopBridge");
                Until(delegate { Call(f, "PollWorker"); return Get(f, "worker") == null; }, "Stop did not finish.");
                ((CheckBox)Get(f, "link")).Checked = false;
                Call(f, "StartBridge");
                Until(delegate { Call(f, "PollWorker"); return (string)Get(f, "state") == "ready"; }, "Restart failed.");
                Check(File.ReadAllText(parameters).Contains("\"TargetHotkeyMode\":\"none\""), "Disabled sync sent a hotkey.");
                f.Close();
                Until(delegate { if (!f.IsDisposed) Call(f, "PollWorker"); return f.IsDisposed; }, "Close left worker running.");
            }
            finally { if (!f.IsDisposed) { Call(f, "StopBridge"); Until(delegate { Call(f, "PollWorker"); return Get(f, "worker") == null; }, "Cleanup timed out."); f.Dispose(); } }
            string fail = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fail.local");
            File.WriteAllText(fail, "fail");
            using (var errorForm = (Form)Activator.CreateInstance(type, new object[] {true, signal}))
            {
                try
                {
                    Call(errorForm, "StartBridge");
                    Until(delegate { Call(errorForm, "PollWorker"); return Get(errorForm, "worker") == null; }, "Failed worker was not handled.");
                    Check((string)Get(errorForm, "state") == "error", "Failure was not displayed.");
                    Check(((CheckBox)Get(errorForm, "link")).Enabled, "Settings remained locked after failure.");
                }
                finally { File.Delete(fail); }
            }
        }
        Console.WriteLine("PASS: tap shortcut, mouse hold mode, audio route, tray, restart, close cleanup, failure state. No Bluetooth or key injection.");
        return 0;
    }
}
