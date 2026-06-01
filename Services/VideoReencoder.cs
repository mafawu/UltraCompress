using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace UltraCompress.Services;

public sealed class VideoReencodeSettings
{
    public bool UseHardwareAcceleration { get; set; } = true;
    public string Preset { get; set; } = "p5";
    public int? TargetWidth { get; set; }
    public int? TargetHeight { get; set; }
    public double? ScaleMultiplier { get; set; }
    public double? FrameRate { get; set; }
    public string? VideoBitrate { get; set; }
    public string AudioCodec { get; set; } = "copy";
    public int? Crf { get; set; }
    /// <summary>源文件的音频编码格式（如 "aac"），用于自动决定是否直通</summary>
    public string? SourceAudioCodec { get; set; }
    public bool RemoveAudio { get; set; }
    public string? VideoCodec { get; set; }
}

public sealed class VideoReencoder
{
    private enum ReencodeAttemptMode
    {
        FullGpu,
        NvencCpuFilter,
        Software
    }

    private readonly string _ffmpegPath;

    public VideoReencoder(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (!File.Exists(_ffmpegPath))
        {
            throw new FileNotFoundException($"FFmpeg executable not found at: {_ffmpegPath}");
        }
    }

    public bool IsHardwareAccelerationSupported()
    {
        // Require both NVENC encoders and CUDA hwaccel to be available
        var encodersAvailable = CheckEncoderAvailable("h264_nvenc") || CheckEncoderAvailable("hevc_nvenc");
        var hwAccelCudaAvailable = CheckHwAccelAvailable("cuda");
        // Additionally ensure CUDA runtime (nvcuda.dll) is present
        var cudaRuntimePresent = false;
        try
        {
            var sysDir = Environment.SystemDirectory;
            var nvcudaPath = Path.Combine(sysDir, "nvcuda.dll");
            cudaRuntimePresent = File.Exists(nvcudaPath);
        }
        catch
        {
            cudaRuntimePresent = false;
        }

        return encodersAvailable && hwAccelCudaAvailable && cudaRuntimePresent;
    }

    public VideoMetadata ProbeInputMetadata(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Input path is required", nameof(inputPath));
        }

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Input file not found: {inputPath}");
        }

        var ffprobePath = GetFFprobePath();
        if (ffprobePath is not null)
        {
            try
            {
                return ProbeWithFFprobe(ffprobePath, inputPath);
            }
            catch
            {
                // Fall back to parsing ffmpeg output if ffprobe fails
            }
        }

        return ProbeWithFFmpeg(inputPath);
    }

    public void Reencode(string inputPath, string outputPath, VideoReencodeSettings settings)
    {
        ReencodeAsync(inputPath, outputPath, settings).GetAwaiter().GetResult();
    }

    public async Task ReencodeAsync(
        string inputPath,
        string outputPath,
        VideoReencodeSettings settings,
        IProgress<VideoProgress>? progress = null,
        CancellationToken cancellationToken = default,
        double? totalDurationSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Input path is required", nameof(inputPath));
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path is required", nameof(outputPath));
        }

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Input file not found: {inputPath}");
        }

        if (settings.UseHardwareAcceleration && !IsHardwareAccelerationSupported())
        {
            settings.UseHardwareAcceleration = false;
        }

        var attemptModes = BuildAttemptModes(settings);
        var includeProgress = progress != null;
        int lastExitCode = -1;
        string lastStderr = string.Empty;

        foreach (var mode in attemptModes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { }
            }

            var modeSettings = BuildAttemptSettings(settings, mode);
            var (exitCode, stderr) = await ExecuteReencodeAttemptAsync(
                inputPath,
                outputPath,
                modeSettings,
                includeProgress,
                progress,
                cancellationToken,
                totalDurationSeconds);

            if (exitCode == 0)
            {
                return;
            }

            lastExitCode = exitCode;
            lastStderr = stderr;

            if (!ShouldRetryWithFallback(stderr, mode))
            {
                break;
            }
        }

        ThrowReencodeFailure(inputPath, lastExitCode, lastStderr);
    }

    private async Task<(int ExitCode, string Stderr)> ExecuteReencodeAttemptAsync(
        string inputPath,
        string outputPath,
        VideoReencodeSettings settings,
        bool includeProgress,
        IProgress<VideoProgress>? progress,
        CancellationToken cancellationToken,
        double? totalDurationSeconds)
    {
        var arguments = BuildArguments(inputPath, outputPath, settings, includeProgress);
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = string.Join(' ', arguments),
            UseShellExecute = false,
            RedirectStandardOutput = includeProgress,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start FFmpeg process");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task progressTask = Task.CompletedTask;
        if (includeProgress && process.StandardOutput != null && progress != null)
        {
            progressTask = MonitorProgressAsync(process.StandardOutput, progress, totalDurationSeconds, cancellationToken);
        }

        using var killRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
                // ignore kill exceptions
            }
        });

        await Task.WhenAll(progressTask, stderrTask, process.WaitForExitAsync());

        cancellationToken.ThrowIfCancellationRequested();

        var stderr = await stderrTask;
        return (process.ExitCode, stderr);
    }

    private static IReadOnlyList<ReencodeAttemptMode> BuildAttemptModes(VideoReencodeSettings settings)
    {
        if (!settings.UseHardwareAcceleration)
        {
            return new[] { ReencodeAttemptMode.Software };
        }

        return new[]
        {
            ReencodeAttemptMode.FullGpu,
            ReencodeAttemptMode.NvencCpuFilter,
            ReencodeAttemptMode.Software
        };
    }

    private static VideoReencodeSettings BuildAttemptSettings(VideoReencodeSettings source, ReencodeAttemptMode mode)
    {
        var copy = new VideoReencodeSettings
        {
            UseHardwareAcceleration = source.UseHardwareAcceleration,
            Preset = source.Preset,
            TargetWidth = source.TargetWidth,
            TargetHeight = source.TargetHeight,
            ScaleMultiplier = source.ScaleMultiplier,
            FrameRate = source.FrameRate,
            VideoBitrate = source.VideoBitrate,
            AudioCodec = source.AudioCodec,
            Crf = source.Crf,
            RemoveAudio = source.RemoveAudio,
            VideoCodec = source.VideoCodec,
            SourceAudioCodec = source.SourceAudioCodec
        };

        switch (mode)
        {
            case ReencodeAttemptMode.FullGpu:
                copy.UseHardwareAcceleration = true;
                break;
            case ReencodeAttemptMode.NvencCpuFilter:
                copy.UseHardwareAcceleration = false;
                if (string.IsNullOrWhiteSpace(copy.VideoCodec))
                {
                    copy.VideoCodec = "h264_nvenc";
                }
                break;
            default:
                copy.UseHardwareAcceleration = false;
                copy.VideoCodec = MapToSoftwareCodec(copy.VideoCodec);
                break;
        }

        return copy;
    }

    private static string? MapToSoftwareCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return codec;
        }

        return codec.Trim().ToLowerInvariant() switch
        {
            "h264_nvenc" => "libx264",
            "hevc_nvenc" => "libx265",
            _ => codec
        };
    }

    private static bool ShouldRetryWithFallback(string stderr, ReencodeAttemptMode mode)
    {
        if (mode == ReencodeAttemptMode.Software)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(stderr))
        {
            return true;
        }

        var error = stderr.ToLowerInvariant();
        return error.Contains("impossible to convert between the formats")
            || error.Contains("error reinitializing filters")
            || error.Contains("function not implemented")
            || error.Contains("no capable devices found")
            || error.Contains("cannot load nvcuda.dll")
            || error.Contains("nvenc")
            || error.Contains("cuda");
    }

    private static void ThrowReencodeFailure(string inputPath, int exitCode, string stderr)
    {
        var logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        var logFileName = $"{Path.GetFileNameWithoutExtension(inputPath) ?? "ffmpeg"}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log";
        try
        {
            Directory.CreateDirectory(logsDirectory);
            var logPath = Path.Combine(logsDirectory, logFileName);
            File.WriteAllText(logPath, stderr);
            throw new InvalidOperationException($"FFmpeg exited with code {exitCode}. See log: {logPath}");
        }
        catch (Exception logEx)
        {
            throw new InvalidOperationException($"FFmpeg exited with code {exitCode}: {stderr}\nFailed to write log: {logEx.Message}");
        }
    }

    public async Task<MemoryStream?> CaptureFrameAsync(string inputPath, TimeSpan? timestamp = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Input path is required", nameof(inputPath));
        }

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException($"Input file not found: {inputPath}");
        }

        var args = new List<string> { "-y" };
        if (timestamp.HasValue && timestamp.Value > TimeSpan.Zero)
        {
            args.AddRange(new[] { "-ss", FormatTimestamp(timestamp.Value) });
        }

        args.AddRange(new[] { "-i", Quote(inputPath), "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1" });

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = string.Join(' ', args),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return null;
        }

        var buffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
        await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0 || buffer.Length == 0)
        {
            buffer.Dispose();
            return null;
        }

        buffer.Position = 0;
        return buffer;
    }

    private static IEnumerable<string> BuildArguments(string inputPath, string outputPath, VideoReencodeSettings settings, bool includeProgress)
    {
        var args = new List<string>(capacity: 20)
        {
            "-y"
        };

        args.AddRange(new[] { "-i", Quote(inputPath) });

        // 先构建过滤器列表，以确定输出格式是否需要 CUDA
        var filters = new List<string>(capacity: 2);
        bool needsCudaOutput = false;

        if (settings.ScaleMultiplier.HasValue)
        {
            var multiplier = settings.ScaleMultiplier.Value;
            if (multiplier > 0 && Math.Abs(multiplier - 1.0) > 0.001)
            {
                var factorText = multiplier.ToString(CultureInfo.InvariantCulture);
                if (settings.UseHardwareAcceleration)
                {
                    filters.Add($"scale_cuda=w=trunc(iw*{factorText}/2)*2:h=trunc(ih*{factorText}/2)*2:format=nv12");
                    needsCudaOutput = true;
                }
                else
                {
                    filters.Add($"scale=trunc(iw*{factorText}/2)*2:trunc(ih*{factorText}/2)*2");
                }
            }
        }
        else if (settings.TargetWidth.HasValue || settings.TargetHeight.HasValue)
        {
            var width = settings.TargetWidth.HasValue ? settings.TargetWidth.Value.ToString(CultureInfo.InvariantCulture) : "-2";
            var height = settings.TargetHeight.HasValue ? settings.TargetHeight.Value.ToString(CultureInfo.InvariantCulture) : "-2";
            if (settings.UseHardwareAcceleration)
            {
                filters.Add($"scale_cuda={width}:{height}:format=nv12");
                needsCudaOutput = true;
            }
            else
            {
                filters.Add($"scale={width}:{height}");
            }
        }

        // 硬件加速参数：仅在需要 CUDA 过滤器时才保持输出格式为 cuda
        if (settings.UseHardwareAcceleration)
        {
            if (needsCudaOutput)
            {
                args.AddRange(new[] { "-hwaccel", "cuda", "-hwaccel_output_format", "cuda" });
            }
            else
            {
                // 无 CUDA 过滤器时不用 -hwaccel_output_format cuda，
                // 避免 ffmpeg 的 auto_scale 过滤器因无法转换 cuda→yuv420p 而失败
                args.AddRange(new[] { "-hwaccel", "cuda" });
            }
        }

        if (filters.Count > 0)
        {
            args.Add("-vf");
            args.Add(Quote(string.Join(",", filters)));
        }

        var videoCodec = !string.IsNullOrWhiteSpace(settings.VideoCodec)
            ? settings.VideoCodec!
            : settings.UseHardwareAcceleration ? "h264_nvenc" : "libx264";
        args.AddRange(new[] { "-c:v", videoCodec });
        args.AddRange(new[] { "-preset", settings.Preset });

        if (videoCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(new[] { "-pix_fmt", "nv12" });
        }

        if (settings.FrameRate.HasValue)
        {
            args.AddRange(new[] { "-r", settings.FrameRate.Value.ToString(CultureInfo.InvariantCulture) });
        }

        if (!string.IsNullOrWhiteSpace(settings.VideoBitrate))
        {
            args.AddRange(new[] { "-b:v", settings.VideoBitrate! });
        }

        if (settings.Crf.HasValue)
        {
            var crfValue = settings.Crf.Value.ToString(CultureInfo.InvariantCulture);
            if (settings.UseHardwareAcceleration)
            {
                args.AddRange(new[] { "-cq", crfValue });
            }
            else
            {
                args.AddRange(new[] { "-crf", crfValue });
            }
        }

        if (settings.RemoveAudio)
        {
            args.Add("-an");
        }
        else
        {
            // 源音频为 AAC 时自动直通（copy），避免不必要的 CPU 重编码
            var srcCodec = settings.SourceAudioCodec?.Trim().ToLowerInvariant();
            var effectiveAudioCodec = (srcCodec is "aac" or "mp4a")
                ? "copy"
                : settings.AudioCodec;

            if (!string.IsNullOrWhiteSpace(effectiveAudioCodec))
            {
                args.AddRange(new[] { "-c:a", effectiveAudioCodec });
            }
        }

        if (includeProgress)
        {
            args.AddRange(new[] { "-progress", "pipe:1", "-nostats" });
        }

        args.Add(Quote(outputPath));
        return args;
    }

    private async Task MonitorProgressAsync(StreamReader reader, IProgress<VideoProgress> progress, double? durationSeconds, CancellationToken cancellationToken)
    {
        var buffer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            buffer[parts[0]] = parts[1];

            if (parts[0].Equals("progress", StringComparison.OrdinalIgnoreCase))
            {
                var report = BuildVideoProgress(buffer, durationSeconds);
                progress.Report(report);
                if (parts[1].Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                buffer.Clear();
            }
        }
    }

    private static VideoProgress BuildVideoProgress(IDictionary<string, string> stage, double? durationSeconds)
    {
        TimeSpan? processedTime = null;
        // ffmpeg's -progress reports out_time_ms in microseconds (despite the name)
#if NET8_0_OR_GREATER
        if (stage.TryGetValue("out_time_ms", out var outTimeMsText) && long.TryParse(outTimeMsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var outTimeUs))
        {
            processedTime = TimeSpan.FromMicroseconds(outTimeUs);
        }
#else
        if (stage.TryGetValue("out_time_ms", out var outTimeMsText) && long.TryParse(outTimeMsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var outTimeUs))
        {
            // Convert microseconds to milliseconds for older frameworks
            processedTime = TimeSpan.FromMilliseconds(outTimeUs / 1000.0);
        }
#endif
        else if (stage.TryGetValue("out_time", out var outTimeText) && TimeSpan.TryParse(outTimeText, CultureInfo.InvariantCulture, out var parsedOutTime))
        {
            processedTime = parsedOutTime;
        }

        long? processedBytes = null;
        if (stage.TryGetValue("total_size", out var totalSizeText) && long.TryParse(totalSizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalSize))
        {
            processedBytes = totalSize;
        }

        double? percent = null;
        if (processedTime.HasValue && durationSeconds.HasValue && durationSeconds.Value > 0)
        {
            percent = Math.Min(100.0, processedTime.Value.TotalSeconds / durationSeconds.Value * 100.0);
        }

        TimeSpan? remaining = null;
        if (percent.HasValue && percent.Value > 0 && percent.Value < 100 && durationSeconds.HasValue && processedTime.HasValue)
        {
            var remainingSeconds = Math.Max(0.0, durationSeconds.Value - processedTime.Value.TotalSeconds);
            remaining = TimeSpan.FromSeconds(remainingSeconds);
        }

        stage.TryGetValue("progress", out var progressState);
        stage.TryGetValue("speed", out var speed);

        return new VideoProgress
        {
            ProcessedTime = processedTime,
            ProcessedBytes = processedBytes,
            Percent = percent,
            EstimatedRemaining = remaining,
            ProgressState = progressState,
            Speed = speed
        };
    }

    private bool CheckEncoderAvailable(string encoderName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = "-hide_banner -encoders",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return false;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Contains(encoderName, StringComparison.OrdinalIgnoreCase);
    }

    private bool CheckHwAccelAvailable(string accelName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = "-hide_banner -hwaccels",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return false;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        // ffmpeg prints a list of available hwaccels, e.g., "cuda", "dxva2" etc.
        return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                     .Any(line => line.Trim().Equals(accelName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Quote(string value)
    {
        return $"\"{value}\"";
    }

    private string? GetFFprobePath()
    {
        var directory = Path.GetDirectoryName(_ffmpegPath);
        var candidate = Path.Combine(directory ?? AppContext.BaseDirectory, "ffprobe.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private VideoMetadata ProbeWithFFprobe(string ffprobePath, string inputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            Arguments = $"-v error -show_entries stream=index,codec_type,codec_name,width,height,bit_rate,avg_frame_rate,r_frame_rate,duration:format=duration,bit_rate -print_format json {Quote(inputPath)}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return new VideoMetadata();
        }

        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        return ParseMetadataFromFFprobe(output);
    }

    private VideoMetadata ProbeWithFFmpeg(string inputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = $"-hide_banner -i {Quote(inputPath)} -f null -",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return new VideoMetadata();
        }

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return ParseMetadataFromFFmpegOutput(stderr);
    }

    private static VideoMetadata ParseMetadataFromFFprobe(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new VideoMetadata();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            int? width = null;
            int? height = null;
            double? frameRate = null;
            double? bitrateKbps = null;
            double? durationSeconds = null;
            string? audioCodec = null;

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var codecTypeElement))
                    {
                        continue;
                    }

                    var codecType = codecTypeElement.GetString();

                    if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        if (stream.TryGetProperty("width", out var widthElement) && widthElement.TryGetInt32(out var w))
                        {
                            width = w;
                        }

                        if (stream.TryGetProperty("height", out var heightElement) && heightElement.TryGetInt32(out var h))
                        {
                            height = h;
                        }

                        frameRate ??= ParseFrameRate(stream);
                        bitrateKbps ??= ParseBitrateFromElement(stream);
                    }
                    else if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase) && audioCodec == null)
                    {
                        if (stream.TryGetProperty("codec_name", out var codecNameElement))
                        {
                            audioCodec = codecNameElement.GetString();
                        }
                    }
                }
            }

            if (root.TryGetProperty("format", out var formatElement))
            {
                durationSeconds ??= ParseDurationFromElement(formatElement);
                bitrateKbps ??= ParseBitrateFromElement(formatElement);
            }

            return new VideoMetadata
            {
                Width = width,
                Height = height,
                FrameRate = frameRate,
                BitrateKbps = bitrateKbps,
                Duration = durationSeconds.HasValue ? TimeSpan.FromSeconds(durationSeconds.Value) : null,
                AudioCodec = audioCodec
            };
        }
        catch (JsonException)
        {
            return new VideoMetadata();
        }
    }

    private static double? ParseDurationFromElement(JsonElement element)
    {
        if (element.TryGetProperty("duration", out var durationElement))
        {
            if (durationElement.ValueKind == JsonValueKind.Number && durationElement.TryGetDouble(out var numeric) && numeric > 0)
            {
                return numeric;
            }

            if (durationElement.ValueKind == JsonValueKind.String && double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return parsed;
            }
        }

        return null;
    }

    private static double? ParseFrameRate(JsonElement element)
    {
        if (element.TryGetProperty("r_frame_rate", out var rateElement) && TryParseRational(rateElement.GetString(), out var rate))
        {
            return rate;
        }

        if (element.TryGetProperty("avg_frame_rate", out var avgElement) && TryParseRational(avgElement.GetString(), out var avgRate))
        {
            return avgRate;
        }

        return null;
    }

    private static bool TryParseRational(string? value, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
        {
            result = numerator / denominator;
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar))
        {
            result = scalar;
            return true;
        }

        return false;
    }

    private static double? ParseBitrateFromElement(JsonElement element)
    {
        if (element.TryGetProperty("bit_rate", out var bitRateElement) && TryParseBitrate(bitRateElement, out var bitrate))
        {
            return bitrate;
        }

        return null;
    }

    private static bool TryParseBitrate(JsonElement element, out double bitrateKbps)
    {
        bitrateKbps = 0;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var numeric))
        {
            if (numeric > 0)
            {
                bitrateKbps = numeric / 1000.0;
                return true;
            }

            return false;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                bitrateKbps = parsed / 1000.0;
                return true;
            }
        }

        return false;
    }

    private static VideoMetadata ParseMetadataFromFFmpegOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new VideoMetadata();
        }

        var width = default(int?);
        var height = default(int?);
        var frameRate = default(double?);
        var bitrate = default(double?);
        var duration = ParseDurationFromOutput(output);

        var videoMatch = Regex.Match(output, @"Video:.*?(?<width>\d{2,5})x(?<height>\d{2,5})", RegexOptions.IgnoreCase);
        if (videoMatch.Success)
        {
            if (int.TryParse(videoMatch.Groups["width"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedWidth))
            {
                width = parsedWidth;
            }

            if (int.TryParse(videoMatch.Groups["height"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedHeight))
            {
                height = parsedHeight;
            }
        }

        var fpsMatch = Regex.Match(output, @"(?<fps>\d+(?:\.\d+)?)\s*fps", RegexOptions.IgnoreCase);
        if (fpsMatch.Success &&
            double.TryParse(fpsMatch.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fpsValue))
        {
            frameRate = fpsValue;
        }

        var bitrateMatch = Regex.Match(output, @"bitrate:\s*(?<bitrate>\d+(?:\.\d+)?)\s*kb\/s", RegexOptions.IgnoreCase);
        if (bitrateMatch.Success &&
            double.TryParse(bitrateMatch.Groups["bitrate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bitrateValue))
        {
            bitrate = bitrateValue;
        }

        return new VideoMetadata
        {
            Width = width,
            Height = height,
            FrameRate = frameRate,
            BitrateKbps = bitrate,
            Duration = duration
        };
    }

    private static TimeSpan? ParseDurationFromOutput(string output)
    {
        var durationMatch = Regex.Match(output, @"Duration:\s*(?<duration>\d{2}:\d{2}:\d{2}\.\d+)");
        if (durationMatch.Success)
        {
            if (TimeSpan.TryParse(durationMatch.Groups["duration"].Value, CultureInfo.InvariantCulture, out var duration))
            {
                return duration;
            }
        }

        return null;
    }

    private static string FormatTimestamp(TimeSpan timestamp)
    {
        return timestamp.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }
}
