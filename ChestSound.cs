using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LootLedger
{
    public sealed class ChestSound
    {
        private const int Rate = 11025, Window = 256, Hop = 128, Bands = 22;
        private readonly float[][] template;
        private readonly float[][] history;
        private readonly double[] samples = new double[Window];
        private readonly double[] real = new double[Window], imaginary = new double[Window];
        private readonly double[] hann = new double[Window];
        private readonly int[] bandForBin = new int[Window / 2];
        private int samplePosition, sampleCount, historyPosition, historyCount;
        private long lastHit = -10000;
        private double priorScore, earlierScore;
        private int downsampleCount;
        private double downsampleSum;

        private ChestSound(float[][] template)
        {
            this.template = template;
            history = template == null ? null : new float[template.Length][];
            for (int i = 0; i < Window; i++)
                hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1));
            for (int bin = 0; bin < bandForBin.Length; bin++)
            {
                double frequency = bin * Rate / (double)Window;
                bandForBin[bin] = frequency < 215 || frequency > 4700 ? -1 :
                    Math.Min(Bands - 1, (int)(Bands * Math.Log(frequency / 215) / Math.Log(4700.0 / 215)));
            }
        }

        public static ChestSound Load(string path)
        {
            const string requirements = "Use a 16-bit PCM WAV with one chest-opening sound, 0.25–2 seconds long.";
            int rate = 0, channels = 0;
            short[] pcm = null;
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.BaseStream.Length < 44 || reader.BaseStream.Length > 1024 * 1024 ||
                    reader.ReadUInt32() != 0x46464952)
                    throw new InvalidDataException(requirements);
                reader.ReadUInt32();
                if (reader.ReadUInt32() != 0x45564157)
                    throw new InvalidDataException(requirements);
                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    uint tag = reader.ReadUInt32();
                    uint bytes = reader.ReadUInt32();
                    long next = reader.BaseStream.Position + bytes + (bytes & 1);
                    if (next > reader.BaseStream.Length + 1)
                        throw new InvalidDataException(requirements);
                    if (tag == 0x20746D66 && bytes >= 16)
                    {
                        int format = reader.ReadUInt16();
                        channels = reader.ReadUInt16();
                        rate = reader.ReadInt32();
                        reader.ReadUInt32();
                        reader.ReadUInt16();
                        int bits = reader.ReadUInt16();
                        if (format != 1 || channels < 1 || channels > 2 || bits != 16 ||
                            rate < 16000 || rate > 96000)
                            throw new InvalidDataException(requirements);
                    }
                    else if (tag == 0x61746164 && bytes % 2 == 0)
                    {
                        pcm = new short[bytes / 2];
                        for (int i = 0; i < pcm.Length; i++)
                            pcm[i] = reader.ReadInt16();
                    }
                    reader.BaseStream.Position = next;
                }
            }
            if (pcm == null || channels == 0 || pcm.Length % channels != 0 ||
                pcm.Length < rate * channels / 4 || pcm.Length > rate * channels * 2)
                throw new InvalidDataException(requirements);

            var extractor = new ChestSound(null);
            var frames = new List<float[]>();
            var levels = new List<double>();
            double level;
            for (int frame = 0; frame * (double)rate / Rate < pcm.Length / channels; frame++)
            {
                int start = (int)(frame * (double)rate / Rate);
                int end = Math.Min(pcm.Length / channels, (int)((frame + 1) * (double)rate / Rate));
                double sum = 0;
                for (int i = start * channels; i < end * channels; i++)
                    sum += pcm[i];
                float[] feature = extractor.PushSample(sum / ((end - start) * channels * 32768.0), out level);
                if (feature != null)
                {
                    frames.Add(feature);
                    levels.Add(level);
                }
            }
            double peak = 0;
            foreach (double value in levels)
                peak = Math.Max(peak, value);
            if (peak < 0.00001)
                throw new InvalidDataException("The WAV is silent or too quiet.");
            int first = levels.FindIndex(value => value > peak * 0.045);
            int last = levels.FindLastIndex(value => value > peak * 0.045);
            if (last - first < 16)
                throw new InvalidDataException(requirements);
            return new ChestSound(frames.GetRange(first, last - first + 1).ToArray());
        }

        public void FeedStereo(short[] pcm, int frames, bool silent, Stopwatch clock, ConcurrentQueue<long> hits)
        {
            for (int i = 0; i < frames; i++)
            {
                if (!silent)
                    downsampleSum += (pcm[2 * i] + pcm[2 * i + 1]) / 65536.0;
                if (++downsampleCount < 4)
                    continue;
                double level;
                float[] feature = PushSample(downsampleSum / 4, out level);
                downsampleCount = 0;
                downsampleSum = 0;
                if (feature == null)
                    continue;
                history[historyPosition] = feature;
                historyPosition = (historyPosition + 1) % history.Length;
                historyCount = Math.Min(historyCount + 1, history.Length);
                if (historyCount < history.Length)
                    continue;

                double score = 0;
                for (int t = 0; t < template.Length; t++)
                {
                    float[] observed = history[(historyPosition + t) % history.Length];
                    for (int band = 0; band < Bands; band++)
                        score += observed[band] * template[t][band];
                }
                score /= template.Length;
                long time = clock.ElapsedMilliseconds;
                // Report the local maximum once. A short holdoff suppresses alternate alignments.
                if (priorScore >= 0.80 && priorScore > earlierScore && priorScore >= score && time - lastHit > 500)
                {
                    long onset = time - (template.Length * Hop * 1000L / Rate);
                    hits.Enqueue(Math.Max(0, onset));
                    lastHit = time;
                }
                earlierScore = priorScore;
                priorScore = score;
            }
        }

        private float[] PushSample(double sample, out double level)
        {
            level = 0;
            samples[samplePosition] = sample;
            samplePosition = (samplePosition + 1) % Window;
            sampleCount++;
            if (sampleCount < Window || sampleCount % Hop != 0)
                return null;

            for (int i = 0, reversed = 0; i < Window; i++)
            {
                double value = samples[(samplePosition + i) % Window];
                level += value * value;
                real[reversed] = value * hann[i];
                imaginary[reversed] = 0;
                int bit = Window / 2;
                while (bit > 0 && (reversed & bit) != 0)
                {
                    reversed ^= bit;
                    bit >>= 1;
                }
                reversed ^= bit;
            }
            level /= Window;
            var feature = new float[Bands];
            if (level < 0.0000005)
                return feature;

            for (int span = 2; span <= Window; span *= 2)
            {
                double angle = -2 * Math.PI / span;
                double stepReal = Math.Cos(angle), stepImaginary = Math.Sin(angle);
                for (int block = 0; block < Window; block += span)
                {
                    double wr = 1, wi = 0;
                    for (int i = block; i < block + span / 2; i++)
                    {
                        int j = i + span / 2;
                        double r = wr * real[j] - wi * imaginary[j];
                        double im = wr * imaginary[j] + wi * real[j];
                        real[j] = real[i] - r;
                        imaginary[j] = imaginary[i] - im;
                        real[i] += r;
                        imaginary[i] += im;
                        double previousReal = wr;
                        wr = wr * stepReal - wi * stepImaginary;
                        wi = previousReal * stepImaginary + wi * stepReal;
                    }
                }
            }
            for (int bin = 0; bin < bandForBin.Length; bin++)
                if (bandForBin[bin] >= 0)
                    feature[bandForBin[bin]] += (float)(real[bin] * real[bin] + imaginary[bin] * imaginary[bin]);
            double norm = 0;
            for (int i = 0; i < Bands; i++)
            {
                feature[i] = (float)Math.Pow(feature[i], 0.32);
                norm += feature[i] * feature[i];
            }
            norm = Math.Sqrt(norm);
            if (norm > 0)
                for (int i = 0; i < Bands; i++)
                    feature[i] /= (float)norm;
            return feature;
        }
    }

    public sealed class ApplicationAudio : IDisposable
    {
        private readonly Thread thread;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private volatile string status = "Starting audio";
        public string Status { get { return status; } }

        public ApplicationAudio(int pid, string wav, Stopwatch clock, ConcurrentQueue<long> hits)
        {
            thread = new Thread(() =>
            {
                try { Capture(pid, ChestSound.Load(wav), clock, hits); }
                catch (Exception error) { status = error.Message; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Dispose()
        {
            stop.Set();
            thread.Join();
            stop.Dispose();
        }

        private void Capture(int pid, ChestSound sound, Stopwatch clock, ConcurrentQueue<long> hits)
        {
            var callback = new Activation();
            var parameters = new ProcessParameters { Type = 1, ProcessId = pid, Mode = 0 };
            IntPtr payload = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ProcessParameters)));
            IActivationOperation operation = null;
            IAudioClient client = null;
            IAudioCapture capture = null;
            bool started = false;
            try
            {
                Marshal.StructureToPtr(parameters, payload, false);
                var variant = new BlobVariant { Type = 65, Size = 12, Data = payload };
                Guid iid = typeof(IAudioClient).GUID;
                Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(@"VAD\Process_Loopback",
                    ref iid, ref variant, callback, out operation));
                if (!callback.Completed.Wait(2000))
                    throw new TimeoutException("Windows audio capture did not start.");
                Marshal.ThrowExceptionForHR(callback.Result);
                client = (IAudioClient)callback.Client;
                using (var ready = new AutoResetEvent(false))
                {
                    var format = new WaveFormat { Tag = 1, Channels = 2, Rate = 44100,
                        BytesPerSecond = 176400, BlockAlign = 4, Bits = 16 };
                    const uint flags = 0x80060000; // Auto-convert PCM, event callback, loopback.
                    Marshal.ThrowExceptionForHR(client.Initialize(0, flags, 0, 0, ref format, IntPtr.Zero));
                    iid = typeof(IAudioCapture).GUID;
                    object service;
                    Marshal.ThrowExceptionForHR(client.GetService(ref iid, out service));
                    capture = (IAudioCapture)service;
                    Marshal.ThrowExceptionForHR(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
                    Marshal.ThrowExceptionForHR(client.Start());
                    started = true;
                    status = "Listening";
                    var pcm = new short[4096];
                    var waits = new WaitHandle[] { stop, ready };
                    while (WaitHandle.WaitAny(waits) == 1)
                    {
                        int frames;
                        Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out frames));
                        while (frames > 0)
                        {
                            IntPtr data;
                            int bufferFlags;
                            Marshal.ThrowExceptionForHR(capture.GetBuffer(out data, out frames,
                                out bufferFlags, IntPtr.Zero, IntPtr.Zero));
                            try
                            {
                                bool silent = (bufferFlags & 2) != 0;
                                if (pcm.Length < frames * 2)
                                    pcm = new short[frames * 2];
                                if (!silent)
                                    Marshal.Copy(data, pcm, 0, frames * 2);
                                sound.FeedStereo(pcm, frames, silent, clock, hits);
                            }
                            finally { Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames)); }
                            Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out frames));
                        }
                    }
                    client.Stop();
                    started = false;
                }
            }
            finally
            {
                if (started)
                    client.Stop();
                if (capture != null)
                    Marshal.ReleaseComObject(capture);
                if (client != null)
                    Marshal.ReleaseComObject(client);
                if (operation != null)
                    Marshal.ReleaseComObject(operation);
                Marshal.FreeHGlobal(payload);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessParameters { public int Type, ProcessId, Mode; }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct BlobVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public int Size;
            [FieldOffset(16)] public IntPtr Data;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort Tag, Channels;
            public int Rate, BytesPerSecond;
            public ushort BlockAlign, Bits, Extra;
        }

        [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid,
            ref BlobVariant parameters, IActivationHandler handler, out IActivationOperation operation);

        [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
            InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivationHandler
        {
            [PreserveSig] int ActivateCompleted(IActivationOperation operation);
        }
        [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
            InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IActivationOperation
        {
            [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object client);
        }
        [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"),
            InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAgileObject { }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class Activation : IActivationHandler, IAgileObject
        {
            public readonly ManualResetEventSlim Completed = new ManualResetEventSlim();
            public int Result;
            public object Client;
            public int ActivateCompleted(IActivationOperation operation)
            {
                int result = operation.GetActivateResult(out Result, out Client);
                if (result < 0)
                    Result = result;
                Completed.Set();
                return 0;
            }
        }
        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
            InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity,
                ref WaveFormat format, IntPtr session);
            [PreserveSig] int GetBufferSize(out int frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out int frames);
            [PreserveSig] int IsFormatSupported(int mode, IntPtr format, IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }
        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
            InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCapture
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags,
                IntPtr position, IntPtr timestamp);
            [PreserveSig] int ReleaseBuffer(int frames);
            [PreserveSig] int GetNextPacketSize(out int frames);
        }
    }
}
