using System;

namespace UltraCompress.Services;

public sealed class VideoProgress
{
    public TimeSpan? ProcessedTime { get; init; }
    public long? ProcessedBytes { get; init; }
    public double? Percent { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
    public string? ProgressState { get; init; }
    public string? Speed { get; init; }
}
