using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("TwinEars")]
[assembly: AssemblyProduct("TwinEars")]
[assembly: AssemblyDescription("Play your Windows audio on two devices at once.")]
[assembly: AssemblyCopyright("Copyright (c) 2026 the TwinEars authors. MIT License.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace TwinEars
{
    // Command line (all optional):
    //   /on          turn mirroring on (at start, or in the copy that's already running)
    //   /off         turn mirroring off in the running copy
    //   /toggle      flip mirroring on/off in the running copy (handy as a shortcut key)
    //   /exit        close the running copy
    //   /tray        start hidden in the system tray
    //   /log         write TwinEars.log next to the settings file
    //   /selftest [report.txt] [/seconds N]   silent diagnostic run, writes a report and exits
    //   /make-icon file.ico                   (developers) regenerate src\TwinEars.ico
    internal static class Program
    {
        const string MutexName = @"Local\TwinEars.SingleInstance";
        const string CommandEventPrefix = @"Local\TwinEars.";
        static readonly string[] Commands = { "show", "on", "off", "toggle", "exit" };

        [STAThread]
        static int Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); }
            catch (Exception) { }

            if (HasArg(args, "selftest")) return SelfTest.Run(args);
            if (HasArg(args, "make-icon"))
            {
                AppIcon.WriteIco(ArgValue(args, "make-icon") ?? "TwinEars.ico");
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool firstInstance;
            using (var mutex = new Mutex(false, MutexName, out firstInstance))
            {
                if (!firstInstance)
                {
                    // Already running (maybe hidden in the tray): pass the command on instead of starting
                    // a second copy, which would double the audio. No command = bring its window up.
                    string command = "show";
                    foreach (string c in Commands)
                        if (HasArg(args, c)) command = c;
                    try
                    {
                        if (command == "show") Native.AllowSetForegroundWindow(0xFFFFFFFF);
                        using (EventWaitHandle signal = EventWaitHandle.OpenExisting(CommandEventPrefix + command)) signal.Set();
                    }
                    catch (Exception) { }
                    return 0;
                }
                if (HasArg(args, "exit")) return 0;   // nothing running to close

                AppSettings settings = AppSettings.Load();
                if (settings.EnableLog || HasArg(args, "log")) Log.Enable(Path.ChangeExtension(settings.FilePath, ".log"));
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { ReportError(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e) { Log.Write("Unhandled: " + e.ExceptionObject); };

                bool turnOn = settings.TurnOnAtStart || HasArg(args, "on") || HasArg(args, "toggle");
                var form = new MainForm(settings, turnOn, HasArg(args, "tray"));
                var handles = new List<EventWaitHandle>();
                var waits = new List<RegisteredWaitHandle>();
                foreach (string c in Commands)
                {
                    string command = c;
                    var handle = new EventWaitHandle(false, EventResetMode.AutoReset, CommandEventPrefix + command);
                    handles.Add(handle);
                    waits.Add(ThreadPool.RegisterWaitForSingleObject(handle, delegate { form.RunCommand(command); }, null, -1, false));
                }
                Application.Run(form);
                foreach (RegisteredWaitHandle w in waits) w.Unregister(null);
                foreach (EventWaitHandle h in handles) h.Close();
            }
            return 0;
        }

        static void ReportError(Exception ex)
        {
            Log.Write("UI error: " + ex);
            MessageBox.Show("Something unexpected went wrong:\n\n" + ex.Message +
                            "\n\nTwinEars will keep running. Your normal audio is not affected.",
                            "TwinEars", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        internal static bool HasArg(string[] args, string name)
        {
            foreach (string a in args)
                if (Matches(a, name)) return true;
            return false;
        }

        internal static string ArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (Matches(args[i], name) && !args[i + 1].StartsWith("-") && !args[i + 1].StartsWith("/")) return args[i + 1];
            return null;
        }

        static bool Matches(string arg, string name)
        {
            return string.Equals(arg.TrimStart('-', '/'), name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
