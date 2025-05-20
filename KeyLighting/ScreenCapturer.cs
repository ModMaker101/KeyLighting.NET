using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace KeyboardLighting
{
    public class ScreenCapturer : IDisposable
    {
        #region Platform Detection

        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        

        #endregion

        #region Windows-specific imports

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public char[] szDevice;
        }

        private const int CCHDEVICENAME = 32;
        private const int MONITORINFOF_PRIMARY = 0x00000001;
        private const int SRCCOPY = 0x00CC0020;

        #endregion

       

        private class Monitor
        {
            public Rectangle Bounds { get; set; }
            public bool IsPrimary { get; set; }
            public string DeviceName { get; set; }

            public Monitor(Rectangle bounds, bool isPrimary, string deviceName)
            {
                Bounds = bounds;
                IsPrimary = isPrimary;
                DeviceName = deviceName;
            }
        }

        private readonly List<Monitor> monitors = new List<Monitor>();
        private int monitorIndex;
        private Rectangle captureRegion;
        private bool useCustomRegion;

        private readonly object bitmapLock = new object();
        private DateTime lastCaptureTime = DateTime.MinValue;
        private const int MIN_CAPTURE_INTERVAL_MS = 16;

        private Bitmap frontBuffer = null;
        private Bitmap backBuffer = null;
        private readonly object bufferSwapLock = new object();

        // Binary serialization buffers for performance
        private byte[] binaryBuffer = null;
        private readonly object binaryBufferLock = new object();

        public ScreenCapturer()
        {
            InitializeMonitors();
            monitorIndex = 0;
            useCustomRegion = false;
            captureRegion = monitors.Count > 0 ? monitors[monitorIndex].Bounds : new Rectangle(0, 0, 1920, 1080);
        }

        private void InitializeMonitors()
        {
            monitors.Clear();

            if (IsWindows)
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, EnumMonitorsCallback, IntPtr.Zero);
            }
            else
            {
                monitors.Add(new Monitor(new Rectangle(0, 0, 1920, 1080), true, "Default"));
            }

            if (monitors.Count == 0)
            {
                monitors.Add(new Monitor(new Rectangle(0, 0, 1920, 1080), true, "Default"));
            }
        }

        private bool EnumMonitorsCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
        {
            MONITORINFOEX monitorInfo = new MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFOEX)),
                szDevice = new char[CCHDEVICENAME]
            };

            if (GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                Rectangle bounds = new Rectangle(
                    monitorInfo.rcMonitor.left,
                    monitorInfo.rcMonitor.top,
                    monitorInfo.rcMonitor.right - monitorInfo.rcMonitor.left,
                    monitorInfo.rcMonitor.bottom - monitorInfo.rcMonitor.top
                );

                bool isPrimary = (monitorInfo.dwFlags & MONITORINFOF_PRIMARY) != 0;
                string deviceName = new string(monitorInfo.szDevice).TrimEnd('\0');

                monitors.Add(new Monitor(bounds, isPrimary, deviceName));
            }

            return true;
        }

        private void InitializeBuffers()
        {
            frontBuffer?.Dispose();
            backBuffer?.Dispose();

            int scaledWidth = captureRegion.Width / 2;
            int scaledHeight = captureRegion.Height / 2;

            frontBuffer = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppRgb);
            backBuffer = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppRgb);

            // Initialize binary buffer for the new size
            int requiredSize = scaledWidth * scaledHeight * 4 + 12; // 4 bytes per pixel + 12 bytes header
            lock (binaryBufferLock)
            {
                if (binaryBuffer == null || binaryBuffer.Length < requiredSize)
                {
                    binaryBuffer = new byte[requiredSize];
                }
            }
        }


        public void SetMonitorIndex(int index)
        {
            if (index < 0 || index >= monitors.Count)
            {
                Console.WriteLine($"Invalid monitor index {index}. Using primary monitor.");
                index = 0;
            }

            if (monitorIndex == index) return;

            monitorIndex = index;
            if (!useCustomRegion)
            {
                var newRegion = monitors[monitorIndex].Bounds;
                if (captureRegion.Size != newRegion.Size)
                {
                    captureRegion = newRegion;
                    ResetBitmapCache();
                }
                else
                {
                    captureRegion = newRegion;
                }
            }
        }

        public void SetCaptureRegion(bool enabled, int x, int y, int width, int height)
        {
            useCustomRegion = enabled;
            Rectangle newRegion;

            if (enabled)
            {
                newRegion = new Rectangle(x, y, width, height);
                Console.WriteLine($"Using custom capture region: {newRegion}");
            }
            else
            {
                newRegion = monitors[monitorIndex].Bounds;
                Console.WriteLine($"Using full monitor bounds: {newRegion}");
            }

            if (captureRegion.Size != newRegion.Size)
            {
                captureRegion = newRegion;
                ResetBitmapCache();
            }
            else
            {
                captureRegion = newRegion;
            }
        }

        private void CaptureToBuffer(Bitmap targetBuffer)
        {
            if (targetBuffer == null) return;

            if (IsWindows)
            {
                CaptureToBufferWindows(targetBuffer);
            }
            else
            {
                Console.WriteLine("Platform not supported for screen capture");
            }
        }

        private void CaptureToBufferWindows(Bitmap targetBuffer)
        {
            IntPtr hdcSrc = GetDC(IntPtr.Zero);
            if (hdcSrc == IntPtr.Zero) return;

            try
            {
                using (Graphics g = Graphics.FromImage(targetBuffer))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.CompositingQuality = CompositingQuality.HighSpeed;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.SmoothingMode = SmoothingMode.None;
                    g.PixelOffsetMode = PixelOffsetMode.None;

                    int scaledWidth = captureRegion.Width / 2;
                    int scaledHeight = captureRegion.Height / 2;

                    using (Bitmap temp = new Bitmap(captureRegion.Width, captureRegion.Height))
                    using (Graphics tempG = Graphics.FromImage(temp))
                    {
                        IntPtr hdcDest = tempG.GetHdc();
                        try
                        {
                            BitBlt(hdcDest, 0, 0, captureRegion.Width, captureRegion.Height,
                                   hdcSrc, captureRegion.X, captureRegion.Y, SRCCOPY);
                        }
                        finally
                        {
                            tempG.ReleaseHdc(hdcDest);
                        }

                        g.DrawImage(temp, 0, 0, scaledWidth, scaledHeight);
                    }
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdcSrc);
            }
        }



        private void ResetBitmapCache()
        {
            lock (bufferSwapLock)
            {
                frontBuffer?.Dispose();
                backBuffer?.Dispose();
                frontBuffer = null;
                backBuffer = null;
            }

            lock (binaryBufferLock)
            {
                binaryBuffer = null;
            }
        }

        // Original method - returns Bitmap
        public Bitmap CaptureFrame()
        {
            try
            {
                var timeSinceLastCapture = (DateTime.Now - lastCaptureTime).TotalMilliseconds;
                if (timeSinceLastCapture < MIN_CAPTURE_INTERVAL_MS)
                {
                    lock (bufferSwapLock)
                    {
                        return frontBuffer?.Clone(new Rectangle(0, 0, frontBuffer.Width, frontBuffer.Height), frontBuffer.PixelFormat) as Bitmap;
                    }
                }

                lastCaptureTime = DateTime.Now;

                lock (bufferSwapLock)
                {
                    if (frontBuffer == null || backBuffer == null ||
                        frontBuffer.Width != captureRegion.Width ||
                        frontBuffer.Height != captureRegion.Height)
                    {
                        InitializeBuffers();
                    }

                    CaptureToBuffer(backBuffer);

                    var temp = frontBuffer;
                    frontBuffer = backBuffer;
                    backBuffer = temp;

                    return frontBuffer?.Clone(new Rectangle(0, 0, frontBuffer.Width, frontBuffer.Height), frontBuffer.PixelFormat) as Bitmap;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error during frame capture: " + ex.Message);
                return null;
            }
        }

        // New method - returns binary data (much faster than string conversion)
        public unsafe byte[] CaptureFrameAsBinary()
        {
            try
            {
                var timeSinceLastCapture = (DateTime.Now - lastCaptureTime).TotalMilliseconds;

                lock (bufferSwapLock)
                {
                    // If we captured recently, just serialize the existing front buffer
                    if (timeSinceLastCapture < MIN_CAPTURE_INTERVAL_MS && frontBuffer != null)
                    {
                        return SerializeBitmapToBinary(frontBuffer);
                    }
                }

                lastCaptureTime = DateTime.Now;

                lock (bufferSwapLock)
                {
                    if (frontBuffer == null || backBuffer == null ||
                        frontBuffer.Width != captureRegion.Width ||
                        frontBuffer.Height != captureRegion.Height)
                    {
                        InitializeBuffers();
                    }

                    CaptureToBuffer(backBuffer);

                    var temp = frontBuffer;
                    frontBuffer = backBuffer;
                    backBuffer = temp;

                    return SerializeBitmapToBinary(frontBuffer);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error during binary frame capture: " + ex.Message);
                return null;
            }
        }

        // High-performance binary serialization
        private unsafe byte[] SerializeBitmapToBinary(Bitmap bitmap)
        {
            if (bitmap == null) return null;

            BitmapData bmpData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppRgb);

            try
            {
                int stride = Math.Abs(bmpData.Stride);
                int pixelDataSize = stride * bitmap.Height;
                int totalSize = pixelDataSize + 12; // 12 bytes for header (width, height, stride)

                byte[] result;
                lock (binaryBufferLock)
                {
                    // Reuse buffer if it's large enough
                    if (binaryBuffer == null || binaryBuffer.Length < totalSize)
                    {
                        binaryBuffer = new byte[totalSize];
                    }
                    result = new byte[totalSize];
                }

                // Write header (width, height, stride)
                BitConverter.GetBytes(bitmap.Width).CopyTo(result, 0);
                BitConverter.GetBytes(bitmap.Height).CopyTo(result, 4);
                BitConverter.GetBytes(stride).CopyTo(result, 8);

                // Copy raw pixel data
                Marshal.Copy(bmpData.Scan0, result, 12, pixelDataSize);

                return result;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }

        // Deserialize binary data back to Bitmap (for receiving end)
        public static unsafe Bitmap DeserializeBinaryToBitmap(byte[] binaryData)
        {
            if (binaryData == null || binaryData.Length < 12)
                return null;

            try
            {
                // Read header
                int width = BitConverter.ToInt32(binaryData, 0);
                int height = BitConverter.ToInt32(binaryData, 4);
                int stride = BitConverter.ToInt32(binaryData, 8);

                // Validate dimensions
                if (width <= 0 || height <= 0 || stride <= 0)
                    return null;

                int expectedDataSize = stride * height;
                if (binaryData.Length < expectedDataSize + 12)
                    return null;

                Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                BitmapData bmpData = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppRgb);

                try
                {
                    // Copy pixel data back
                    Marshal.Copy(binaryData, 12, bmpData.Scan0, expectedDataSize);
                    return bitmap;
                }
                finally
                {
                    bitmap.UnlockBits(bmpData);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error deserializing binary data to bitmap: " + ex.Message);
                return null;
            }
        }

        // Utility method to extract dimensions from binary data without full deserialization
        public static (int width, int height) GetDimensionsFromBinary(byte[] binaryData)
        {
            if (binaryData == null || binaryData.Length < 8)
                return (0, 0);

            int width = BitConverter.ToInt32(binaryData, 0);
            int height = BitConverter.ToInt32(binaryData, 4);
            return (width, height);
        }

        // Performance comparison method
        public void ComparePerformance(int iterations = 100)
        {
            Console.WriteLine($"Performance comparison over {iterations} iterations:");

            var watch = System.Diagnostics.Stopwatch.StartNew();

            // Test binary serialization
            watch.Restart();
            for (int i = 0; i < iterations; i++)
            {
                byte[] binaryData = CaptureFrameAsBinary();
                if (binaryData != null)
                {
                    // Simulate sending to CPU processing
                    // In real scenario, you'd pass this to your image processing method
                }
            }
            watch.Stop();
            long binaryTime = watch.ElapsedMilliseconds;

            // Test bitmap method
            watch.Restart();
            for (int i = 0; i < iterations; i++)
            {
                Bitmap bitmap = CaptureFrame();
                if (bitmap != null)
                {
                    // Simulate processing
                    bitmap.Dispose();
                }
            }
            watch.Stop();
            long bitmapTime = watch.ElapsedMilliseconds;

            Console.WriteLine($"Binary serialization: {binaryTime}ms");
            Console.WriteLine($"Bitmap method: {bitmapTime}ms");
            Console.WriteLine($"Binary is {(double)bitmapTime / binaryTime:F2}x faster");
        }

        public int ScreenCount => monitors.Count;

        public void PrintAvailableMonitors()
        {
            Console.WriteLine($"Available monitors ({monitors.Count}):");
            for (int i = 0; i < monitors.Count; i++)
            {
                Console.WriteLine($"  [{i}] {monitors[i].DeviceName} - Primary: {monitors[i].IsPrimary} - Bounds: {monitors[i].Bounds}");
            }
        }

        public void Dispose()
        {
            lock (bufferSwapLock)
            {
                frontBuffer?.Dispose();
                backBuffer?.Dispose();
                frontBuffer = null;
                backBuffer = null;
            }

            lock (binaryBufferLock)
            {
                binaryBuffer = null;
            }
        }
    }

    // Example usage class
    public class CpuImageProcessor
    {
        // Example method that would receive binary data instead of string
        public void ProcessImageBinary(byte[] binaryImageData)
        {
            if (binaryImageData == null) return;

            // Get dimensions without full deserialization (very fast)
            var (width, height) = ScreenCapturer.GetDimensionsFromBinary(binaryImageData);
            Console.WriteLine($"Processing image: {width}x{height}");

            // If you need the full bitmap for processing
            using (Bitmap bitmap = ScreenCapturer.DeserializeBinaryToBitmap(binaryImageData))
            {
                if (bitmap != null)
                {
                    // Your image processing logic here
                    // For example: calculate average color, detect brightness, etc.
                    Color avgColor = CalculateAverageColor(bitmap);
                    Console.WriteLine($"Average color: R={avgColor.R}, G={avgColor.G}, B={avgColor.B}");
                }
            }
        }

        private unsafe Color CalculateAverageColor(Bitmap bitmap)
        {
            BitmapData bmpData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppRgb);

            try
            {
                byte* ptr = (byte*)bmpData.Scan0;
                long totalR = 0, totalG = 0, totalB = 0;
                int pixelCount = bitmap.Width * bitmap.Height;

                for (int i = 0; i < pixelCount; i++)
                {
                    totalB += ptr[i * 4];
                    totalG += ptr[i * 4 + 1];
                    totalR += ptr[i * 4 + 2];
                }

                return Color.FromArgb(
                    (int)(totalR / pixelCount),
                    (int)(totalG / pixelCount),
                    (int)(totalB / pixelCount));
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }
    }
}