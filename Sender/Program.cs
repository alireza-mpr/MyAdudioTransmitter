using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace FastSender
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Fast Audio Sender");

            string filePath;
            double pilotSeconds = 5.0;
            int baud = 1000; // با ریسیور sync

            if (args.Length >= 1)
                filePath = args[0];
            else
            {
                Console.Write("Enter file path: ");
                filePath = Console.ReadLine()?.Trim('"');
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                Console.WriteLine("File not found.");
                return;
            }

            if (args.Length >= 2 && double.TryParse(args[1], out var p) && p >= 1.0)
                pilotSeconds = p;
            if (args.Length >= 3 && int.TryParse(args[2], out var b) && b > 100 && b <= 5000)
                baud = b;

            byte[] data = File.ReadAllBytes(filePath);
            Console.WriteLine($"File: {filePath}, {data.Length} bytes");

            var cfg = new ModemConfig
            {
                PilotSeconds = pilotSeconds,
                Baud = baud
            };

            var frames = FrameBuilder.BuildFrames(data, cfg);
            Console.WriteLine($"Frames (with repeats): {frames.Count}");

            var bits = new List<int>();
            foreach (var f in frames)
            {
                bits.AddRange(ModemConfig.PreambleBits);
                var fb = f.ToBytes();
                foreach (var bt in fb)
                    for (int i = 7; i >= 0; i--)
                        bits.Add((bt >> i) & 1);
            }

            Console.WriteLine($"Total bits: {bits.Count}");

            var pilot = FskModulator.GeneratePilot(cfg);
            var gap = FskModulator.GenerateSilence(0.2, cfg);
            var dataAudio = FskModulator.GenerateData(bits, cfg);

            var full = new byte[pilot.Length + gap.Length + dataAudio.Length];
            Buffer.BlockCopy(pilot, 0, full, 0, pilot.Length);
            Buffer.BlockCopy(gap, 0, full, pilot.Length, gap.Length);
            Buffer.BlockCopy(dataAudio, 0, full, pilot.Length + gap.Length, dataAudio.Length);

            double seconds = full.Length / (double)(cfg.SampleRate * 2);
            Console.WriteLine($"Pilot: {cfg.PilotSeconds}s @ {cfg.PilotFreq} Hz");
            Console.WriteLine($"Baud={cfg.Baud}, Audio length ≈ {seconds:F1}s");

            Play(full, cfg);

            Console.WriteLine("Done. Press Enter to exit.");
            Console.ReadLine();
        }

        static void Play(byte[] audio, ModemConfig cfg)
        {
            using var ms = new MemoryStream(audio);
            using var rs = new RawSourceWaveStream(ms, cfg.GetWaveFormat());
            using var wo = new WaveOutEvent();
            wo.Volume = 1.0f;
            wo.Init(rs);
            wo.Play();
            while (wo.PlaybackState == PlaybackState.Playing)
                System.Threading.Thread.Sleep(100);
        }
    }

    class ModemConfig
    {
        public int SampleRate = 48000;
        public int BitsPerSample = 16;
        public int Channels = 1;

        public int Baud = 1000;
        public double Freq0 = 4000.0;
        public double Freq1 = 8000.0;

        public double PilotFreq = 1000.0;
        public double PilotSeconds = 5.0;

        public int FramePayloadSize = 128;
        public int FrameRepeats = 2;

        public static readonly int[] PreambleBits;

        static ModemConfig()
        {
            uint pre = 0x55AA55AA;
            var bits = new List<int>();
            for (int i = 31; i >= 0; i--)
                bits.Add((int)((pre >> i) & 1));
            PreambleBits = bits.ToArray();
        }

        public WaveFormat GetWaveFormat()
            => new WaveFormat(SampleRate, BitsPerSample, Channels);
    }

    class Frame
    {
        public byte Type;      // 0=data, 1=eof
        public ushort Index;
        public ushort Length;
        public byte[] Payload;

        public byte[] ToBytes()
        {
            using var ms = new MemoryStream();
            ms.WriteByte(Type);
            ms.WriteByte((byte)(Index >> 8));
            ms.WriteByte((byte)(Index & 0xFF));
            ms.WriteByte((byte)(Length >> 8));
            ms.WriteByte((byte)(Length & 0xFF));

            if (Payload != null && Payload.Length > 0)
                ms.Write(Payload, 0, Payload.Length);

            var body = ms.ToArray();
            uint crc = Crc32.Compute(body);

            ms.WriteByte((byte)((crc >> 24) & 0xFF));
            ms.WriteByte((byte)((crc >> 16) & 0xFF));
            ms.WriteByte((byte)((crc >> 8) & 0xFF));
            ms.WriteByte((byte)(crc & 0xFF));

            return ms.ToArray();
        }
    }

    static class FrameBuilder
    {
        public static List<Frame> BuildFrames(byte[] data, ModemConfig cfg)
        {
            var frames = new List<Frame>();
            int offset = 0;
            ushort index = 0;

            while (offset < data.Length)
            {
                int len = Math.Min(cfg.FramePayloadSize, data.Length - offset);
                var payload = new byte[len];
                Buffer.BlockCopy(data, offset, payload, 0, len);

                var f = new Frame
                {
                    Type = 0,
                    Index = index,
                    Length = (ushort)len,
                    Payload = payload
                };

                for (int r = 0; r < cfg.FrameRepeats; r++)
                    frames.Add(f);

                offset += len;
                index++;
            }

            var eof = new Frame
            {
                Type = 1,
                Index = index,
                Length = 0,
                Payload = Array.Empty<byte>()
            };

            for (int r = 0; r < cfg.FrameRepeats; r++)
                frames.Add(eof);

            return frames;
        }
    }

    static class FskModulator
    {
        public static byte[] GeneratePilot(ModemConfig cfg)
        {
            int totalSamples = (int)(cfg.SampleRate * cfg.PilotSeconds);
            var samples = new short[totalSamples];
            double w = 2.0 * Math.PI * cfg.PilotFreq / cfg.SampleRate;

            for (int n = 0; n < totalSamples; n++)
                samples[n] = (short)(Math.Sin(w * n) * short.MaxValue * 0.7);

            return ShortsToBytes(samples);
        }

        public static byte[] GenerateSilence(double sec, ModemConfig cfg)
        {
            int totalSamples = (int)(cfg.SampleRate * sec);
            return new byte[totalSamples * 2];
        }

        public static byte[] GenerateData(List<int> bits, ModemConfig cfg)
        {
            int spb = cfg.SampleRate / cfg.Baud; // 48000/1000 = 48
            var samples = new short[bits.Count * spb];

            double w0 = 2.0 * Math.PI * cfg.Freq0 / cfg.SampleRate;
            double w1 = 2.0 * Math.PI * cfg.Freq1 / cfg.SampleRate;

            int idx = 0;
            foreach (var bit in bits)
            {
                double w = bit == 0 ? w0 : w1;
                for (int n = 0; n < spb; n++)
                    samples[idx++] = (short)(Math.Sin(w * n) * short.MaxValue * 0.7);
            }

            return ShortsToBytes(samples);
        }

        private static byte[] ShortsToBytes(short[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                bytes[2 * i] = (byte)(samples[i] & 0xFF);
                bytes[2 * i + 1] = (byte)((samples[i] >> 8) & 0xFF);
            }
            return bytes;
        }
    }

    static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            const uint poly = 0xEDB88320u;
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                    c = ((c & 1) != 0) ? (poly ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }

        public static uint Compute(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                uint idx = (crc ^ b) & 0xFF;
                crc = Table[idx] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFFu;
        }
    }
}