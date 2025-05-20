using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Drawing;
using System.Drawing.Imaging;
using OpenRGB.NET;
using OpenRGB.NET.Utils;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text.Unicode;

namespace KeyboardLighting
{
    using SDColor = System.Drawing.Color;
    using ORGBColor = OpenRGB.NET.Color;

    class Program
    {
        static readonly byte[] debugBuffer = new byte[1024];
        static readonly object colorsLock = new object();
        static ORGBColor[]? prevColors;
        static DateTime lastUpdate = DateTime.MinValue;
        static DateTime lastDebugImageSave = DateTime.MinValue;

        static ORGBColor[] targetColors;

        const int MIN_CAPTURE_INTERVAL_MS = 16;
        const int MIN_DEBUG_IMAGE_INTERVAL_MS = 1000;

        static ORGBColor[] ledColorsBuffer = Array.Empty<ORGBColor>();

        static void Main(string[] args)
        {
            try
            {

                var config = ParseConfig();
                if (config == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error: config.json is invalid or missing required fields!");
                    return;
                }

                PrintHeader(config.Version);

                using var client = new OpenRgbClient();
                client.Connect();

                var devices = client.GetAllControllerData();
                int keyboardIndex = -1;
                int ledCount = 0;
                //foreach (var device in devices)
                //{
                //    Console.WriteLine($"Name: {device.Name}, Type: {device.Type}, LEDs: {device.Leds.Length}");
                //}

                for (int i = 0; i < devices.Length; i++)
                {
                    if (devices[i].Type == DeviceType.Keyboard)
                    {
                        keyboardIndex = i;
                        ledCount = devices[i].Leds.Length;
                        break;
                    }
                }

                if (keyboardIndex < 0)
                {
                    Console.WriteLine("Keyboard not found.");
                    return;
                }

                prevColors = new ORGBColor[ledCount];
                ledColorsBuffer = new ORGBColor[ledCount];

                targetColors = new ORGBColor[ledCount];

                var processor = new CPUImageProcessor(config);

                using var capturer = ConfigureScreenCapturer(config);

                var updateTimer = new System.Timers.Timer(config.UpdateDelayMs);
                updateTimer.Elapsed += (s, e) => ProcessFrame(capturer, processor, client, keyboardIndex, ledCount, config);
                updateTimer.Start();

                Console.WriteLine("Press any key to exit.");
                Console.ReadKey();
                updateTimer.Stop();
                updateTimer.Dispose();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.ResetColor();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static LightingConfig? ParseConfig()
        {
            if (!File.Exists("config.json"))
            {
                Console.WriteLine("Error: config.json not found in output directory!");
                return null;
            }

            var json = File.ReadAllText("config.json");
            var settings = new JsonLoadSettings { CommentHandling = CommentHandling.Load };
            var jObject = JObject.Parse(json, settings);
            var config = jObject.ToObject<LightingConfig>();

            return (config?.NumKeys > 0 && config.DownscaleHeight > 0) ? config : null;
        }

        static void PrintHeader(string version)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\r\n$$\\   $$\\                    $$\\       $$\\           $$\\        $$\\     $$\\                     \r\n$$ | $$  |                   $$ |      \\__|          $$ |       $$ |    \\__|                    \r\n$$ |$$  / $$$$$$\\  $$\\   $$\\ $$ |      $$\\  $$$$$$\\  $$$$$$$\\ $$$$$$\\   $$\\ $$$$$$$\\   $$$$$$\\  \r\n$$$$$  / $$  __$$\\ $$ |  $$ |$$ |      $$ |$$  __$$\\ $$  __$$\\\\_$$  _|  $$ |$$  __$$\\ $$  __$$\\ \r\n$$  $$<  $$$$$$$$ |$$ |  $$ |$$ |      $$ |$$ /  $$ |$$ |  $$ | $$ |    $$ |$$ |  $$ |$$ /  $$ |\r\n$$ |\\$$\\ $$   ____|$$ |  $$ |$$ |      $$ |$$ |  $$ |$$ |  $$ | $$ |$$\\ $$ |$$ |  $$ |$$ |  $$ |\r\n$$ | \\$$\\\\$$$$$$$\\ \\$$$$$$$ |$$$$$$$$\\ $$ |\\$$$$$$$ |$$ |  $$ | \\$$$$  |$$ |$$ |  $$ |\\$$$$$$$ |\r\n\\__|  \\__|\\_______| \\____$$ |\\________|\\__| \\____$$ |\\__|  \\__|  \\____/ \\__|\\__|  \\__| \\____$$ |\r\n                   $$\\   $$ |              $$\\   $$ |                                 $$\\   $$ |\r\n                   \\$$$$$$  |              \\$$$$$$  |                                 \\$$$$$$  |\r\n                    \\______/                \\______/                                   \\______/ \r\n");

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("============================================================");
            Console.WriteLine("|                     Keyboard Lighting                    |");
            Console.WriteLine("============================================================");
            Console.WriteLine($"| Version: {version}                                           |");
            Console.WriteLine("============================================================\n");
        }

        static ScreenCapturer ConfigureScreenCapturer(LightingConfig config)
        {
            var capturer = new ScreenCapturer();
            capturer.PrintAvailableMonitors();
            capturer.SetMonitorIndex(config.MonitorSettings.UseMonitorIndex);

            var regionConfig = config.MonitorSettings.CaptureRegion;
            capturer.SetCaptureRegion(
                regionConfig.Enabled,
                regionConfig.X,
                regionConfig.Y,
                regionConfig.Width,
                regionConfig.Height
            );

            return capturer;
        }

        static void ProcessFrame(ScreenCapturer capturer, CPUImageProcessor processor, OpenRgbClient client, int keyboardIndex, int ledCount, LightingConfig config)
        {
            try
            {

                if ((DateTime.Now - lastUpdate).TotalMilliseconds < MIN_CAPTURE_INTERVAL_MS)
                    return;

                lastUpdate = DateTime.Now;

                var frame = capturer.CaptureFrame();
                if (frame == null)
                {
                    if (config.DebugStringUpdates)
                    {
                        Console.WriteLine("Captured frame is null, skipping update.");
                    }
                    return;
                }

                try
                {

                    var columnColors = processor.ProcessImage(
                        frame,
                        config.NumKeys,
                        config.DownscaleHeight,
                        config.BrightnessMultiplier,
                        config.VibranceFactor,
                        config.ContrastPower,
                        config.DarkenThreshold,
                        config.DarkenFactor
                    );

                    if (config.DebugStringUpdates)
                    {

                        var span = debugBuffer.AsSpan();
                        bool success = false;
                        int written = 0;

                        if (Utf8.TryWrite(span, $"CPU colors: ", out written))
                        {
                            span = span.Slice(written);
                            for (int i = 0; i < Math.Min(5, columnColors.Length); i++)
                            {
                                var c = columnColors[i];
                                if (i > 0 && Utf8.TryWrite(span, $", ", out int commaWritten))
                                {
                                    span = span.Slice(commaWritten);
                                }
                                if (Utf8.TryWrite(span, $"R{c.R},G{c.G},B{c.B}", out int colorWritten))
                                {
                                    span = span.Slice(colorWritten);
                                    written += colorWritten;
                                }
                                else break;
                            }
                            Console.WriteLine(Encoding.UTF8.GetString(debugBuffer, 0, debugBuffer.Length - span.Length));
                        }
                    }

                    UpdateLedColors(columnColors, config, ledCount);

                    client.UpdateLeds(keyboardIndex, ledColorsBuffer);

                    if (config.DebugStringUpdates)
                    {

                        var span = debugBuffer.AsSpan();
                        if (Utf8.TryWrite(span, $"Updated LEDs, first LED: R{ledColorsBuffer[0].R} G{ledColorsBuffer[0].G} B{ledColorsBuffer[0].B}", out int written))
                        {
                            Console.WriteLine(Encoding.UTF8.GetString(debugBuffer, 0, written));
                        }
                    }

                    if (config.SaveDebugImages &&
                        (DateTime.Now - lastDebugImageSave).TotalMilliseconds > MIN_DEBUG_IMAGE_INTERVAL_MS)
                    {
                        lastDebugImageSave = DateTime.Now;
                        SaveDebugImages(frame, columnColors, config);
                    }
                }
                finally
                {

                    frame?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in ProcessFrame: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void UpdateLedColors(ORGBColor[] columnColors, LightingConfig config, int ledCount)
        {

            bool instantTransition = config.FadeFactor >= 0.99;

            var wasdEnabled = config.WASDEnabled;
            var wasdKeys = wasdEnabled ? config.WASDKeys : Array.Empty<int>();
            byte wasdR = 0, wasdG = 0, wasdB = 0;

            if (wasdEnabled && config.WASDColor != null)
            {
                var wasdColor = config.WASDColor.ToDrawingColor();
                wasdR = wasdColor.R;
                wasdG = wasdColor.G;
                wasdB = wasdColor.B;
            }

            int columnLength = columnColors.Length;

            for (int i = 0; i < ledCount; i++)
            {
                if (wasdEnabled && Array.IndexOf(wasdKeys, i) >= 0)
                {

                    ledColorsBuffer[i] = new ORGBColor(wasdR, wasdG, wasdB);
                }
                else
                {

                    int columnIndex = Math.Min(i, columnLength - 1);

                    if (instantTransition)
                    {

                        ledColorsBuffer[i] = columnColors[columnIndex];
                    }
                    else
                    {

                        ORGBColor prev = prevColors[i];
                        ORGBColor target = columnColors[columnIndex];

                        float t = 0.8f;

                        byte r = (byte)Math.Round(prev.R * (1 - t) + target.R * t);
                        byte g = (byte)Math.Round(prev.G * (1 - t) + target.G * t);
                        byte b = (byte)Math.Round(prev.B * (1 - t) + target.B * t);

                        ledColorsBuffer[i] = new ORGBColor(r, g, b);
                    }

                    prevColors[i] = ledColorsBuffer[i];
                }
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void WriteFormattedToConsole<T>(T value) where T : IUtf8SpanFormattable
        {
            Span<byte> buffer = stackalloc byte[512];
            if (value.TryFormat(buffer, out int written, default, null))
            {
                Console.WriteLine(Encoding.UTF8.GetString(buffer.Slice(0, written)));
            }
        }
        static class Utf8BufferPool
        {
            private static readonly ThreadLocal<byte[]> _threadLocalBuffer =
                new ThreadLocal<byte[]>(() => new byte[2048]);

            public static byte[] GetBuffer() => _threadLocalBuffer.Value;
        }
        static void SaveDebugImages(Bitmap frame, ORGBColor[] columnColors, LightingConfig config)
        {
            try
            {
                string folder = "images";
                Directory.CreateDirectory(folder);

                Span<byte> filenameBuffer = stackalloc byte[256];
                DateTime now = DateTime.Now;

                if (Utf8.TryWrite(filenameBuffer, $"{folder}/debug_frame_{now:yyyyMMdd_HHmmss_fff}.png", out int debugWritten))
                {
                    string debugFilename = Encoding.UTF8.GetString(filenameBuffer.Slice(0, debugWritten));

                    using (var debugBmp = new Bitmap(columnColors.Length, 50))
                    {

                        debugBmp.Save(debugFilename, ImageFormat.Png);
                    }
                }

                if (Utf8.TryWrite(filenameBuffer, $"{folder}/captured_frame_{now:yyyyMMdd_HHmmss_fff}.png", out int frameWritten))
                {
                    string frameFilename = Encoding.UTF8.GetString(filenameBuffer.Slice(0, frameWritten));
                    frame.Save(frameFilename, ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {

                var span = debugBuffer.AsSpan();
                if (Utf8.TryWrite(span, $"Error saving debug images: {ex.Message}", out int written))
                {
                    Console.WriteLine(Encoding.UTF8.GetString(debugBuffer, 0, written));
                }
            }
        }
    }

    static class ORGBColorExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SDColor ToSDColor(this ORGBColor c) => SDColor.FromArgb(c.R, c.G, c.B);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SDColor ToDrawingColor(this JToken colorToken)
        {
            if (colorToken == null || !colorToken["R"].HasValues || !colorToken["G"].HasValues || !colorToken["B"].HasValues)
                return SDColor.Black;

            return SDColor.FromArgb(
                colorToken["R"].Value<int>(),
                colorToken["G"].Value<int>(),
                colorToken["B"].Value<int>()
            );
        }
    }
}