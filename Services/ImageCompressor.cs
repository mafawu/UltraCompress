using ImageMagick;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace UltraCompress.Services;

public class ImageCompressorService
{
    /// <summary>
    /// 执行压缩
    /// </summary>
    /// <param name="inputPath">原图路径</param>
    /// <param name="outputPath">输出保存路径</param>
    /// <param name="quality">质量 (1-100)，建议 75-80</param>
    /// <param name="enableLossyPng">是否开启 PNG 有损压缩 (类似 TinyPNG，体积大幅减小)</param>
    /// <param name="resizeWidth">缩放目标宽度（0 表示不缩放）</param>
    /// <param name="resizeHeight">缩放目标高度（0 表示按宽等比计算）</param>
    public async Task CompressAsync(
        string inputPath,
        string outputPath,
        int quality = 80,
        bool enableLossyPng = true,
        int resizeWidth = 0,
        int resizeHeight = 0)
    {
        // 图片处理是 CPU 密集型任务，必须放到后台线程，否则界面会卡死
        await Task.Run(() =>
        {
            // 1. 加载图片
            using (var image = new MagickImage(inputPath))
            {
                // === 核心步骤 A: 移除元数据 ===
                image.Strip();

                // === 核心步骤 B: 缩放（如果启用了） ===
                if (resizeWidth > 0 || resizeHeight > 0)
                {
                    var w = resizeWidth > 0 ? resizeWidth : (int)(resizeHeight * (double)image.Width / image.Height);
                    var h = resizeHeight > 0 ? resizeHeight : (int)(resizeWidth * (double)image.Height / image.Width);
                    image.Resize((uint)Math.Max(1, w), (uint)Math.Max(1, h));
                }

                // 2. 根据格式分发逻辑
                switch (image.Format)
                {
                    // === JPEG / JPG ===
                    case MagickFormat.Jpg:
                    case MagickFormat.Jpeg:
                        image.Quality = (uint)quality;
                        image.Settings.Interlace = Interlace.Jpeg;
                        break;

                    // === PNG ===
                    case MagickFormat.Png:
                    case MagickFormat.Png8:
                    case MagickFormat.Png24:
                    case MagickFormat.Png32:
                        image.Settings.Interlace = Interlace.NoInterlace;
                        if (enableLossyPng)
                        {
                            image.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.Riemersma });
                        }
                        // 一次写入到位，不再二次 LosslessCompress
                        image.Settings.Compression = CompressionMethod.Zip;
                        break;

                    // === BMP ===
                    case MagickFormat.Bmp:
                    case MagickFormat.Bmp2:
                    case MagickFormat.Bmp3:
                        image.Quantize(new QuantizeSettings
                        {
                            Colors = 256,
                            DitherMethod = DitherMethod.Riemersma
                        });
                        image.Settings.Compression = CompressionMethod.RLE;
                        break;

                    // === GIF ===
                    case MagickFormat.Gif:
                        image.Quantize(new QuantizeSettings { Colors = 128, DitherMethod = DitherMethod.FloydSteinberg });
                        break;

                    // === WebP ===
                    case MagickFormat.WebP:
                        image.Quality = (uint)quality;
                        break;
                }

                // 3. 一次写入（PNG 不再需要二次 LosslessCompress）
                image.Write(outputPath);
            }
        });
    }
}



