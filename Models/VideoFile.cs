using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using UltraCompress.Services;

namespace UltraCompress.Models
{
    public partial class VideoFile : ObservableObject
    {
        [ObservableProperty] private int _index;
        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string _fullPath = string.Empty;
        [ObservableProperty] private string? _outputPath;
        [ObservableProperty] private double _originalSize; // MB
        [ObservableProperty] private double? _compressedSize; // MB
        [ObservableProperty] private string _status = "pending"; // pending, processing, done, error
        [ObservableProperty] private double _progress;
        [ObservableProperty] private TimeSpan? _duration;
        [ObservableProperty] private TimeSpan? _elapsed;
        [ObservableProperty] private TimeSpan? _remaining;
        [ObservableProperty] private double? _expectedSize; // MB
        [ObservableProperty] private string? _errorMessage;
        [ObservableProperty] private VideoMetadata? _metadata;
        [ObservableProperty] private bool _canOpen;
        [ObservableProperty] private Bitmap? _thumbnail;

        // 辅助属性：用于 UI 显示颜色
        public string StatusColor => Status switch
        {
            "processing" => "#60A5FA", // Blue-400
            "done" => "#4ADE80",       // Green-400
            "error" => "#F87171",      // Red-400
            _ => "#94A3B8"              // Slate-400
        };

        public string StatusText => Status switch
        {
            "processing" => "处理中...",
            "done" => "已完成",
            "error" => "失败",
            _ => "等待中"
        };

        public bool CanDelete => Status == "pending" || Status == "error";
        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
        public bool HasThumbnail => Thumbnail != null;

        public string ResolutionText
        {
            get
            {
                var width = Metadata?.Width;
                var height = Metadata?.Height;
                if (width.HasValue && width.Value > 0 && height.HasValue && height.Value > 0)
                {
                    return $"{width.Value}x{height.Value}";
                }

                return "--";
            }
        }

        partial void OnThumbnailChanging(Bitmap? value)
        {
            _thumbnail?.Dispose();
        }

        partial void OnThumbnailChanged(Bitmap? value)
        {
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }
}