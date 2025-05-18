using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;

namespace KeyboardLighting
{

    public class ScreenCapturer : IDisposable
    {

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        private const int SRCCOPY = 0x00CC0020;

        private readonly Screen[] screens;
        private int monitorIndex;
        private Rectangle captureRegion;
        private bool useCustomRegion;

        private readonly object bitmapLock = new object();
        private DateTime lastCaptureTime = DateTime.MinValue;
        private const int MIN_CAPTURE_INTERVAL_MS = 16;

        public ScreenCapturer()
        {

            screens = Screen.AllScreens;
            monitorIndex = 0;
            useCustomRegion = false;
            captureRegion = screens[monitorIndex].Bounds;
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
            if (index < 0 || index >= screens.Length)
            {
                Console.WriteLine($"Invalid monitor index {index}. Using primary monitor.");
                index = 0;
            }

            if (monitorIndex == index) return;

            monitorIndex = index;
            if (!useCustomRegion)
            {
                var newRegion = screens[monitorIndex].Bounds;
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
                newRegion = screens[monitorIndex].Bounds;
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

        private Bitmap? frontBuffer = null;
        private Bitmap? backBuffer = null;
        private readonly object bufferSwapLock = new object();

        public Bitmap? CaptureFrame()
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
        public int ScreenCount => screens.Length;

        public void PrintAvailableMonitors()
        {
            Console.WriteLine($"Available monitors ({screens.Length}):");
            for (int i = 0; i < screens.Length; i++)
            {
                Console.WriteLine($"  [{i}] {screens[i].DeviceName} - Primary: {screens[i].Primary} - Bounds: {screens[i].Bounds}");
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