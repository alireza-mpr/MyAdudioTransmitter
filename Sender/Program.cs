using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace MmSender
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Minimodem-Compatible Sender (1200 bps, Bell202, 8N1)");

            string filePath;
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

            byte[] data = File.ReadAllBytes(filePath);
            Console.WriteLine($"File: {filePath}, {data.Length} bytes");

            const int sampleRate = 48000;
            const int baud = 1200;
            const double markFreq = 1200.0; // bit = 1
            const double spaceFreq = 2200.0; // bit = 0
            const double amplitude = 0.7; // avoid clipping

            int samplesPerBit = sampleRate / baud; // 40

            var samples = new List<short>();

            // 1) idle preamble: 500ms of mark (line idle = 1)
            double t = 0.0;
            double dt = 1.0 / sampleRate;
            double phase = 0.0;

            int idleSamples = (int)(0.5 * sampleRate);
            double wMark = 2.0 * Math.PI * markFreq;
            for (int i = 0; i < idleSamples; i++)
            {
                double s = Math.Sin(phase) * amplitude;
                samples.Add((short)(s * short.MaxValue));
                phase += wMark * dt;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                t += dt;
            }

            // helper: append one bit with continuous phase
            void AppendBit(int bit)
            {
                double freq = (bit == 0) ? spaceFreq : markFreq;
                double w = 2.0 * Math.PI * freq;
                for (int i = 0; i < samplesPerBit; i++)
                {
                    double s = Math.Sin(phase) * amplitude;
                    samples.Add((short)(s * short.MaxValue));
                    phase += w * dt;
                    if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                }
            }

            // 2) send bytes as Bell202 8N1 (LSB-first)
            foreach (byte b in data)
            {
                // start bit: 0
                AppendBit(0);

                // 8 data bits, LSB-first (مطابق UART و minimodem)
                for (int i = 0; i < 8; i++)
                {
                    int bit = (b >> i) & 1;
                    AppendBit(bit);
                }

                // stop bit: 1
                AppendBit(1);
            }

            // 3) a bit of idle mark at end
            int tailSamples = (int)(0.2 * sampleRate);
            for (int i = 0; i < tailSamples; i++)
            {
                double s = Math.Sin(phase) * amplitude;
                samples.Add((short)(s * short.MaxValue));
                phase += wMark * dt;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
            }

            // play it
            Console.WriteLine($"Total duration ≈ {(double)samples.Count / sampleRate:F1} s");

            byte[] pcm = new byte[samples.Count * 2];
            for (int i = 0; i < samples.Count; i++)
            {
                short v = samples[i];
                pcm[2 * i] = (byte)(v & 0xFF);
                pcm[2 * i + 1] = (byte)((v >> 8) & 0xFF);
            }

            using var ms = new MemoryStream(pcm);
            using var rs = new RawSourceWaveStream(ms, new WaveFormat(sampleRate, 16, 1));
            using var wo = new WaveOutEvent();
            wo.Init(rs);
            wo.Play();
            while (wo.PlaybackState == PlaybackState.Playing)
                System.Threading.Thread.Sleep(50);

            Console.WriteLine("Done.");
        }
    }
}