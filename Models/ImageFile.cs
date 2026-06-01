using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UltraCompress.Models
{
    public partial class ImageFile : ObservableObject
    {
        [ObservableProperty] private int _index;
        [ObservableProperty] private string _fileName = string.Empty;
        [ObservableProperty] private string _fullPath = string.Empty;
        [ObservableProperty] private long _originalSizeBytes;
        [ObservableProperty] private long? _newSizeBytes;
        [ObservableProperty] private string _status = "Pending"; // Pending, Processing, Done, Error
        [ObservableProperty] private string? _errorMessage;
        [ObservableProperty] private Bitmap? _thumbnail;
        [ObservableProperty] private string? _compressedPath;
        [ObservableProperty] private long? _predictedSizeBytes;

        public string OriginalSizeText => FormatBytes(OriginalSizeBytes);
        public string PredictedSizeText => PredictedSizeBytes.HasValue ? $"~{FormatBytes(PredictedSizeBytes.Value)}" : "-";
        public string NewSizeText => NewSizeBytes.HasValue ? FormatBytes(NewSizeBytes.Value) : PredictedSizeText;

        public string ReductionText
        {
            get
            {
                var targetBytes = NewSizeBytes ?? PredictedSizeBytes;
                if (OriginalSizeBytes > 0 && targetBytes.HasValue)
                {
                    var percent = (1.0 - (double)targetBytes.Value / OriginalSizeBytes) * 100.0;
                    return $"-{percent:F1}%";
                }
                return "-";
            }
        }

        public string StatusColor => Status switch
        {
            "Processing" => "#60A5FA", // Blue-400
            "Done" => "#4ADE80",       // Green-400
            "Error" => "#F87171",      // Red-400
            _ => "#94A3B8"             // Slate-400
        };

        partial void OnOriginalSizeBytesChanged(long value)
        {
            OnPropertyChanged(nameof(OriginalSizeText));
            OnPropertyChanged(nameof(ReductionText));
        }

        partial void OnNewSizeBytesChanged(long? value)
        {
            OnPropertyChanged(nameof(NewSizeText));
            OnPropertyChanged(nameof(ReductionText));
        }

        partial void OnPredictedSizeBytesChanged(long? value)
        {
            OnPropertyChanged(nameof(PredictedSizeText));
            OnPropertyChanged(nameof(NewSizeText));
            OnPropertyChanged(nameof(ReductionText));
        }

        partial void OnStatusChanged(string value)
        {
            OnPropertyChanged(nameof(StatusColor));
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}