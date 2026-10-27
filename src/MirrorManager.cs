using System;
using System.Collections.Generic;

namespace TwinEars
{
    // Keeps exactly one worker running for every device that should currently receive a copy,
    // and nothing else. Called repeatedly from the UI timer, so it also handles devices coming and
    // going, the Windows default changing, and retrying after errors.
    internal sealed class MirrorManager
    {
        readonly Dictionary<string, MirrorWorker> workers = new Dictionary<string, MirrorWorker>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public ICollection<MirrorWorker> Workers
        {
            get { return workers.Values; }
        }

        public MirrorWorker Get(string targetId)
        {
            MirrorWorker w;
            return targetId != null && workers.TryGetValue(targetId, out w) ? w : null;
        }

        public void Apply(bool on, string sourceId, ICollection<string> presentIds, IList<TargetSetting> targets, int bufferMs, int safetyMs)
        {
            var wanted = new Dictionary<string, TargetSetting>(StringComparer.OrdinalIgnoreCase);
            if (on && !string.IsNullOrEmpty(sourceId) && presentIds.Contains(sourceId))
            {
                foreach (TargetSetting t in targets)
                {
                    // Never copy a device onto itself: that would be an echo loop.
                    if (t.Enabled && presentIds.Contains(t.Id) && !string.Equals(t.Id, sourceId, StringComparison.OrdinalIgnoreCase))
                        wanted[t.Id] = t;
                }
            }

            var stale = new List<MirrorWorker>();
            foreach (KeyValuePair<string, MirrorWorker> pair in workers)
            {
                if (!wanted.ContainsKey(pair.Key) || !string.Equals(pair.Value.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
                    stale.Add(pair.Value);
            }
            Stop(stale);

            int now = Environment.TickCount;
            foreach (TargetSetting t in wanted.Values)
            {
                MirrorWorker w = Get(t.Id);
                if (w != null && w.Ended)
                {
                    // Leave the failed worker in place (so its error stays visible) until it's time to retry.
                    int fails = CountFailure(w);
                    if (unchecked(now - w.EndedTick) < RetryDelayMs(fails)) continue;
                    workers.Remove(t.Id);
                    w = null;
                }
                if (w == null)
                {
                    w = new MirrorWorker(sourceId, t.Id, t.Name, bufferMs, safetyMs);
                    w.DelayMs = t.DelayMs;
                    w.VolumePercent = t.Volume;
                    workers[t.Id] = w;
                    w.Start();
                }
                else
                {
                    w.DelayMs = t.DelayMs;
                    w.VolumePercent = t.Volume;
                }
            }
        }

        public void StopAll()
        {
            Stop(new List<MirrorWorker>(workers.Values));
        }

        void Stop(List<MirrorWorker> list)
        {
            foreach (MirrorWorker w in list) w.RequestStop();
            foreach (MirrorWorker w in list)
            {
                w.Join();
                workers.Remove(w.TargetId);
            }
        }

        int CountFailure(MirrorWorker w)
        {
            int fails;
            failures.TryGetValue(w.TargetId, out fails);
            if (w.FailureCounted) return fails;
            w.FailureCounted = true;
            // A worker that ran fine for a while starts the count again.
            bool ranLong = unchecked(w.EndedTick - w.StartedTick) > 15000;
            fails = ranLong ? 1 : fails + 1;
            failures[w.TargetId] = fails;
            return fails;
        }

        static int RetryDelayMs(int fails)
        {
            if (fails <= 2) return 2000;
            if (fails <= 5) return 5000;
            return 15000;
        }
    }
}
