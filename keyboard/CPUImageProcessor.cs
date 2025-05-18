using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using OpenRGB.NET;

public class CPUImageProcessor : IDisposable
{

    private byte[]? pixelBuffer;
    private int lastWidth;
    private int lastHeight;
    private readonly object bufferLock = new object();

    private byte[] brightnessLut;
    private byte[] contrastLut;
    private bool lutsInitialized = false;
    private double lastBrightness = -1;
    private double lastContrast = -1;

    public OpenRGB.NET.Color[] ProcessImage(Bitmap image, int targetWidth, int targetHeight, double brightness, double vibrance, double contrast, int darkThreshold, double darkFactor)
    {

        return ProcessImageOnCPU(image, targetWidth, targetHeight, brightness, vibrance, contrast, darkThreshold, darkFactor);
    }

    private OpenRGB.NET.Color[] resultBuffer;

    private OpenRGB.NET.Color[] ProcessImageOnCPU(Bitmap image, int targetWidth, int targetHeight, double brightness, double vibrance, double contrast, int darkThreshold, double darkFactor)
    {

        if (resultBuffer == null || resultBuffer.Length != targetWidth)
        {
            resultBuffer = new OpenRGB.NET.Color[targetWidth];
        }

        Bitmap bitmapToProcess = image;
        bool needToDispose = false;

        if (image.Width != targetWidth || image.Height != targetHeight)
        {
            bitmapToProcess = new Bitmap(targetWidth, targetHeight);
            needToDispose = true;

            using (Graphics g = Graphics.FromImage(bitmapToProcess))
            {
                g.InterpolationMode = InterpolationMode.Low;
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.SmoothingMode = SmoothingMode.None;
                g.DrawImage(image, 0, 0, targetWidth, targetHeight);
            }
        }

        try
        {
            BitmapData bmpData = bitmapToProcess.LockBits(
                new Rectangle(0, 0, bitmapToProcess.Width, bitmapToProcess.Height),
                ImageLockMode.ReadOnly,
                bitmapToProcess.PixelFormat);

            try
            {
                int bytesPerPixel = System.Drawing.Image.GetPixelFormatSize(bmpData.PixelFormat) / 8;
                int byteCount = bmpData.Stride * targetHeight;

                if (pixelBuffer == null || pixelBuffer.Length < byteCount)
                {
                    pixelBuffer = new byte[byteCount];
                }

                Marshal.Copy(bmpData.Scan0, pixelBuffer, 0, byteCount);

                if (bytesPerPixel == 4)
                {
                    ProcessColumns32Bpp(bmpData.Stride, targetWidth, targetHeight, brightness, vibrance, contrast, darkThreshold, darkFactor);
                }
                else if (bytesPerPixel == 3)
                {
                    ProcessColumns24Bpp(bmpData.Stride, targetWidth, targetHeight, brightness, vibrance, contrast, darkThreshold, darkFactor);
                }

                return resultBuffer;
            }
            finally
            {
                bitmapToProcess.UnlockBits(bmpData);
            }
        }
        finally
        {
            if (needToDispose)
            {
                bitmapToProcess.Dispose();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ProcessColumns24Bpp(int stride, int width, int height, double brightness, double vibrance, double contrast, int darkThreshold, double darkFactor)
    {
        Parallel.For(0, width, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, x =>
        {
            unchecked
            {
                uint totalR = 0, totalG = 0, totalB = 0;
                int pixelCount = height;
                int columnOffset = x * 3;

                for (int y = 0; y < height; y++)
                {
                    int offset = y * stride + columnOffset;
                    totalB += pixelBuffer[offset];
                    totalG += pixelBuffer[offset + 1];
                    totalR += pixelBuffer[offset + 2];
                }

                byte avgR = (byte)(totalR / pixelCount);
                byte avgG = (byte)(totalG / pixelCount);
                byte avgB = (byte)(totalB / pixelCount);

                resultBuffer[x] = FastApplyEffects(avgR, avgG, avgB, brightness, vibrance, contrast, darkThreshold, darkFactor);
            }
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ProcessColumns32Bpp(int stride, int width, int height, double brightness, double vibrance, double contrast, int darkThreshold, double darkFactor)
    {
        Parallel.For(0, width, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, x =>
        {
            unchecked
            {
                uint totalR = 0, totalG = 0, totalB = 0;
                int pixelCount = height;
                int columnOffset = x * 4;

                for (int y = 0; y < height; y++)
                {
                    int offset = y * stride + columnOffset;
                    totalB += pixelBuffer[offset];
                    totalG += pixelBuffer[offset + 1];
                    totalR += pixelBuffer[offset + 2];
                }

                byte avgR = (byte)(totalR / pixelCount);
                byte avgG = (byte)(totalG / pixelCount);
                byte avgB = (byte)(totalB / pixelCount);

                resultBuffer[x] = FastApplyEffects(avgR, avgG, avgB, brightness, vibrance, contrast, darkThreshold, darkFactor);
            }
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private OpenRGB.NET.Color FastApplyEffects(byte r, byte g, byte b, double brightness, double vibrance, double contrast, int darkThreshold, double darkFactor)
    {

        if (!lutsInitialized || Math.Abs(lastBrightness - brightness) > 0.001 || Math.Abs(lastContrast - contrast) > 0.001)
        {
            InitializeLuts(brightness, contrast);
            lastBrightness = brightness;
            lastContrast = contrast;
            lutsInitialized = true;
        }

        unchecked
        {

            int rVal = brightnessLut[r];
            int gVal = brightnessLut[g];
            int bVal = brightnessLut[b];

            if (Math.Abs(vibrance - 1.0) > 0.001)
            {
                int max = Math.Max(rVal, Math.Max(gVal, bVal));
                int min = Math.Min(rVal, Math.Min(gVal, bVal));
                int delta = max - min;

                if (max > 0 && delta > 0)
                {
                    double adjustment = vibrance - 1.0;
                    double l = (max + min) * 0.00196f;
                    double satAdjust = 1.0 + (adjustment * (1.0 - Math.Abs(2 * l - 1.0)));

                    if (rVal != max)
                        rVal = max - (int)((max - rVal) * satAdjust);
                    if (gVal != max)
                        gVal = max - (int)((max - gVal) * satAdjust);
                    if (bVal != max)
                        bVal = max - (int)((max - bVal) * satAdjust);

                    rVal = Math.Min(Math.Max(rVal, 0), 255);
                    gVal = Math.Min(Math.Max(gVal, 0), 255);
                    bVal = Math.Min(Math.Max(bVal, 0), 255);
                }
            }

            if (Math.Abs(contrast - 1.0) > 0.001)
            {
                rVal = contrastLut[rVal];
                gVal = contrastLut[gVal];
                bVal = contrastLut[bVal];
            }

            if (darkFactor < 1.0)
            {
                int rDark = (int)(rVal * darkFactor);
                int gDark = (int)(gVal * darkFactor);
                int bDark = (int)(bVal * darkFactor);

                rVal = rVal < darkThreshold ? rDark : rVal;
                gVal = gVal < darkThreshold ? gDark : gVal;
                bVal = bVal < darkThreshold ? bDark : bVal;
            }

            return new OpenRGB.NET.Color((byte)rVal, (byte)gVal, (byte)bVal);
        }
    }

    private void InitializeLuts(double brightness, double contrast)
    {
        if (brightnessLut == null)
        {
            brightnessLut = new byte[256];
            contrastLut = new byte[256];
        }

        for (int i = 0; i < 256; i++)
        {

            int brightVal = (int)(i * brightness);
            brightnessLut[i] = (byte)Math.Min(brightVal, 255);

            if (Math.Abs(contrast - 1.0) > 0.001)
            {
                double normalized = i / 255.0;
                int contrastVal = (int)(Math.Pow(normalized, contrast) * 255.0);
                contrastLut[i] = (byte)Math.Min(Math.Max(contrastVal, 0), 255);
            }
            else
            {
                contrastLut[i] = (byte)i;
            }
        }
    }

    public void Dispose()
    {
        pixelBuffer = null;
        resultBuffer = null;
        brightnessLut = null;
        contrastLut = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}