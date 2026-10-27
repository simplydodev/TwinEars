using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TwinEars
{
    internal enum MirrorState { Starting, Waiting, Playing }

    // Copies whatever Windows is playing on the source device to one target device.
    //
    // One worker = one thread = one loopback capture + one render stream, so a misbehaving device
    // can only ever break its own copy. The source device itself is never written to or changed:
    // loopback capture is a read-only tap that Windows provides for exactly this purpose.
    internal sealed class MirrorWorker
    {
        public const int MaxDelayMs = 1000;
        const int StallMs = 5000;            // a device that consumes nothing for this long is restarted
        const float SoundThreshold = 3e-5f;  // about -90 dB; anything quieter counts as silence

        public readonly string SourceId;
        public readonly string TargetId;
        public readonly string Label;
        readonly int bufferMs;
        readonly int safetyMs;

        // Adjustable live from the UI thread.
        public volatile int DelayMs;
        public volatile int VolumePercent = 100;

        // Close the output after this long without sound, so the device and the laptop can sleep.
        // Long enough that pausing a video doesn't make a Bluetooth device reconnect.
        public int IdleCloseMs = 20000;

        public volatile MirrorState State = MirrorState.Starting;
        // Audio is arriving from the main output but it has been pure silence for a while. Normal when
        // nothing plays, but it's also what happens when another app locks the main output (exclusive mode).
        public volatile bool HearingSilence;
        // Self-test only: treat digital silence as sound, so the whole path can be tested inaudibly.
        public volatile bool SelfTestForceActive;
        public volatile string Error;
        public volatile bool Ended;
        public int EndedTick;
        public int StartedTick;
        public bool FailureCounted;   // used by MirrorManager on the UI thread only

        // Diagnostics for the self-test. Informational only, so not synchronised.
        public string FormatText = "";
        public bool UsesRateAdjust;
        public double BufferMsActual;
        public double LatencyErrorMs;
        public double Ratio = 1.0;
        public int Underruns;
        public int Resyncs;
        public long FramesIn;
        public long FramesOut;
        public volatile bool OutputOpen;
        public int OutputOpens;

        volatile bool stopRequested;
        readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        Thread thread;

        WaveFormatInfo format;
        RingBuffer ring;
        float[] convertBuffer = new float[0];
        float[] outBuffer = new float[0];
        short[] int16Buffer = new short[0];
        int[] int32Buffer = new int[0];
        byte[] byteBuffer = new byte[0];

        int lastPacketTick;
        int lastSoundTick;
        bool buffering;
        int lastTarget;
        double fillAverage;
        int controlCounter;
        double slip;
        float lastRateSet;
        float currentGain = -1f;
        int lastPaddingAfterWrite;
        int lastProgressTick;

        public MirrorWorker(string sourceId, string targetId, string label, int bufferMs, int safetyMs)
        {
            SourceId = sourceId;
            TargetId = targetId;
            Label = label;
            this.bufferMs = bufferMs;
            this.safetyMs = safetyMs;
        }

        public void Start()
        {
            StartedTick = Environment.TickCount;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "TwinEars mirror";
            thread.Priority = ThreadPriority.AboveNormal;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void RequestStop()
        {
            stopRequested = true;
            stopEvent.Set();
        }

        public void Join()
        {
            if (thread != null) thread.Join(2000);
        }

        void Run()
        {
            IntPtr mmcss = IntPtr.Zero;
            IMMDeviceEnumerator enumerator = null;
            IMMDevice sourceDevice = null;
            IAudioClient captureClient = null;
            IAudioCaptureClient capture = null;
            IntPtr mixFormat = IntPtr.Zero;
            RenderStream output = null;
            try
            {
                try
                {
                    uint taskIndex = 0;
                    mmcss = Native.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
                }
                catch (Exception) { }

                Log.Write("[" + Label + "] opening main output");
                enumerator = Wasapi.CreateEnumerator();
                enumerator.GetDevice(SourceId, out sourceDevice);
                captureClient = Wasapi.Activate(sourceDevice);
                Log.Write("[" + Label + "] activated");
                captureClient.GetMixFormat(out mixFormat);
                format = WaveFormatInfo.FromPointer(mixFormat);
                FormatText = format.ToString();
                if (format.Kind == SampleKind.Unsupported) throw new NotSupportedException("the main output uses an unsupported audio format");

                captureClient.Initialize(Wasapi.ShareModeShared, Wasapi.StreamFlagsLoopback, 200 * 10000L, 0, mixFormat, IntPtr.Zero);
                Log.Write("[" + Label + "] loopback initialized");
                capture = (IAudioCaptureClient)Wasapi.GetService(captureClient, Wasapi.IID_IAudioCaptureClient);
                ring = new RingBuffer(format.Channels, format.Rate * 3);
                captureClient.Start();
                lastPacketTick = Environment.TickCount - IdleCloseMs;
                lastSoundTick = lastPacketTick;
                State = MirrorState.Waiting;
                Log.Write("[" + Label + "] started, main output format " + FormatText);

                while (!stopRequested)
                {
                    if (output == null)
                    {
                        // Idle: only the read-only capture is open. Wait for actual sound.
                        // (Some PCs deliver a constant stream of digital silence, so packets alone mean nothing.)
                        stopEvent.WaitOne(25);
                        if (Drain(capture) && !stopRequested)
                        {
                            output = RenderStream.Open(enumerator, TargetId, format, bufferMs);
                            OnOutputOpened(output);
                        }
                        else
                        {
                            ring.Clear();
                        }
                    }
                    else
                    {
                        output.Event.WaitOne(20);
                        Drain(capture);
                        Pump(output);
                        if (unchecked(Environment.TickCount - lastSoundTick) > IdleCloseMs)
                        {
                            // Closing the stream lets the device (and the laptop) go to sleep normally.
                            output.Dispose();
                            output = null;
                            OutputOpen = false;
                            ring.Clear();
                            Log.Write("[" + Label + "] nothing playing, output closed until sound returns");
                        }
                    }
                    int now = Environment.TickCount;
                    State = output != null && unchecked(now - lastSoundTick) < 1500 ? MirrorState.Playing : MirrorState.Waiting;
                    HearingSilence = unchecked(now - lastPacketTick) < 1000 && unchecked(now - lastSoundTick) > 3000;
                }
            }
            catch (Exception ex)
            {
                if (!stopRequested)
                {
                    Error = Wasapi.Describe(ex);
                    Log.Write("[" + Label + "] stopped: " + Error + " | " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            finally
            {
                if (output != null) output.Dispose();
                OutputOpen = false;
                if (captureClient != null)
                {
                    try { captureClient.Stop(); }
                    catch (Exception) { }
                }
                Wasapi.Release(capture);
                Wasapi.Release(captureClient);
                Wasapi.Release(sourceDevice);
                Wasapi.Release(enumerator);
                if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
                if (mmcss != IntPtr.Zero)
                {
                    try { Native.AvRevertMmThreadCharacteristics(mmcss); }
                    catch (Exception) { }
                }
                EndedTick = Environment.TickCount;
                Ended = true;
            }
        }

        void OnOutputOpened(RenderStream output)
        {
            UsesRateAdjust = output.Clock != null;
            BufferMsActual = output.BufferFrames * 1000.0 / format.Rate;
            buffering = true;
            lastTarget = -1;
            fillAverage = -1;
            controlCounter = 0;
            slip = 0;
            lastRateSet = 0;
            Ratio = 1.0;
            lastPaddingAfterWrite = output.BufferFrames;
            lastProgressTick = Environment.TickCount;
            OutputOpen = true;
            OutputOpens++;
            Log.Write("[" + Label + "] output opened, buffer " + BufferMsActual.ToString("0.0") + " ms, drift correction " + (UsesRateAdjust ? "by Windows resampler" : "by sample slipping"));
        }

        // Total queued audio (our ring + the device buffer) is held at "target", which is what keeps
        // the delay constant. Everything below is about holding that number steady.
        int TargetFrames(RenderStream output)
        {
            int delay = Math.Max(0, Math.Min(MaxDelayMs, DelayMs));
            return output.BufferFrames + (int)((long)(safetyMs + delay) * format.Rate / 1000);
        }

        void Pump(RenderStream output)
        {
            uint paddingFrames;
            output.Client.GetCurrentPadding(out paddingFrames);
            int padding = (int)paddingFrames;
            int now = Environment.TickCount;

            // Watchdog: a healthy device always consumes something within a few seconds.
            if (padding < lastPaddingAfterWrite || padding == 0) lastProgressTick = now;
            else if (unchecked(now - lastProgressTick) > StallMs) throw new TimeoutException("the device stopped playing (stalled)");

            int target = TargetFrames(output);
            if (target != lastTarget)
            {
                // Delay changed: jump straight to the new value instead of drifting there slowly.
                if (lastTarget >= 0 && !buffering)
                {
                    int queued = ring.Count + padding;
                    if (queued > target) ring.Skip(queued - target);
                    else buffering = true;
                }
                lastTarget = target;
                fillAverage = -1;
            }

            if (buffering && ring.Count + padding >= target)
            {
                ring.Skip(ring.Count + padding - target);
                buffering = false;
                fillAverage = -1;
                SetRatio(output, 1.0);
            }

            int free = output.BufferFrames - padding;
            int written = 0;
            if (free > 0)
            {
                if (buffering)
                {
                    // Still building up the delay; silence already queued counts towards it.
                    output.WriteSilence(free);
                    written = free;
                }
                else
                {
                    written = Math.Min(free, ring.Count);
                    if (written > 0) WriteAudio(output, written);
                    if (ring.Count == 0 && padding + written < output.BufferFrames / 2)
                    {
                        // Ran dry: the main output paused or hiccupped. Fill the gap with silence and
                        // rebuild the full delay before resuming, so both devices stay in sync.
                        if (free > written) output.WriteSilence(free - written);
                        written = free;
                        buffering = true;
                        if (unchecked(now - lastPacketTick) < 200) Underruns++;
                    }
                }
            }
            lastPaddingAfterWrite = padding + written;

            if (!buffering)
            {
                double queuedNow = ring.Count + padding + written;
                fillAverage = fillAverage < 0 ? queuedNow : fillAverage + 0.01 * (queuedNow - fillAverage);
                if (++controlCounter >= 10)
                {
                    controlCounter = 0;
                    Regulate(output, target);
                }
            }
        }

        // The two devices run on separate clocks that drift apart by a few hundred parts per million.
        // Nudge the playback speed by a tiny amount (never more than 0.5%, which is inaudible) to hold the
        // queue at its target; only jump if something went badly wrong.
        void Regulate(RenderStream output, int target)
        {
            double errorFrames = fillAverage - target;
            double errorMs = errorFrames * 1000.0 / format.Rate;
            LatencyErrorMs = errorMs;
            if (errorMs > 60)
            {
                ring.Skip((int)errorFrames);
                fillAverage = -1;
                Resyncs++;
                SetRatio(output, 1.0);
                return;
            }
            if (errorMs < -60)
            {
                buffering = true;
                Resyncs++;
                SetRatio(output, 1.0);
                return;
            }
            SetRatio(output, 1.0 + Math.Max(-0.005, Math.Min(0.005, errorMs * 0.0001)));
        }

        void SetRatio(RenderStream output, double ratio)
        {
            Ratio = ratio;
            if (output.Clock == null) return;
            float rate = (float)(format.Rate * ratio);
            if (rate == lastRateSet) return;
            try
            {
                output.Clock.SetSampleRate(rate);
                lastRateSet = rate;
            }
            catch (Exception)
            {
                // Fall back to sample slipping for the rest of this stream.
                output.DisableClock();
                UsesRateAdjust = false;
            }
        }

        void WriteAudio(RenderStream output, int frames)
        {
            int channels = format.Channels;
            Ensure(ref outBuffer, frames * channels);
            if (output.Clock == null && Ratio != 1.0)
            {
                // No rate adjustment from Windows: drop or repeat one sample frame now and then instead.
                slip += (Ratio - 1.0) * frames;
                slip = Math.Max(-2.0, Math.Min(2.0, slip));
            }
            if (slip >= 1.0 && ring.Count > frames)
            {
                ring.Skip(1);
                slip -= 1.0;
            }
            if (slip <= -1.0 && frames > 1)
            {
                ring.Read(outBuffer, channels, frames - 1);
                Array.Copy(outBuffer, channels, outBuffer, 0, channels);
                slip += 1.0;
            }
            else
            {
                ring.Read(outBuffer, 0, frames);
            }
            ApplyGain(outBuffer, frames, channels);
            output.Write(outBuffer, frames, channels);
            FramesOut += frames;
        }

        void ApplyGain(float[] buffer, int frames, int channels)
        {
            float v = Math.Max(0, Math.Min(100, VolumePercent)) / 100f;
            float target = v * v;   // roughly perceptual: 50% sounds about half as loud
            float start = currentGain < 0 ? target : currentGain;
            currentGain = target;
            if (start == 1f && target == 1f) return;
            float step = (target - start) / frames;   // ramp to avoid clicks while dragging the slider
            float gain = start;
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                gain += step;
                for (int c = 0; c < channels; c++) buffer[i++] *= gain;
            }
        }

        // Moves everything captured so far into the ring. Returns true if any of it was audible.
        bool Drain(IAudioCaptureClient capture)
        {
            int total = 0;
            bool sound = false;
            uint next;
            capture.GetNextPacketSize(out next);
            while (next > 0 && !stopRequested)
            {
                IntPtr data;
                uint frames, flags;
                ulong devicePosition, qpcPosition;
                capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition);
                int n = (int)frames;
                if (n > 0)
                {
                    if ((flags & Wasapi.BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                    {
                        ring.WriteSilence(n);
                    }
                    else
                    {
                        float[] samples = Convert(data, n);
                        if (!sound) sound = HasSound(samples, n * format.Channels);
                        ring.Write(samples, n);
                    }
                    total += n;
                }
                capture.ReleaseBuffer(frames);
                capture.GetNextPacketSize(out next);
            }
            int now = Environment.TickCount;
            if (total > 0)
            {
                lastPacketTick = now;
                FramesIn += total;
                if (SelfTestForceActive) sound = true;
            }
            if (sound) lastSoundTick = now;
            return sound;
        }

        internal static bool HasSound(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
                if (samples[i] > SoundThreshold || samples[i] < -SoundThreshold) return true;
            return false;
        }

        float[] Convert(IntPtr data, int frames)
        {
            int samples = frames * format.Channels;
            Ensure(ref convertBuffer, samples);
            switch (format.Kind)
            {
                case SampleKind.Float32:
                    Marshal.Copy(data, convertBuffer, 0, samples);
                    break;
                case SampleKind.Int16:
                    Ensure(ref int16Buffer, samples);
                    Marshal.Copy(data, int16Buffer, 0, samples);
                    for (int i = 0; i < samples; i++) convertBuffer[i] = int16Buffer[i] / 32768f;
                    break;
                case SampleKind.Int32:
                    Ensure(ref int32Buffer, samples);
                    Marshal.Copy(data, int32Buffer, 0, samples);
                    for (int i = 0; i < samples; i++) convertBuffer[i] = int32Buffer[i] / 2147483648f;
                    break;
                case SampleKind.Int24:
                    Ensure(ref byteBuffer, samples * 3);
                    Marshal.Copy(data, byteBuffer, 0, samples * 3);
                    for (int i = 0, b = 0; i < samples; i++, b += 3)
                        convertBuffer[i] = ((byteBuffer[b] << 8) | (byteBuffer[b + 1] << 16) | (byteBuffer[b + 2] << 24)) / 2147483648f;
                    break;
            }
            return convertBuffer;
        }

        static void Ensure<T>(ref T[] array, int size)
        {
            if (array.Length < size) array = new T[Math.Max(size, array.Length * 2)];
        }
    }

    // A shared-mode output stream on one device, in 32-bit float at the source's rate and layout.
    // Windows converts it to whatever the device actually uses (AUTOCONVERTPCM).
    internal sealed class RenderStream : IDisposable
    {
        public IMMDevice Device;
        public IAudioClient Client;
        public IAudioRenderClient Render;
        public IAudioClockAdjustment Clock;
        public readonly AutoResetEvent Event = new AutoResetEvent(false);
        public int BufferFrames;
        IntPtr format;

        public static RenderStream Open(IMMDeviceEnumerator enumerator, string deviceId, WaveFormatInfo source, int bufferMs)
        {
            var s = new RenderStream();
            try
            {
                enumerator.GetDevice(deviceId, out s.Device);
                s.format = WaveFormatInfo.AllocFloat(source.Rate, source.Channels, source.ChannelMask);
                long duration = Math.Max(10, bufferMs) * 10000L;
                // NOPERSIST: Windows won't remember a volume for this stream, so nothing is left behind.
                uint flags = Wasapi.StreamFlagsEventCallback | Wasapi.StreamFlagsAutoConvertPcm |
                             Wasapi.StreamFlagsSrcDefaultQuality | Wasapi.StreamFlagsNoPersist;
                s.Client = Wasapi.Activate(s.Device);
                try
                {
                    s.Client.Initialize(Wasapi.ShareModeShared, flags | Wasapi.StreamFlagsRateAdjust, duration, 0, s.format, IntPtr.Zero);
                    s.Clock = (IAudioClockAdjustment)Wasapi.GetService(s.Client, Wasapi.IID_IAudioClockAdjustment);
                    s.Clock.SetSampleRate(source.Rate);
                }
                catch (Exception)
                {
                    // Rate adjustment isn't available here; open a plain stream instead.
                    s.DisableClock();
                    Wasapi.Release(s.Client);
                    s.Client = Wasapi.Activate(s.Device);
                    s.Client.Initialize(Wasapi.ShareModeShared, flags, duration, 0, s.format, IntPtr.Zero);
                }
                s.Client.SetEventHandle(s.Event.SafeWaitHandle.DangerousGetHandle());
                uint frames;
                s.Client.GetBufferSize(out frames);
                s.BufferFrames = (int)frames;
                s.Render = (IAudioRenderClient)Wasapi.GetService(s.Client, Wasapi.IID_IAudioRenderClient);
                s.WriteSilence(s.BufferFrames);
                s.Client.Start();
                return s;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }

        public void Write(float[] samples, int frames, int channels)
        {
            IntPtr p;
            Render.GetBuffer((uint)frames, out p);
            Marshal.Copy(samples, 0, p, frames * channels);
            Render.ReleaseBuffer((uint)frames, 0);
        }

        public void WriteSilence(int frames)
        {
            IntPtr p;
            Render.GetBuffer((uint)frames, out p);
            Render.ReleaseBuffer((uint)frames, Wasapi.BufferFlagsSilent);
        }

        public void DisableClock()
        {
            Wasapi.Release(Clock);
            Clock = null;
        }

        public void Dispose()
        {
            if (Client != null)
            {
                try { Client.Stop(); }
                catch (Exception) { }
            }
            DisableClock();
            Wasapi.Release(Render);
            Render = null;
            Wasapi.Release(Client);
            Client = null;
            Wasapi.Release(Device);
            Device = null;
            if (format != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(format);
                format = IntPtr.Zero;
            }
            Event.Close();
        }
    }

    // Fixed-capacity FIFO of interleaved float frames. When full, the oldest audio is dropped.
    internal sealed class RingBuffer
    {
        readonly float[] data;
        readonly int channels;
        readonly int capacity;
        int start;
        int count;

        public RingBuffer(int channels, int capacityFrames)
        {
            this.channels = channels;
            capacity = Math.Max(1, capacityFrames);
            data = new float[capacity * channels];
        }

        public int Count { get { return count; } }

        public void Clear()
        {
            start = 0;
            count = 0;
        }

        public void Skip(int frames)
        {
            if (frames <= 0) return;
            if (frames > count) frames = count;
            start = (start + frames) % capacity;
            count -= frames;
        }

        public void Write(float[] source, int frames)
        {
            Append(source, frames, false);
        }

        public void WriteSilence(int frames)
        {
            Append(null, frames, true);
        }

        void Append(float[] source, int frames, bool silence)
        {
            int offset = 0;
            if (frames > capacity)
            {
                offset = (frames - capacity) * channels;
                frames = capacity;
            }
            int overflow = count + frames - capacity;
            if (overflow > 0) Skip(overflow);
            int end = (start + count) % capacity;
            int first = Math.Min(frames, capacity - end);
            CopyIn(source, offset, end, first, silence);
            if (frames > first) CopyIn(source, offset + first * channels, 0, frames - first, silence);
            count += frames;
        }

        void CopyIn(float[] source, int sourceOffset, int frameIndex, int frames, bool silence)
        {
            if (silence) Array.Clear(data, frameIndex * channels, frames * channels);
            else Array.Copy(source, sourceOffset, data, frameIndex * channels, frames * channels);
        }

        public int Read(float[] dest, int destOffset, int frames)
        {
            if (frames > count) frames = count;
            int first = Math.Min(frames, capacity - start);
            Array.Copy(data, start * channels, dest, destOffset, first * channels);
            if (frames > first) Array.Copy(data, 0, dest, destOffset + first * channels, (frames - first) * channels);
            Skip(frames);
            return frames;
        }
    }
}
