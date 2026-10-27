using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TwinEars
{
    internal sealed class TargetSetting
    {
        public string Id = "";
        public string Name = "";
        public bool Enabled;
        public int DelayMs;
        public int Volume = 100;
    }

    // Settings live in TwinEars.ini next to the exe (portable). If that folder is read-only,
    // they go to %LOCALAPPDATA%\TwinEars instead. Nothing is ever written to the registry.
    internal sealed class AppSettings
    {
        public const string FileName = "TwinEars.ini";

        public string SourceId = "";     // "" = follow the Windows default output
        public string SourceName = "";
        public bool TurnOnAtStart;
        public bool CloseToTray;
        public bool EnableLog;
        public int BufferMs = 40;
        public int SafetyMs = 20;
        public readonly List<TargetSetting> Targets = new List<TargetSetting>();
        public string FilePath;

        static string PortablePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName); }
        }

        static string FallbackPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwinEars", FileName); }
        }

        public static AppSettings Load()
        {
            var s = new AppSettings();
            string path = File.Exists(PortablePath) ? PortablePath : File.Exists(FallbackPath) ? FallbackPath : null;
            s.FilePath = path ?? PortablePath;
            if (path == null) return s;
            try
            {
                foreach (string line in File.ReadAllLines(path)) s.ParseLine(line);
            }
            catch (Exception ex)
            {
                Log.Write("Could not read settings: " + ex.Message);
            }
            return s;
        }

        void ParseLine(string raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') return;
            int eq = line.IndexOf('=');
            if (eq <= 0) return;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string value = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "source": SourceId = value; break;
                case "sourcename": SourceName = value; break;
                case "turnonatstart": TurnOnAtStart = value == "1"; break;
                case "closetotray": CloseToTray = value == "1"; break;
                case "log": EnableLog = value == "1"; break;
                case "bufferms": BufferMs = Clamp(ParseInt(value, 40), 10, 500); break;
                case "safetyms": SafetyMs = Clamp(ParseInt(value, 20), 0, 500); break;
                case "target":
                    // Target=enabled|delayMs|volume|deviceId|name
                    string[] parts = value.Split(new[] { '|' }, 5);
                    if (parts.Length < 4 || parts[3].Length == 0 || Find(parts[3]) != null) return;
                    Targets.Add(new TargetSetting
                    {
                        Enabled = parts[0] == "1",
                        DelayMs = Clamp(ParseInt(parts[1], 0), 0, MirrorWorker.MaxDelayMs),
                        Volume = Clamp(ParseInt(parts[2], 100), 0, 100),
                        Id = parts[3],
                        Name = parts.Length > 4 ? parts[4] : ""
                    });
                    break;
            }
        }

        public bool Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; TwinEars settings. Plain text: safe to edit, or delete to reset.");
            sb.AppendLine("Source=" + SourceId);
            sb.AppendLine("SourceName=" + Clean(SourceName));
            sb.AppendLine("TurnOnAtStart=" + (TurnOnAtStart ? "1" : "0"));
            sb.AppendLine("CloseToTray=" + (CloseToTray ? "1" : "0"));
            sb.AppendLine("Log=" + (EnableLog ? "1" : "0"));
            sb.AppendLine("; Advanced: output buffer and safety margin in ms. Raise if the copy crackles.");
            sb.AppendLine("BufferMs=" + BufferMs);
            sb.AppendLine("SafetyMs=" + SafetyMs);
            sb.AppendLine("; Target=enabled|delayMs|volume|deviceId|name");
            foreach (TargetSetting t in Targets)
                sb.AppendLine("Target=" + (t.Enabled ? "1" : "0") + "|" + t.DelayMs + "|" + t.Volume + "|" + t.Id + "|" + Clean(t.Name));

            var tried = new List<string>();
            foreach (string path in new[] { FilePath, PortablePath, FallbackPath })
            {
                if (path == null || tried.Contains(path)) continue;
                tried.Add(path);
                if (TryWrite(path, sb.ToString()))
                {
                    FilePath = path;
                    return true;
                }
            }
            return false;
        }

        static bool TryWrite(string path, string text)
        {
            string temp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temp, text, Encoding.UTF8);
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                catch (IOException)
                {
                    File.Copy(temp, path, true);   // some drives (FAT USB sticks, shares) can't do Replace
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception) { }
            }
        }

        public TargetSetting Find(string id)
        {
            if (id == null) return null;
            foreach (TargetSetting t in Targets)
                if (string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        public TargetSetting GetOrAdd(string id, string name)
        {
            TargetSetting t = Find(id);
            if (t == null)
            {
                t = new TargetSetting { Id = id, Name = name ?? "" };
                Targets.Add(t);
            }
            return t;
        }

        static string Clean(string s)
        {
            return (s ?? "").Replace('\r', ' ').Replace('\n', ' ');
        }

        static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        static int Clamp(int v, int min, int max)
        {
            return v < min ? min : v > max ? max : v;
        }
    }

    internal static class Log
    {
        static readonly object Gate = new object();
        static string path;

        public static void Enable(string file)
        {
            path = file;
            Write("---- TwinEars started ----");
        }

        public static void Write(string message)
        {
            string p = path;
            if (p == null) return;
            lock (Gate)
            {
                try { File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  ") + message + Environment.NewLine); }
                catch (Exception) { }
            }
        }
    }
}
