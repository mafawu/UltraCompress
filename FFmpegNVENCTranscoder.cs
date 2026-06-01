using System;
using System.Diagnostics;
using System.IO;

namespace FFmpegNVENCTranscoder
{
    public class NVENCVideoCompressor
    {
        private string ffmpegPath;

        public NVENCVideoCompressor(string ffmpegExecutablePath)
        {
            ffmpegPath = ffmpegExecutablePath;
            if (!File.Exists(ffmpegPath))
            {
                throw new FileNotFoundException($"FFmpeg executable not found at: {ffmpegPath}");
            }
        }

        /// <summary>
        /// 使用NVIDIA NVENC硬件加速压缩视频
        /// </summary>
        /// <param name="inputPath">输入视频路径</param>
        /// <param name="outputPath">输出视频路径</param>
        /// <param name="preset">编码质量预设 (e.g., "p1" fastest, "p7" slowest)</param>
        /// <param name="crf">质量因子 (lower = higher quality, 0-51)</param>
        /// <param name="bitrate">目标比特率 (e.g., "2M", "5M")</param>
        public void CompressVideoWithNVENC(
            string inputPath,
            string outputPath,
            string preset = "p5", // 平衡性能与质量
            int crf = 28, // 默认质量
            string bitrate = null)
        {
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException($"Input file not found: {inputPath}");
            }

            // 检查NVIDIA GPU支持
            if (!CheckNVEncSupport())
            {
                throw new InvalidOperationException("NVIDIA GPU with NVENC support not detected");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = BuildNVENCArguments(inputPath, outputPath, preset, crf, bitrate),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();

                // 读取输出流
                string output = process.StandardError.ReadToEnd();

                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    Console.WriteLine($"Compression completed successfully: {outputPath}");
                }
                else
                {
                    throw new Exception($"FFmpeg failed with exit code {process.ExitCode}: {output}");
                }
            }
        }

        /// <summary>
        /// 构建NVENC编码参数
        /// </summary>
        private string BuildNVENCArguments(
            string inputPath,
            string outputPath,
            string preset,
            int crf,
            string bitrate)
        {
            var args = $"-hwaccel cuda -i \"{inputPath}\" -c:v h264_nvenc";

            // 添加预设参数
            args += $" -preset {preset}";

            // 选择质量控制方式
            if (!string.IsNullOrEmpty(bitrate))
            {
                args += $" -b:v {bitrate}"; // 固定比特率模式
                if (crf > 0)
                    args += $" -cq {crf}"; // 二次确认CRF
            }
            else
            {
                args += $" -cq {crf}"; // 恒定质量模式
            }

            // 添加其他优化参数
            args += " -pix_fmt yuv420p -c:a copy"; // 保持音频原样

            args += $" \"{outputPath}\"";

            return args;
        }

        /// <summary>
        /// 检查系统是否支持NVIDIA NVENC
        /// </summary>
        private bool CheckNVEncSupport()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                // 检查是否有NVENC编码器
                return output.Contains("h264_nvenc") || output.Contains("hevc_nvenc");
            }
        }

        /// <summary>
        /// 获取GPU信息
        /// </summary>
        public string GetGPUInfo()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-f lavfi -i testsrc=size=1920x1080:rate=1 -c:v h264_nvenc -t 1 -f null -",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string errorOutput = process.StandardError.ReadToEnd();
                process.WaitForExit();

                // 提取GPU信息
                var lines = errorOutput.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("CUDA") || line.Contains("NVIDIA"))
                        return line.Trim();
                }
                return "NVIDIA GPU information not found";
            }
        }
    }

    //// 示例使用
    //class Program
    //{
    //    static void Main(string[] args)
    //    {
    //        try
    //        {
    //            // 初始化压缩器 (需替换为实际FFmpeg路径)
    //            var compressor = new NVENCVideoCompressor(@"C:\ffmpeg\bin\ffmpeg.exe");

    //            // 显示GPU信息
    //            Console.WriteLine("GPU Info: " + compressor.GetGPUInfo());

    //            // 压缩视频
    //            compressor.CompressVideoWithNVENC(
    //                inputPath: @"C:\Videos\input.mp4",
    //                outputPath: @"C:\Videos\output_nvenc.mp4",
    //                preset: "p5",    // 平衡预设
    //                crf: 28,         // 质量因子
    //                bitrate: null    // 使用CRF模式而非固定比特率
    //            );

    //            Console.WriteLine("Video compression completed successfully!");
    //        }
    //        catch (Exception ex)
    //        {
    //            Console.WriteLine($"Error: {ex.Message}");
    //        }
    //    }
    //}
}



