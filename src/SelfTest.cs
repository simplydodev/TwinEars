using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace TwinEars
{
    // "TwinEars.exe /selftest [report.txt] [/seconds N]"
    // Runs the real mirroring engine from the default output to every other connected device at
    // volume 0 (silent) and writes a report: device formats, exclusive-mode locks, drift correction,
    // how steady the delay stays, idle/resume behaviour and error handling.
    // Changes no settings and writes nothing except the report and its log.
    internal static class SelfTest
    {
        public static int Run(string[] args)
        {
            string reportPath = Program.ArgValue(args, "selftest");
            if (string.IsNullOrEmpty(reportPath)) reportPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TwinEars-selftest.txt");
            Log.Enable(Path.ChangeExtension(reportPath, ".log"));
            int seconds;
            if (!int.TryParse(Program.ArgValue(args, "seconds"), out seconds)) seconds = 20;
            seconds = Math.Max(4, Math.Min(600, seconds));

            var report = new StringBuilder();
            int result;
            try
            {
                result = Execute(report, seconds);
            }
            catch (Exception ex)
            {
                report.AppendLine("SELF-TEST CRASHED: " + ex);
                result = 2;
            }
            File.WriteAllText(reportPath, report.ToString());
            return result;
        }

        static int Execute(StringBuilder r, int seconds)
        {
            r.AppendLine("TwinEars self-test, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            r.AppendLine("Windows " + Environment.OSVersion.Version + ", " + (Environment.Is64BitProcess ? "64" : "32") + "-bit process, CLR " + Environment.Version);
            r.AppendLine("Ring buffer logic: " + (RingBufferWorks() ? "ok" : "FAILED"));
            r.AppendLine("Sound detection:   " + (SoundDetectionWorks() ? "ok" : "FAILED"));

            List<AudioDevice> devices = Wasapi.GetActiveOutputs();
            string defaultId = Wasapi.GetDefaultOutputId();
            var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            r.AppendLine();
            r.AppendLine("Active output devices (* = Windows default):");
            foreach (AudioDevice d in devices)
            {
                bool isLocked = Wasapi.IsLockedExclusively(d.Id);
                if (isLocked) locked.Add(d.Id);
                int exclusive = Wasapi.GetExclusiveModeAllowed(d.Id);
                r.AppendLine("  " + (Same(d.Id, defaultId) ? "* " : "  ") + d.Name + "   [" + Wasapi.DescribeMixFormat(d.Id) + "]" +
                             "   exclusive mode " + (exclusive == 1 ? "allowed" : exclusive == 0 ? "off" : "unknown") +
                             (isLocked ? "   BUSY: Windows refuses all shared audio on it" : ""));
            }
            if (defaultId == null)
            {
                r.AppendLine("No default output device: nothing to test.");
                return 1;
            }
            var targets = new List<AudioDevice>();
            foreach (AudioDevice d in devices)
                if (!Same(d.Id, defaultId)) targets.Add(d);
            if (targets.Count == 0)
            {
                r.AppendLine("Only one output device is connected: connect a second one to test mirroring.");
                return 1;
            }

            // --- Phase 1: steady mirroring, then a live delay change.
            r.AppendLine();
            r.AppendLine("Phase 1: mirroring the default output to " + targets.Count + " device(s) for " + seconds + " s at volume 0 (silent).");
            r.AppendLine("Extra delay 0 ms, then 150 ms halfway. 'err' = distance from the target delay.");
            Log.Write("self-test: phase 1");
            var keepAlive = new SilentStream(defaultId);   // makes sure loopback delivers packets
            keepAlive.Start();
            var workers = new List<MirrorWorker>();
            foreach (AudioDevice t in targets)
            {
                var w = new MirrorWorker(defaultId, t.Id, t.Name, 40, 20);
                w.VolumePercent = 0;
                w.IdleCloseMs = 6000;   // shorter than the app's 20 s so the test doesn't take forever
                w.SelfTestForceActive = true;
                w.Start();
                workers.Add(w);
            }
            int clock = 0;
            for (int s = 1; s <= seconds; s++)
            {
                if (s == seconds / 2 + 1)
                    foreach (MirrorWorker w in workers) w.DelayMs = 150;
                Sample(r, workers, ++clock);
            }
            string keepAliveError = keepAlive.Error;

            // --- Phase 2: no sound -> each copy should close its output so devices and the laptop can sleep.
            r.AppendLine();
            r.AppendLine("Phase 2: silence for up to 14 s (outputs should close after 6 s; the app uses 20 s).");
            Log.Write("self-test: phase 2");
            foreach (MirrorWorker w in workers) w.SelfTestForceActive = false;
            bool wentIdle = false;
            for (int s = 0; s < 14 && !wentIdle; s++)
            {
                Sample(r, workers, ++clock);
                wentIdle = true;
                foreach (MirrorWorker w in workers)
                    if (!w.Ended && (w.OutputOpen || w.State != MirrorState.Waiting)) wentIdle = false;
            }

            // --- Phase 3: sound returns -> outputs reopen.
            r.AppendLine();
            r.AppendLine("Phase 3: sound returns (outputs should reopen).");
            Log.Write("self-test: phase 3");
            var opensBefore = new Dictionary<MirrorWorker, int>();
            foreach (MirrorWorker w in workers)
            {
                opensBefore[w] = w.OutputOpens;
                w.SelfTestForceActive = true;
            }
            for (int s = 0; s < 4; s++) Sample(r, workers, ++clock);
            bool resumed = true;
            foreach (MirrorWorker w in workers)
                if (!w.Ended && (w.State != MirrorState.Playing || w.OutputOpens <= opensBefore[w])) resumed = false;

            foreach (MirrorWorker w in workers) w.RequestStop();
            foreach (MirrorWorker w in workers) w.Join();
            keepAlive.Stop();

            // --- Phase 4: copying to a device another app has locked must fail cleanly with a clear reason.
            string errorPath = null;
            AudioDevice lockedTarget = null, freeSource = null;
            foreach (AudioDevice d in devices)
            {
                if (locked.Contains(d.Id)) { if (lockedTarget == null) lockedTarget = d; }
                else if (freeSource == null) freeSource = d;
            }
            if (lockedTarget != null && freeSource != null)
            {
                Log.Write("self-test: phase 4");
                var w = new MirrorWorker(freeSource.Id, lockedTarget.Id, lockedTarget.Name, 40, 20);
                w.VolumePercent = 0;
                w.SelfTestForceActive = true;
                w.Start();
                for (int i = 0; i < 30 && !w.Ended; i++) Thread.Sleep(100);
                w.RequestStop();
                w.Join();
                errorPath = w.Error ?? "(no error reported)";
            }

            // --- Summary
            r.AppendLine();
            r.AppendLine("Summary:");
            if (keepAliveError != null) r.AppendLine("  note: couldn't play silence on the main output (" + keepAliveError + "); relied on Windows' own packets");
            int failed = 0;
            foreach (MirrorWorker w in workers)
            {
                bool blocked = locked.Contains(w.TargetId);
                bool ok = w.Error == null && w.FramesOut > 0;
                if (!ok && !blocked) failed++;
                r.AppendLine("  " + (ok ? "OK      " : blocked ? "BLOCKED " : "FAIL    ") + w.Label +
                             (blocked ? "  (device is busy, expected)" : ""));
                r.AppendLine("          main output format " + w.FormatText + ", output buffer " + w.BufferMsActual.ToString("0.0") +
                             " ms, drift correction: " + (w.UsesRateAdjust ? "Windows resampler" : "sample slipping") +
                             ", underruns " + w.Underruns + ", resyncs " + w.Resyncs);
                if (w.Error != null) r.AppendLine("          error: " + w.Error);
            }
            r.AppendLine("  " + (wentIdle ? "OK      " : "FAIL    ") + "idle: outputs closed during silence");
            r.AppendLine("  " + (resumed ? "OK      " : "FAIL    ") + "resume: outputs reopened when sound returned");
            if (!wentIdle) failed++;
            if (!resumed) failed++;
            if (errorPath != null)
            {
                bool clear = errorPath.Contains("busy");
                if (!clear) failed++;
                r.AppendLine("  " + (clear ? "OK      " : "FAIL    ") + "error path: copying to locked '" + lockedTarget.Name + "' reports: " + errorPath);
            }
            r.AppendLine();
            r.AppendLine(failed == 0 ? "RESULT: PASS" : "RESULT: " + failed + " problem(s)");
            return failed == 0 ? 0 : 1;
        }

        static void Sample(StringBuilder r, List<MirrorWorker> workers, int second)
        {
            Thread.Sleep(1000);
            foreach (MirrorWorker w in workers)
            {
                r.AppendLine(string.Format("  {0,3}s  {1,-30} {2,-8} {3,-7} err {4,6:0.0} ms  speed {5:0.00000}  in {6,8}  out {7,8}  underruns {8}  resyncs {9}{10}",
                    second, Trim(w.Label, 30), w.Ended ? "ENDED" : w.State.ToString(), w.OutputOpen ? "out:on" : "out:off",
                    w.LatencyErrorMs, w.Ratio, w.FramesIn, w.FramesOut, w.Underruns, w.Resyncs,
                    w.Error != null ? "  error: " + w.Error : ""));
            }
        }

        static bool RingBufferWorks()
        {
            var ring = new RingBuffer(2, 4);
            ring.Write(new float[] { 1, 1, 2, 2, 3, 3 }, 3);
            var a = new float[4];
            if (ring.Read(a, 0, 2) != 2 || a[0] != 1 || a[2] != 2) return false;
            ring.Write(new float[] { 4, 4, 5, 5, 6, 6, 7, 7 }, 4);   // overflow: oldest frame (3) is dropped
            var b = new float[8];
            if (ring.Read(b, 0, 8) != 4 || b[0] != 4 || b[6] != 7) return false;
            ring.WriteSilence(2);
            ring.Skip(1);
            return ring.Count == 1;
        }

        static bool SoundDetectionWorks()
        {
            var quiet = new float[] { 0f, 1e-6f, -2e-5f, 0f };
            var audible = new float[] { 0f, 0f, -0.01f, 0f };
            return !MirrorWorker.HasSound(quiet, quiet.Length) && MirrorWorker.HasSound(audible, audible.Length);
        }

        static bool Same(string a, string b)
        {
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static string Trim(string s, int max)
        {
            return s.Length > max ? s.Substring(0, max - 1) + "~" : s;
        }
    }

    // Keeps a device's audio engine running with pure silence, so loopback capture has packets to
    // deliver even when nothing else is playing. Used only by the self-test.
    internal sealed class SilentStream
    {
        readonly string deviceId;
        volatile bool stop;
        Thread thread;
        public volatile string Error;

        public SilentStream(string deviceId)
        {
            this.deviceId = deviceId;
        }

        public void Start()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            if (thread != null) thread.Join(2000);
        }

        void Run()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            RenderStream stream = null;
            try
            {
                enumerator = Wasapi.CreateEnumerator();
                enumerator.GetDevice(deviceId, out device);
                WaveFormatInfo format = Wasapi.GetMixFormat(device);
                stream = RenderStream.Open(enumerator, deviceId, format, 40);
                while (!stop)
                {
                    stream.Event.WaitOne(20);
                    uint padding;
                    stream.Client.GetCurrentPadding(out padding);
                    int free = stream.BufferFrames - (int)padding;
                    if (free > 0) stream.WriteSilence(free);
                }
            }
            catch (Exception ex)
            {
                Error = Wasapi.Describe(ex);
            }
            finally
            {
                if (stream != null) stream.Dispose();
                Wasapi.Release(device);
                Wasapi.Release(enumerator);
            }
        }
    }
}
