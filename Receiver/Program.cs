using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace Receiver
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Audio Receiver");

            string outputPath;
            if (args.Length >= 1)
                outputPath = args[0];
            else
            {
                Console.Write("Enter output file name (e.g. received.bin): ");
                outputPath = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(outputPath))
                    outputPath = "received.bin";
            }

            var cfg = new ModemConfig();

            // فایل خروجی را از همان اول باز می‌کنیم؛ هر فریم معتبر به‌ترتیب روی آن نوشته می‌شود.
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var collector = new FrameCollector(cfg, fs);

            var waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(48000, 16, 1) // فرمت ثابت؛ ویندوز/درایور resample می‌کند
            };

            Console.WriteLine($"Capture format: {waveIn.WaveFormat.SampleRate} Hz, {waveIn.WaveFormat.Channels} ch, {waveIn.WaveFormat.BitsPerSample} bit");

            var demod = new FskDemodulator(cfg, waveIn.WaveFormat);

            bool capturing = true;

            DateTime lastActivity = DateTime.UtcNow;
            waveIn.DataAvailable += (s, e) =>
            {
                var bits = demod.Process(e.Buffer, e.BytesRecorded);

                if (!demod.Started)
                    return; // تا زمان detection پایلوت، بیت‌ها را نادیده بگیر

                foreach (var b in bits)
                    collector.FeedBit(b);

                if (collector.WroteData)
                {
                    lastActivity = DateTime.UtcNow;
                    collector.ResetActivityFlag();
                }

                // اگر ۵ ثانیه هیچ دادهٔ معتبری نیامد، ضبط را قطع کن
                if ((DateTime.UtcNow - lastActivity).TotalSeconds > cfg.TimeOutSec)
                {
                    try { waveIn.StopRecording(); } catch { }
                }

                if (collector.Completed)
                {
                    try { waveIn.StopRecording(); } catch { }
                }
            };

            waveIn.RecordingStopped += (s, e) =>
            {
                Console.WriteLine("Recording stopped.");
                capturing = false;
            };

            Console.WriteLine("Listening... (waiting for pilot tone, then data)");
            waveIn.StartRecording();

            while (capturing)
                System.Threading.Thread.Sleep(200);

            if (!collector.SawEof)
            {
                Console.WriteLine("No EOF detected. Partial data (if any) is already in the output file.");
            }
            else
            {
                Console.WriteLine("EOF received. File completed (as far as frames decoded correctly).");
            }

            Console.WriteLine($"Output file: {outputPath}");
            Console.WriteLine("Press Enter to exit.");
            Console.ReadLine();
        }
    }

    class ModemConfig
    {
        public int TimeOutSec = 64;
        public int Baud = 400;
        public double Freq0 = 1200.0;
        public double Freq1 = 2200.0;

        public double PilotFreq = 1000.0;
        public double PilotLockSeconds = 1.0;

        public int FramePayloadSize = 64;
        public int FrameRepeats = 2;

        public static readonly int[] PreambleBits;

        static ModemConfig()
        {
            uint preamble = 0x55AA55AA;
            var bits = new List<int>();
            for (int i = 31; i >= 0; i--)
                bits.Add((int)((preamble >> i) & 1));
            PreambleBits = bits.ToArray();
        }
    }

    class FskDemodulator
    {
        private readonly ModemConfig _cfg;
        private readonly int _sampleRate;
        private readonly int _bytesPerSample;
        private readonly double _samplesPerBit;

        private readonly List<double> _buffer = new();

        private readonly double _w0;
        private readonly double _w1;
        private readonly double _pilotW;

        private double _pilotLockedDuration = 0.0;
        public bool Started { get; private set; } = false;

        public FskDemodulator(ModemConfig cfg, WaveFormat fmt)
        {
            _cfg = cfg;
            _sampleRate = fmt.SampleRate;
            _bytesPerSample = (fmt.BitsPerSample / 8) * fmt.Channels;
            _samplesPerBit = (double)_sampleRate / _cfg.Baud;

            _w0 = 2.0 * Math.PI * _cfg.Freq0 / _sampleRate;
            _w1 = 2.0 * Math.PI * _cfg.Freq1 / _sampleRate;
            _pilotW = 2.0 * Math.PI * _cfg.PilotFreq / _sampleRate;
        }

        public List<int> Process(byte[] buffer, int bytesRecorded)
        {
            var bitsOut = new List<int>();

            int sampleCount = bytesRecorded / _bytesPerSample;
            for (int i = 0; i < sampleCount; i++)
            {
                int idx = i * _bytesPerSample;
                short s = (short)(buffer[idx] | (buffer[idx + 1] << 8));
                _buffer.Add(s);
            }

            if (!Started)
            {
                DetectPilot();
                return bitsOut;
            }

            int win = (int)Math.Round(_samplesPerBit);
            while (_buffer.Count >= win)
            {
                int bit = DetectBit(_buffer, win);
                bitsOut.Add(bit);
                _buffer.RemoveRange(0, win);
            }

            Console.Write(".");
            return bitsOut;
        }

        private void DetectPilot()
        {
            int window = (int)(_sampleRate * 0.25); // 0.25s
            if (_buffer.Count < window)
                return;

            int start = _buffer.Count - window;
            double coeff = 2 * Math.Cos(_pilotW);

            double s0 = 0, s1 = 0;
            double total = 0;

            for (int i = start; i < _buffer.Count; i++)
            {
                double x = _buffer[i];
                total += x * x;

                double y = x + coeff * s0 - s1;
                s1 = s0;
                s0 = y;
            }

            double pilotEnergy = s0 * s0 + s1 * s1 - coeff * s0 * s1;
            double avgEnergy = total / window;

            bool strong =
                avgEnergy > 1e3 &&
                pilotEnergy > avgEnergy * 8;

            if (strong)
                _pilotLockedDuration += 0.25;
            else
                _pilotLockedDuration = Math.Max(0, _pilotLockedDuration - 0.25);

            if (!Started && _pilotLockedDuration >= _cfg.PilotLockSeconds)
            {
                Started = true;
                Console.WriteLine($">>> Pilot detected (~{_pilotLockedDuration:F1}s). Start decoding.");
                _buffer.Clear(); // pilot را دور می‌ریزم، از اینجا به بعد دیتا
            }

            if (!Started && _buffer.Count > _sampleRate * 10)
                _buffer.RemoveRange(0, _buffer.Count - _sampleRate * 10);
        }

        private int DetectBit(List<double> buf, int count)
        {
            double c0 = 2 * Math.Cos(_w0);
            double c1 = 2 * Math.Cos(_w1);
            double s0 = 0, s0_1 = 0;
            double s1 = 0, s1_1 = 0;

            for (int i = 0; i < count; i++)
            {
                double x = buf[i];

                double y0 = x + c0 * s0 - s0_1;
                s0_1 = s0;
                s0 = y0;

                double y1 = x + c1 * s1 - s1_1;
                s1_1 = s1;
                s1 = y1;
            }

            double mag0 = s0 * s0 + s0_1 * s0_1 - c0 * s0 * s0_1;
            double mag1 = s1 * s1 + s1_1 * s1_1 - c1 * s1 * s1_1;

            return mag1 > mag0 ? 1 : 0;
        }
    }

    class FrameCollector
    {
        private readonly int[] _preamble;
        private readonly int _preambleLen;

        private readonly Queue<int> _preWin = new();
        private bool _inFrame = false;
        private readonly List<int> _frameBits = new();

        private readonly FileStream _fs;
        private ushort _nextIndex = 0;  // انتظار فریم بعدی
        public bool SawEof { get; private set; }
        public bool Completed { get; private set; }
        public bool WroteData { get; private set; }

        public FrameCollector(ModemConfig cfg, FileStream fs)
        {
            _preamble = ModemConfig.PreambleBits;
            _preambleLen = _preamble.Length;
            _fs = fs;
        }

        public void ResetActivityFlag()
        {
            WroteData = false;
        }

        public void FeedBit(int bit)
        {
            if (Completed) return;

            if (!_inFrame)
            {
                _preWin.Enqueue(bit);
                if (_preWin.Count > _preambleLen)
                    _preWin.Dequeue();

                if (_preWin.Count == _preambleLen)
                {
                    bool match = true;
                    int i = 0;
                    foreach (var b in _preWin)
                    {
                        if (b != _preamble[i++]) { match = false; break; }
                    }

                    if (match)
                    {
                        _inFrame = true;
                        _frameBits.Clear();
                    }
                }
            }
            else
            {
                _frameBits.Add(bit);

                // حداقل برای هدر+CRC بدون payload: 1+2+2+4 = 9 بایت = 72 بیت
                if (_frameBits.Count >= 72)
                    TryParseFrame();
            }
        }

        private void TryParseFrame()
        {
            int pos = 0;
            if (_frameBits.Count < 72) return;

            byte type = ReadByte(_frameBits, ref pos);
            ushort index = ReadU16(_frameBits, ref pos);
            ushort len = ReadU16(_frameBits, ref pos);

            int totalBits = (1 + 2 + 2 + len + 4) * 8;
            if (_frameBits.Count < totalBits)
                return;

            pos = 0;
            var body = new List<byte>();

            body.Add(ReadByte(_frameBits, ref pos));                 // Type
            body.AddRange(ReadBytes(_frameBits, ref pos, 2));        // Index
            body.AddRange(ReadBytes(_frameBits, ref pos, 2));        // Len

            var payload = ReadBytes(_frameBits, ref pos, len);
            body.AddRange(payload);

            var crcBytes = ReadBytes(_frameBits, ref pos, 4);
            uint recvCrc = (uint)(crcBytes[0] << 24 | crcBytes[1] << 16 | crcBytes[2] << 8 | crcBytes[3]);
            uint calc = Crc32.Compute(body.ToArray());

            // آماده برای فریم بعد
            _inFrame = false;
            _frameBits.Clear();
            _preWin.Clear();

            if (recvCrc != calc)
                return; // خراب، بنداز دور

            if (type == 1)
            {
                SawEof = true;
                // اگر EOF با index برابر nextIndex بود، یعنی دقیقا بعد آخرین فریم پذیرفته شده آمده
                Completed = true;
                Console.WriteLine("EOF frame received.");
                return;
            }

            // فقط اگر فریم همونی‌ست که انتظار داریم، بنویس
            if (index == _nextIndex)
            {
                _fs.Write(payload, 0, payload.Length);
                _fs.Flush(true);
                Console.Write("w");
                WroteData = true;
                _nextIndex++;
                // اگر فریم تکراری (به‌خاطر repeat) دوباره بیاد، چون index==nextIndex نیست، نادیده گرفته می‌شه.
            }
            else
            {
                // اگر index != _nextIndex، در این نسخه ساده نادیده می‌گیریم
                // (می‌توانی برای دیباگ: Console.WriteLine($"Out-of-order frame {index}, expected {_nextIndex}");
            }
        }

        private static byte ReadByte(List<int> bits, ref int pos)
        {
            int v = 0;
            for (int i = 0; i < 8; i++)
                v = (v << 1) | bits[pos++];
            return (byte)v;
        }

        private static ushort ReadU16(List<int> bits, ref int pos)
        {
            int v = 0;
            for (int i = 0; i < 16; i++)
                v = (v << 1) | bits[pos++];
            return (ushort)v;
        }

        private static byte[] ReadBytes(List<int> bits, ref int pos, int count)
        {
            var arr = new byte[count];
            for (int j = 0; j < count; j++)
            {
                int v = 0;
                for (int i = 0; i < 8; i++)
                    v = (v << 1) | bits[pos++];
                arr[j] = (byte)v;
            }
            return arr;
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