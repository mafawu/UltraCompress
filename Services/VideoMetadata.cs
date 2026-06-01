using System;

namespace UltraCompress.Services;

public sealed class VideoMetadata
{
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public double? BitrateKbps { get; init; }
    public TimeSpan? Duration { get; init; }
    /// <summary>音频编码格式，如 "aac"、"mp3"、"opus" 等，用于判断是否能直通</summary>
    public string? AudioCodec { get; init; }
}
