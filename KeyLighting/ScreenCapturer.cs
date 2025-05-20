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
        private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

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

        #region Linux-specific imports

        #endregion

        #region MacOS-specific imports

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
            else if (IsLinux)
            {

                monitors.Add(new Monitor(new Rectangle(0, 0, 1920, 1080), true, "Default"));
            }
            else if (IsMacOS)
            {

                monitors.Add(new Monitor(new Rectangle(0, 0, 1920, 1080), true, "Default"));
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

            frontBuffer = new Bitmap(captureRegion.Width, captureRegion.Height, PixelFormat.Format32bppRgb);
            backBuffer = new Bitmap(captureRegion.Width, captureRegion.Height, PixelFormat.Format32bppRgb);
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
            else if (IsLinux)
            {
                CaptureToBufferLinux(targetBuffer);
            }
            else if (IsMacOS)
            {
                CaptureToBufferMacOS(targetBuffer);
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

                    IntPtr hdcDest = g.GetHdc();
                    try
                    {
                        BitBlt(hdcDest, 0, 0, captureRegion.Width, captureRegion.Height,
                               hdcSrc, captureRegion.X, captureRegion.Y, SRCCOPY);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdcDest);
                    }
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdcSrc);
            }
        }

        private void CaptureToBufferLinux(Bitmap targetBuffer)
        {

            Console.WriteLine("Linux screen capture not yet implemented");

            using (Graphics g = Graphics.FromImage(targetBuffer))
            {
                g.Clear(Color.DarkGray);

                using (Pen pen = new Pen(Color.LightGray, 1))
                {
                    for (int x = 0; x < targetBuffer.Width; x += 20)
                    {
                        g.DrawLine(pen, x, 0, x, targetBuffer.Height);
                    }

                    for (int y = 0; y < targetBuffer.Height; y += 20)
                    {
                        g.DrawLine(pen, 0, y, targetBuffer.Width, y);
                    }
                }

                using (Font font = new Font("Arial", 24))
                using (Brush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("Linux Capture", font, brush, new PointF(targetBuffer.Width / 2 - 100, targetBuffer.Height / 2 - 20));
                }
            }
        }

        private void CaptureToBufferMacOS(Bitmap targetBuffer)
        {

            Console.WriteLine("macOS screen capture not yet implemented");

            using (Graphics g = Graphics.FromImage(targetBuffer))
            {
                g.Clear(Color.DarkGray);

                using (Pen pen = new Pen(Color.LightGray, 1))
                {
                    for (int x = 0; x < targetBuffer.Width; x += 20)
                    {
                        g.DrawLine(pen, x, 0, x, targetBuffer.Height);
                    }

                    for (int y = 0; y < targetBuffer.Height; y += 20)
                    {
                        g.DrawLine(pen, 0, y, targetBuffer.Width, y);
                    }
                }

                using (Font font = new Font("Arial", 24))
                using (Brush brush = new SolidBrush(Color.White))
                {
                    g.DrawString("macOS Capture", font, brush, new PointF(targetBuffer.Width / 2 - 100, targetBuffer.Height / 2 - 20));
                }
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
        }

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
        }
    }
}