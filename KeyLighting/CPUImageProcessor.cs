using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KeyboardLighting;
using Newtonsoft.Json;
using OpenRGB.NET;

public class CPUImageProcessor : IDisposable
{
    private byte[]? pixelBuffer;

    private byte[] brightnessLut = new byte[256];
    private byte[] contrastLut = new byte[256];
    private double lastBrightness = -1;
    private double lastContrast = -1;

    private OpenRGB.NET.Color[] previousFrame;
    private OpenRGB.NET.Color[] rawColors;
    private OpenRGB.NET.Color[] resultBuffer;
    private bool hasPreviousFrame = false;

    private double fadeSpeed;

    public CPUImageProcessor(LightingConfig config)
    {
        fadeSpeed = config.FadeFactor;
    }

    private bool lastFrameWasSolid = false;
    private int lastSolidR, lastSolidG, lastSolidB;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetFadeSpeed(double speed)
    {
        fadeSpeed = Math.Clamp(speed, 0.0, 1.0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public OpenRGB.NET.Color[] ProcessImage(Bitmap image, int targetWidth, int targetHeight, double brightness, double vibrance, double contrast, int darkThreshold, double darkFactor)
    {

        if (resultBuffer == null || resultBuffer.Length != targetWidth)
        {
            resultBuffer = new OpenRGB.NET.Color[targetWidth];
            rawColors = new OpenRGB.NET.Color[targetWidth];
            previousFrame = new OpenRGB.NET.Color[targetWidth];
            hasPreviousFrame = false;
        }

        if (!AreSettingsCached(brightness, contrast))
        {
            InitializeLuts(brightness, contrast);
            lastBrightness = brightness;
            lastContrast = contrast;
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

                ExtractColumns(bmpData.Stride, targetWidth, targetHeight, bytesPerPixel);

                bool isSolidColor = IsSolidColorFrame();

                if (isSolidColor)
                {
                    ProcessSolidColor(rawColors[0].R, rawColors[0].G, rawColors[0].B,
                                      targetWidth, brightness, vibrance, contrast,
                                      darkThreshold, darkFactor);
                }
                else
                {
                    ProcessColumnsWithEffects(targetWidth, brightness, vibrance, contrast, darkThreshold, darkFactor);

                    if (hasPreviousFrame && fadeSpeed < 1.0)
                    {
                        ApplyFading(targetWidth, fadeSpeed);
                    }
                }

                StoreFrameState(targetWidth, isSolidColor);

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AreSettingsCached(double brightness, double contrast)
    {
        return Math.Abs(lastBrightness - brightness) <= 0.001 && Math.Abs(lastContrast - contrast) <= 0.001;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StoreFrameState(int width, bool isSolidColor)
    {

        Array.Copy(resultBuffer, previousFrame, width);
        hasPreviousFrame = true;
        lastFrameWasSolid = isSolidColor;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool IsSolidColorFrame()
    {
        if (rawColors.Length < 2) return true;

        OpenRGB.NET.Color first = rawColors[0];
        const int tolerance = 5;

        int[] samplePoints = { 0, rawColors.Length / 3, rawColors.Length / 2, (rawColors.Length * 2) / 3, rawColors.Length - 1 };

        foreach (int i in samplePoints)
        {
            if (i == 0) continue;

            if (Math.Abs(first.R - rawColors[i].R) > tolerance ||
                Math.Abs(first.G - rawColors[i].G) > tolerance ||
                Math.Abs(first.B - rawColors[i].B) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ApplyFading(int width, double fadeFactor)
    {

        Parallel.For(0, width, i => {
            resultBuffer[i] = FastBlendColors(previousFrame[i], resultBuffer[i], fadeFactor);
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private OpenRGB.NET.Color FastBlendColors(OpenRGB.NET.Color color1, OpenRGB.NET.Color color2, double factor)
    {
        factor = Math.Clamp(factor, 0.0, 1.0);
        double inverseFactor = 1.0 - factor;

        byte r = (byte)(color1.R * inverseFactor + color2.R * factor);
        byte g = (byte)(color1.G * inverseFactor + color2.G * factor);
        byte b = (byte)(color1.B * inverseFactor + color2.B * factor);

        return new OpenRGB.NET.Color(r, g, b);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ProcessSolidColor(byte r, byte g, byte b, int width, double brightness, double vibrance, double contrast, int darkThreshold, double darkFactor)
    {
        OpenRGB.NET.Color processedColor = FastApplyEffects(r, g, b, brightness, vibrance, contrast, darkThreshold, darkFactor);

        bool needsFade = hasPreviousFrame &&
                         fadeSpeed < 1.0 &&
                         !(lastFrameWasSolid &&
                           lastSolidR == processedColor.R &&
                           lastSolidG == processedColor.G &&
                           lastSolidB == processedColor.B);

        if (needsFade)
        {

            int prevBrightness = lastSolidR + lastSolidG + lastSolidB;
            int newBrightness = processedColor.R + processedColor.G + processedColor.B;

            double fadeFactor = fadeSpeed;

            Parallel.For(0, width, i => {
                resultBuffer[i] = FastBlendColors(previousFrame[i], processedColor, fadeFactor);
            });
        }
        else
        {
            for (int i = 0; i < width; i++)
            {
                resultBuffer[i] = processedColor;
            }
        }

        lastSolidR = processedColor.R;
        lastSolidG = processedColor.G;
        lastSolidB = processedColor.B;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ExtractColumns(int stride, int width, int height, int bytesPerPixel)
    {
        if (bytesPerPixel == 4)
            ExtractColumns32Bpp(stride, width, height);
        else
            ExtractColumns24Bpp(stride, width, height);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ExtractColumns24Bpp(int stride, int width, int height)
    {
        Parallel.For(0, width, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, x =>
        {
            unchecked
            {
                int pixelCount = height;
                int columnOffset = x * 3;
                int y = 0;

                Vector<ulong> sumR = Vector<ulong>.Zero;
                Vector<ulong> sumG = Vector<ulong>.Zero;
                Vector<ulong> sumB = Vector<ulong>.Zero;
                int vectorSize = Vector<byte>.Count;

                // Vectorized sum
                for (; y <= height - vectorSize; y += vectorSize)
                {
                    Span<byte> colBytes = stackalloc byte[vectorSize * 3];
                    for (int v = 0; v < vectorSize; v++)
                    {
                        int offset = (y + v) * stride + columnOffset;
                        colBytes[v * 3 + 0] = pixelBuffer[offset];
                        colBytes[v * 3 + 1] = pixelBuffer[offset + 1];
                        colBytes[v * 3 + 2] = pixelBuffer[offset + 2];
                    }

                    var vec = new Vector<byte>(colBytes);

                    // Extract R, G, B channels and sum
                    ulong r = 0, g = 0, b = 0;
                    for (int v = 0; v < vectorSize; v++)
                    {
                        b += vec[v * 3 + 0];
                        g += vec[v * 3 + 1];
                        r += vec[v * 3 + 2];
                    }
                    sumR += new Vector<ulong>(r);
                    sumG += new Vector<ulong>(g);
                    sumB += new Vector<ulong>(b);
                }

                // Scalar sum for remaining pixels
                ulong totalR = 0, totalG = 0, totalB = 0;
                for (; y < height; y++)
                {
                    int offset = y * stride + columnOffset;
                    totalB += pixelBuffer[offset];
                    totalG += pixelBuffer[offset + 1];
                    totalR += pixelBuffer[offset + 2];
                }

                // Add vectorized sums
                for (int i = 0; i < Vector<ulong>.Count; i++)
                {
                    totalR += sumR[i];
                    totalG += sumG[i];
                    totalB += sumB[i];
                }

                byte avgR = (byte)(totalR / (ulong)pixelCount);
                byte avgG = (byte)(totalG / (ulong)pixelCount);
                byte avgB = (byte)(totalB / (ulong)pixelCount);

                rawColors[x] = new OpenRGB.NET.Color(avgR, avgG, avgB);
            }
        });
    }


    // Move the stackalloc and Vector<byte> creation outside the vectorized loop
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ExtractColumns32Bpp(int stride, int width, int height)
    {
        Parallel.For(0, width, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, x =>
        {
            unchecked
            {
                int pixelCount = height;
                int columnOffset = x * 4;
                ulong totalR = 0, totalG = 0, totalB = 0;

                // Process in blocks for cache efficiency
                int blockSize = 32; // Tune for your CPU cache
                int y = 0;
                for (; y <= height - blockSize; y += blockSize)
                {
                    for (int b = 0; b < blockSize; b++)
                    {
                        int offset = (y + b) * stride + columnOffset;
                        totalB += pixelBuffer![offset];
                        totalG += pixelBuffer![offset + 1];
                        totalR += pixelBuffer![offset + 2];
                    }
                }
                // Process remaining pixels
                for (; y < height; y++)
                {
                    int offset = y * stride + columnOffset;
                    totalB += pixelBuffer![offset];
                    totalG += pixelBuffer![offset + 1];
                    totalR += pixelBuffer![offset + 2];
                }

                byte avgR = (byte)(totalR / (ulong)pixelCount);
                byte avgG = (byte)(totalG / (ulong)pixelCount);
                byte avgB = (byte)(totalB / (ulong)pixelCount);

                rawColors[x] = new OpenRGB.NET.Color(avgR, avgG, avgB);
            }
        });
    }



    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ProcessColumnsWithEffects(int width, double brightness, double vibrance, double contrast, int darkThreshold, double darkFactor)
    {
        Parallel.For(0, width, x =>
        {
            resultBuffer[x] = FastApplyEffects(rawColors[x].R, rawColors[x].G, rawColors[x].B,
                                              brightness, vibrance, contrast, darkThreshold, darkFactor);
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private OpenRGB.NET.Color FastApplyEffects(byte r, byte g, byte b, double brightness, double vibrance, double contrast, int darkThreshold, double darkFactor)
    {
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

            return new OpenRGB.NET.Color(
                (byte)Math.Min(Math.Max(rVal, 0), 255),
                (byte)Math.Min(Math.Max(gVal, 0), 255),
                (byte)Math.Min(Math.Max(bVal, 0), 255)
            );
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    // Vectorized LUT initialization
    private void InitializeLuts(double brightness, double contrast)
    {
        // Vectorized brightness LUT
        if (Vector.IsHardwareAccelerated)
        {
            var brightnessVec = new Vector<float>((float)brightness);
            int vecSize = Vector<float>.Count;
            int i = 0;
            for (; i <= 256 - vecSize; i += vecSize)
            {
                var indices = new Vector<float>(Enumerable.Range(i, vecSize).Select(x => (float)x).ToArray());
                var result = Vector.Multiply(indices, brightnessVec);
                for (int j = 0; j < vecSize; j++)
                {
                    brightnessLut[i + j] = (byte)Math.Clamp((int)result[j], 0, 255);
                }
            }
            // Handle any remaining elements
            for (; i < 256; i++)
            {
                int brightVal = (int)(i * brightness);
                brightnessLut[i] = (byte)Math.Clamp(brightVal, 0, 255);
            }
        }
        else
        {
            for (int i = 0; i < 256; i++)
            {
                int brightVal = (int)(i * brightness);
                brightnessLut[i] = (byte)Math.Clamp(brightVal, 0, 255);
            }
        }

        // Contrast LUT (not vectorized)
        for (int i = 0; i < 256; i++)
        {
            if (Math.Abs(contrast - 1.0) > 0.001)
            {
                double normalized = i / 255.0;
                double adjusted = Math.Pow(normalized, contrast) * 255.0;
                contrastLut[i] = (byte)Math.Clamp((int)adjusted, 0, 255);
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
        rawColors = null;
        previousFrame = null;
        brightnessLut = null;
        contrastLut = null;
    }
}