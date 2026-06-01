using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using UltraCompress.Models;
using UltraCompress.Services;
using Microsoft.VisualBasic.FileIO;

namespace UltraCompress.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v",
            ".flv", ".rmvb", ".rm", ".mpg", ".mpeg", ".m2ts", ".ts",
            ".webm", ".ogv", ".3gp", ".3g2", ".asf", ".f4v"
        };

        private static readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff"
        };

        private const string SourceImageFormatOption = "同源格式";

        private readonly VideoReencoder _reencoder = new();
        private readonly bool _hardwareAccelerationAvailable;
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _imageCts;
        private readonly ImageCompressorService _compressor = new ImageCompressorService();
        internal static bool IsSupportedVideoFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            return !string.IsNullOrWhiteSpace(extension) && _supportedExtensions.Contains(extension);
        }
        
        internal static bool IsSupportedImageFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            return !string.IsNullOrWhiteSpace(extension) && _imageExtensions.Contains(extension);
        }

        private static string GetOutputPath(string inputPath)
        {
            var directory = Path.GetDirectoryName(inputPath) ?? AppContext.BaseDirectory;
            var name = Path.GetFileNameWithoutExtension(inputPath);
            var candidate = Path.Combine(directory, $"{name}_ultracompress.mp4");
            var index = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{name}_ultracompress_{index++}.mp4");
            }

            return candidate;
        }


        private string ResolveImageOutputExtension(string sourcePath)
        {
            if (IsSourceFormatSelected())
            {
                var sourceExtension = Path.GetExtension(sourcePath);
                if (!string.IsNullOrWhiteSpace(sourceExtension))
                {
                    var normalized = sourceExtension.ToLowerInvariant();
                    if (_imageExtensions.Contains(normalized))
                    {
                        return normalized;
                    }

                    return sourceExtension;
                }
            }

            return NormalizeFormatKey(SelectedImageFormat) switch
            {
                "PNG" => ".png",
                "WEBP" => ".webp",
                "AVIF" => ".avif",
                _ => ".jpg"
            };
        }

        private string BuildImageOutputPath(string sourcePath)
        {
            var directory = Path.GetDirectoryName(sourcePath) ?? AppContext.BaseDirectory;
            var name = Path.GetFileNameWithoutExtension(sourcePath);
            var extension = ResolveImageOutputExtension(sourcePath);
            var candidate = Path.Combine(directory, $"{name}_ultracompress{extension}");
            var index = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(directory, $"{name}_ultracompress_{index++}{extension}");
            }

            return candidate;
        }

        private bool IsSourceFormatSelected()
            => string.Equals(SelectedImageFormat, SourceImageFormatOption, StringComparison.OrdinalIgnoreCase);

        private static string NormalizeFormatKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "JPG";
            }

            var trimmed = value.Trim();
            if (trimmed.StartsWith('.'))
            {
                trimmed = trimmed[1..];
            }

            var upper = trimmed.ToUpperInvariant();
            return upper switch
            {
                "JPEG" => "JPG",
                _ => upper
            };
        }

        private string GetEffectiveImageFormatKey(ImageFile image)
        {
            if (IsSourceFormatSelected())
            {
                var sourceExt = Path.GetExtension(image.FullPath);
                if (!string.IsNullOrWhiteSpace(sourceExt))
                {
                    return NormalizeFormatKey(sourceExt);
                }
            }

            return NormalizeFormatKey(SelectedImageFormat);
        }


        // --- Image Properties ---
        [ObservableProperty] private ObservableCollection<ImageFile> _imageFiles = new();
        [ObservableProperty] private ImageFile? _selectedImage;
        [ObservableProperty] private int _imageQuality = 80;
        [ObservableProperty] private string _selectedImageFormat = "JPG"; // JPG, PNG, WEBP
        [ObservableProperty] private bool _resizeImages;
        [ObservableProperty] private int _resizeWidth = 1920;
        [ObservableProperty] private int _resizeHeight;
        [ObservableProperty] private double _imageGlobalProgress;
        [ObservableProperty] private string _imageProgressCountText = "0/0";
        [ObservableProperty] private bool _showImagePreview = false;

        // 状态属性
        [ObservableProperty] private bool _isProcessing;
        [ObservableProperty] private bool _isAddingFiles;
        [ObservableProperty] private bool _isImageProcessing;
        [ObservableProperty] private bool _isAddingImages;
        [ObservableProperty] private bool _isReplacingVideos;
        [ObservableProperty] private bool _isReplacingImages;
        [ObservableProperty] private string _currentView = "Video";
        [ObservableProperty] private double _globalProgress;
        [ObservableProperty] private double _targetBitrateMbps = 2.5;
        [ObservableProperty] private int _selectedBitrateMode; // 0: custom, 1: multiplier, 2: original
        [ObservableProperty] private double _targetBitrateMultiplier = 1.0;
        [ObservableProperty] private int _selectedEncoderIndex = 2;
        [ObservableProperty] private int _maxConcurrentEncodes = 2;
        [ObservableProperty] private int _minBitrateFilterKbps;
        [ObservableProperty] private bool _isVideoEncodingExpanded = true;
        [ObservableProperty] private bool _isImportFilterExpanded;
        [ObservableProperty] private bool _isResolutionExpanded;
        [ObservableProperty] private bool _isParallelEncodingExpanded;

        public bool IsVideoEncodingCollapsed => !IsVideoEncodingExpanded;
        public bool IsImportFilterCollapsed => !IsImportFilterExpanded;
        public bool IsResolutionCollapsed => !IsResolutionExpanded;
        public bool IsParallelEncodingCollapsed => !IsParallelEncodingExpanded;

        public string VideoEncodingSummary
        {
            get
            {
                var encoder = SelectedEncoderIndex switch
                {
                    0 => "H.264 (x264)",
                    1 => "H.265 (x265)",
                    2 => "NVENC H.264",
                    3 => "NVENC H.265",
                    _ => "H.264"
                };
                var bitrate = SelectedBitrateMode switch
                {
                    0 => $"{TargetBitrateMbps:F1} Mbps",
                    1 => $"{TargetBitrateMultiplier:F1}×",
                    _ => "原始码率"
                };
                return $"{encoder} · {bitrate}";
            }
        }

        public string ImportFilterSummary => MinBitrateFilterKbps > 0 ? $"≥ {MinBitrateFilterKbps} kbps" : "未限制";

        public string ResolutionSummary
        {
            get
            {
                return SelectedResolutionMode switch
                {
                    1 => "1080p",
                    2 => "720p",
                    3 => $"{CustomWidth}×{CustomHeight}",
                    4 => $"{ScaleFactor:F1}×",
                    _ => "原始"
                };
            }
        }

        public string ParallelEncodingSummary => $"{MaxConcurrentEncodes} 路并发";

        public bool IsVideoView => string.Equals(CurrentView, "Video", StringComparison.OrdinalIgnoreCase);
        public bool IsNotProcessing => !IsProcessing;
        public bool IsImageView => string.Equals(CurrentView, "Image", StringComparison.OrdinalIgnoreCase);
        public bool IsAnalyticsView => string.Equals(CurrentView, "Analytics", StringComparison.OrdinalIgnoreCase);

        private int _selectedResolutionMode;
        public int SelectedResolutionMode
        {
            get => _selectedResolutionMode;
            set
            {
                if (SetProperty(ref _selectedResolutionMode, value))
                {
                    OnPropertyChanged(nameof(IsCustomResolutionMode));
                    OnPropertyChanged(nameof(IsScaleResolutionMode));
                    OnPropertyChanged(nameof(ResolutionSummary));
                    UpdateResolutionPreview();
                    UpdatePredictedSizes();
                }
            }
        }

        public bool IsCustomResolutionMode => SelectedResolutionMode == 3;
        public bool IsScaleResolutionMode => SelectedResolutionMode == 4;
        public bool IsCustomBitrateMode => SelectedBitrateMode == 0;
        public bool IsMultiplierBitrateMode => SelectedBitrateMode == 1;
        public bool IsOriginalBitrateMode => SelectedBitrateMode == 2;

        private int _customWidth = 1920;
        public int CustomWidth
        {
            get => _customWidth;
            set
            {
                if (SetProperty(ref _customWidth, value))
                {
                    OnPropertyChanged(nameof(ResolutionSummary));
                    UpdateResolutionPreview();
                    UpdatePredictedSizes();
                }
            }
        }

        private int _customHeight = 1080;
        public int CustomHeight
        {
            get => _customHeight;
            set
            {
                if (SetProperty(ref _customHeight, value))
                {
                    OnPropertyChanged(nameof(ResolutionSummary));
                    UpdateResolutionPreview();
                    UpdatePredictedSizes();
                }
            }
        }

        private double _scaleFactor = 1.0;
        public double ScaleFactor
        {
            get => _scaleFactor;
            set
            {
                var clamped = Math.Clamp(value, 0.25, 3.0);
                if (SetProperty(ref _scaleFactor, clamped))
                {
                    OnPropertyChanged(nameof(ResolutionSummary));
                    UpdateResolutionPreview();
                    UpdatePredictedSizes();
                }
            }
        }

        private string _referenceResolutionText = "--";
        public string ReferenceResolutionText
        {
            get => _referenceResolutionText;
            private set => SetProperty(ref _referenceResolutionText, value);
        }

        private string _targetResolutionText = "--";
        public string TargetResolutionText
        {
            get => _targetResolutionText;
            private set => SetProperty(ref _targetResolutionText, value);
        }

        private string _cpuUsageText = "CPU: --%";
        public string CpuUsageText
        {
            get => _cpuUsageText;
            private set => SetProperty(ref _cpuUsageText, value);
        }

        private string _gpuUsageText = "GPU: --%";
        public string GpuUsageText
        {
            get => _gpuUsageText;
            private set => SetProperty(ref _gpuUsageText, value);
        }

        [ObservableProperty]
        private string _hardwareAccelerationStatus = "硬件加速：检测中...";

        // 统计属性
        [ObservableProperty] private int _totalFilesCount;
        [ObservableProperty] private string _totalOriginalSize = "0 MB";
        [ObservableProperty] private string _totalCompressedSize = "0 MB";
        [ObservableProperty] private string _savedPercent = "0%";

        // 集合
        public ObservableCollection<VideoFile> Files { get; } = new();
        public IReadOnlyList<double> ScaleOptions { get; } = new[] { 0.25, 0.5, 0.75, 1.0, 2.0, 3.0 };
        public IReadOnlyList<double> BitrateMultiplierOptions { get; } = new[] { 0.25, 0.5, 0.75, 1.0, 2.0, 3.0 };
        public IReadOnlyList<string> ImageFormats { get; } = new[] { SourceImageFormatOption, "JPG", "PNG", "WEBP", "AVIF" };
        private readonly Process _currentProcess = Process.GetCurrentProcess();
        private TimeSpan _lastCpuTime;
        private DateTime _lastCpuSampleTime;
        private CancellationTokenSource? _resourceCts;
        private readonly Dictionary<VideoFile, DateTime> _processingStartTimes = new();
        private readonly Dictionary<string, Task> _thumbnailLoadTasks = new(StringComparer.OrdinalIgnoreCase);
		private string? _videoSortKey;
		private bool _videoSortAscending = true;
		private string? _imageSortKey;
		private bool _imageSortAscending = true;

        public MainWindowViewModel()
        {
            _hardwareAccelerationAvailable = _reencoder.IsHardwareAccelerationSupported();
            HardwareAccelerationStatus = _hardwareAccelerationAvailable ? "硬件加速：可用" : "硬件加速：不可用";
            if (_hardwareAccelerationAvailable)
            {
                if (SelectedEncoderIndex < 2)
                {
                    SelectedEncoderIndex = 2; // Prefer NVENC H.264 by default when available
                }
            }
            else if (SelectedEncoderIndex >= 2)
            {
                SelectedEncoderIndex = 0; // Fallback to software H.264 when GPU unavailable
            }

            Files.CollectionChanged += OnFilesCollectionChanged;
            ImageFiles.CollectionChanged += (_, __) =>
            {
                ReindexImages();
                StartImageCompressionCommand.NotifyCanExecuteChanged();
			ReplaceImageOutputsCommand.NotifyCanExecuteChanged();
			UpdateImagePredictions();
		UpdateImageProgressSummary();
            };

            if (Design.IsDesignMode)
            {
                AddMockFiles();
            }
            else
            {
                StartResourceMonitor();
            }

            UpdateResolutionPreview();
            UpdatePredictedSizes();
            UpdateImageProgressSummary();

            // Ensure initial view state propagates to bindings so the default view is visible.
            OnPropertyChanged(nameof(IsVideoView));
            OnPropertyChanged(nameof(IsImageView));
            OnPropertyChanged(nameof(IsAnalyticsView));
        }

		partial void OnIsProcessingChanged(bool value)
        {
            StartBatchCommand.NotifyCanExecuteChanged();
            CancelBatchCommand.NotifyCanExecuteChanged();
            AddFilesCommand.NotifyCanExecuteChanged();
            ReplaceOutputsCommand.NotifyCanExecuteChanged();
			SortVideoColumnCommand.NotifyCanExecuteChanged();
			OnPropertyChanged(nameof(IsNotProcessing));
        }

        partial void OnIsAddingFilesChanged(bool value)
        {
            AddFilesCommand.NotifyCanExecuteChanged();
        }

		partial void OnIsImageProcessingChanged(bool value)
		{
			StartImageCompressionCommand.NotifyCanExecuteChanged();
			CancelImageCompressionCommand.NotifyCanExecuteChanged();
			AddImagesCommand.NotifyCanExecuteChanged();
			ReplaceImageOutputsCommand.NotifyCanExecuteChanged();
			SortImageColumnCommand.NotifyCanExecuteChanged();
        }

		partial void OnIsVideoEncodingExpandedChanged(bool value)
		{
			OnPropertyChanged(nameof(IsVideoEncodingCollapsed));
		}

		partial void OnIsImportFilterExpandedChanged(bool value)
		{
			OnPropertyChanged(nameof(IsImportFilterCollapsed));
		}

		partial void OnIsResolutionExpandedChanged(bool value)
		{
			OnPropertyChanged(nameof(IsResolutionCollapsed));
		}

		partial void OnIsParallelEncodingExpandedChanged(bool value)
		{
			OnPropertyChanged(nameof(IsParallelEncodingCollapsed));
		}

		partial void OnIsReplacingVideosChanged(bool value)
		{
			ReplaceOutputsCommand.NotifyCanExecuteChanged();
		}

		partial void OnIsReplacingImagesChanged(bool value)
		{
			ReplaceImageOutputsCommand.NotifyCanExecuteChanged();
		}

        partial void OnIsAddingImagesChanged(bool value)
        {
            AddImagesCommand.NotifyCanExecuteChanged();
        }

        partial void OnCurrentViewChanged(string value)
        {
            OnPropertyChanged(nameof(IsVideoView));
            OnPropertyChanged(nameof(IsImageView));
            OnPropertyChanged(nameof(IsAnalyticsView));
        }

		partial void OnSelectedImageChanged(ImageFile? value)
		{
			if (value != null && ShowImagePreview)
			{
				_ = EnsureImageThumbnailAsync(value);
			}
		}

		partial void OnShowImagePreviewChanged(bool value)
		{
			if (value && SelectedImage != null)
			{
				_ = EnsureImageThumbnailAsync(SelectedImage);
			}
		}

        private void OnFilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            StartBatchCommand.NotifyCanExecuteChanged();
            ReplaceOutputsCommand.NotifyCanExecuteChanged();
            UpdateResolutionPreview();
            UpdatePredictedSizes();
        }

        [RelayCommand]
        private void SwitchView(string? view)
        {
            if (string.IsNullOrWhiteSpace(view))
            {
                CurrentView = "Video";
                return;
            }

            CurrentView = view;
        }

        [RelayCommand(CanExecute = nameof(CanAddFiles))]
        private async Task AddFiles()
        {
            if (IsProcessing)
            {
                return;
            }

            var paths = await ShowFilePickerAsync();
            if (paths is { Length: > 0 })
            {
                await AddFilesFromPathsAsync(paths);
            }
        }

        private bool CanAddFiles() => !IsProcessing && !IsAddingFiles;

        [RelayCommand]
        private void RemoveFile(VideoFile file)
        {
            if (IsProcessing || file == null)
            {
                return;
            }

            file.Thumbnail = null;
            Files.Remove(file);
            ReindexFiles();
            UpdateStats();
            UpdatePredictedSizes();
        }

        [RelayCommand]
        private void ClearCompleted()
        {
            if (IsProcessing)
            {
                return;
            }

            var toRemove = Files.Where(f => f.Status == "done").ToList();
            foreach (var item in toRemove)
            {
                item.Thumbnail = null;
                Files.Remove(item);
            }

            ReindexFiles();
            UpdateStats();
            UpdatePredictedSizes();
        }

        [RelayCommand]
        private void ClearAll()
        {
            if (IsProcessing)
            {
                return;
            }

            foreach (var file in Files)
            {
                file.Thumbnail = null;
            }

            Files.Clear();
            _processingStartTimes.Clear();
            GlobalProgress = 0;
            UpdateStats();
        }

        [RelayCommand(CanExecute = nameof(CanAddImages))]
        private async Task AddImages()
        {
            if (IsImageProcessing)
            {
                return;
            }

            var paths = await ShowImagePickerAsync();
            if (paths is { Length: > 0 })
            {
                await AddImagesFromPathsAsync(paths);
            }
        }

        [RelayCommand]
        private void ClearImageCompleted()
        {
            if (IsImageProcessing)
            {
                return;
            }

            var done = ImageFiles.Where(f => string.Equals(f.Status, "Done", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var file in done)
            {
                ImageFiles.Remove(file);
            }

            ReindexImages();

            StartImageCompressionCommand.NotifyCanExecuteChanged();
			ReplaceImageOutputsCommand.NotifyCanExecuteChanged();
		UpdateImageProgressSummary();
        }

        [RelayCommand]
        private void ClearImageAll()
        {
            if (IsImageProcessing)
            {
                return;
            }

            foreach (var file in ImageFiles)
            {
                file.Thumbnail = null;
            }

            ImageFiles.Clear();
            SelectedImage = null;
            ImageGlobalProgress = 0;
            NotifyImageReplacementState();
            StartImageCompressionCommand.NotifyCanExecuteChanged();
		UpdateImageProgressSummary();
        }

        [RelayCommand]
        private void RemoveImage(ImageFile file)
        {
            if (IsImageProcessing || file == null)
            {
                return;
            }

            ImageFiles.Remove(file);
            ReindexImages();
            StartImageCompressionCommand.NotifyCanExecuteChanged();
			ReplaceImageOutputsCommand.NotifyCanExecuteChanged();
        }

		[RelayCommand]
		private void OpenCompressedImage(ImageFile file)
		{
			if (file == null || string.IsNullOrWhiteSpace(file.CompressedPath))
			{
				return;
			}

			var targetPath = file.CompressedPath;
			if (string.IsNullOrWhiteSpace(targetPath))
			{
				return;
			}

			var directory = Path.GetDirectoryName(targetPath);
			if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
			{
				return;
			}

			try
			{
				if (OperatingSystem.IsWindows() && File.Exists(targetPath))
				{
					Process.Start(new ProcessStartInfo
					{
						FileName = "explorer",
						Arguments = $"/select,\"{targetPath}\"",
						UseShellExecute = true
					});
				}
				else
				{
					Process.Start(new ProcessStartInfo
					{
						FileName = directory,
						UseShellExecute = true
					});
				}
			}
			catch
			{
				// ignore failures
			}
		}

		[RelayCommand(CanExecute = nameof(CanSortVideos))]
		private void SortVideoColumn(string? columnKey)
		{
			ApplyVideoSort(columnKey);
		}

		[RelayCommand(CanExecute = nameof(CanSortImages))]
		private void SortImageColumn(string? columnKey)
		{
			ApplyImageSort(columnKey);
		}

		private bool CanSortVideos() => !IsProcessing;

		private bool CanSortImages() => !IsImageProcessing;

		[RelayCommand(CanExecute = nameof(CanReplaceImageOutputs))]
		private async Task ReplaceImageOutputs()
		{
			if (IsImageProcessing || IsReplacingImages)
			{
				return;
			}

			IsReplacingImages = true;

			var candidates = ImageFiles
				.Where(f => string.Equals(f.Status, "Done", StringComparison.OrdinalIgnoreCase)
					&& !string.IsNullOrWhiteSpace(f.CompressedPath)
					&& File.Exists(f.CompressedPath!))
				.ToList();

			foreach (var image in candidates)
			{
				var originalPath = image.FullPath;
				var compressedPath = image.CompressedPath;
				if (string.IsNullOrWhiteSpace(originalPath) || string.IsNullOrWhiteSpace(compressedPath))
				{
					continue;
				}

				try
				{
					if (File.Exists(originalPath))
					{
						DeleteToRecycleBin(originalPath);
					}

					File.Move(compressedPath, originalPath, overwrite: true);
					var info = new FileInfo(originalPath);
					image.NewSizeBytes = info.Length;
					// 清空 CompressedPath 表示已替换完成
					image.CompressedPath = null;
					image.Status = "Done";
				}
				catch (Exception ex)
				{
					// 失败仍保留 CompressedPath，下次可重试
					image.Status = "Done";
					image.ErrorMessage = ex.Message;
				}
			}

			IsReplacingImages = false;
			NotifyImageReplacementState();
			UpdateImagePredictions();
			UpdateImageProgressSummary();
		}

		private bool CanReplaceImageOutputs()
		{
			return !IsImageProcessing
				&& !IsReplacingImages
				&& ImageFiles.Any(f =>
					string.Equals(f.Status, "Done", StringComparison.OrdinalIgnoreCase)
					&& !string.IsNullOrWhiteSpace(f.CompressedPath)
					&& File.Exists(f.CompressedPath));
		}

        [RelayCommand(CanExecute = nameof(CanReplaceOutputs))]
        private async Task ReplaceOutputs()
        {
            if (IsProcessing || IsReplacingVideos)
            {
                return;
            }

            IsReplacingVideos = true;

            var candidates = Files.Where(f =>
                f.Status == "done"
                && !string.IsNullOrWhiteSpace(f.OutputPath)
                && File.Exists(f.OutputPath)
                && !string.Equals(f.OutputPath, f.FullPath, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var file in candidates)
            {
                var originalPath = file.FullPath;
                var tempOutput = file.OutputPath;

                try
                {
                    if (File.Exists(originalPath))
                    {
                        DeleteToRecycleBin(originalPath);
                    }

                    File.Move(tempOutput, originalPath, overwrite: true);
                    // 清空 OutputPath 表示已替换完成，不再可替换
                    file.OutputPath = null;
                    file.CanOpen = true;
                    file.Status = "done";
                }
                catch (Exception ex)
                {
                    // 失败仍保留 OutputPath，下次可重试
                    file.Status = "done";
                    file.ErrorMessage = ex.Message;
                    file.CanOpen = false;
                }
            }

            IsReplacingVideos = false;
            UpdateStats();
            UpdatePredictedSizes();
        }

        private bool CanReplaceOutputs()
        {
            return !IsProcessing
                && !IsReplacingVideos
                && Files.Any(f =>
                    f.Status == "done"
                    && !string.IsNullOrWhiteSpace(f.OutputPath)
                    && File.Exists(f.OutputPath)
                    && !string.Equals(f.OutputPath, f.FullPath, StringComparison.OrdinalIgnoreCase));
        }

        [RelayCommand(CanExecute = nameof(CanStartBatch))]
        private async Task StartBatch()
        {
            if (IsProcessing)
            {
                return;
            }

            var pendingFiles = Files.Where(f => f.Status == "pending").ToList();
            if (pendingFiles.Count == 0)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            IsProcessing = true;
            GlobalProgress = 0;

            var baseSettings = BuildSettings();
            var totalItems = pendingFiles.Count;
            var concurrency = Math.Clamp(MaxConcurrentEncodes, 1, Math.Max(1, Environment.ProcessorCount));
            var semaphore = new SemaphoreSlim(concurrency);

            var tasks = pendingFiles.Select(file => ProcessOneFileAsync(file, baseSettings, totalItems, semaphore));
            await Task.WhenAll(tasks);

            _cts?.Dispose();
            _cts = null;
            IsProcessing = false;
            GlobalProgress = 0;
            UpdateStats();
            StartBatchCommand.NotifyCanExecuteChanged();
        }

        private async Task ProcessOneFileAsync(VideoFile file, VideoReencodeSettings baseSettings, int totalItems, SemaphoreSlim semaphore)
        {
            await semaphore.WaitAsync(_cts?.Token ?? CancellationToken.None);

            try
            {
                if (_cts?.IsCancellationRequested == true)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        file.Status = "pending";
                        file.Progress = 0;
                        file.Elapsed = null;
                        file.Remaining = null;
                        file.ExpectedSize = null;
                    });
                    return;
                }

                if (!File.Exists(file.FullPath))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        file.Status = "error";
                        file.ErrorMessage = "文件不存在";
                        file.Progress = 0;
                        file.Elapsed = null;
                        file.Remaining = null;
                        file.ExpectedSize = null;
                        RecalcGlobalProgress();
                    });
                    return;
                }

                // 构建每个文件的 setting（携带 SourceAudioCodec）
                var fileSettings = new VideoReencodeSettings
                {
                    UseHardwareAcceleration = baseSettings.UseHardwareAcceleration,
                    Preset = baseSettings.Preset,
                    TargetWidth = baseSettings.TargetWidth,
                    TargetHeight = baseSettings.TargetHeight,
                    ScaleMultiplier = baseSettings.ScaleMultiplier,
                    FrameRate = baseSettings.FrameRate,
                    VideoBitrate = baseSettings.VideoBitrate,
                    AudioCodec = baseSettings.AudioCodec,
                    Crf = baseSettings.Crf,
                    RemoveAudio = baseSettings.RemoveAudio,
                    VideoCodec = baseSettings.VideoCodec,
                    SourceAudioCodec = file.Metadata?.AudioCodec
                };

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    file.Status = "processing";
                    file.ErrorMessage = null;
                    file.Progress = 0;
                    file.Elapsed = TimeSpan.Zero;
                    file.Remaining = null;
                    file.ExpectedSize = null;
                    file.CanOpen = false;
                });

                DateTime startTime = DateTime.UtcNow;
                lock (_processingStartTimes)
                {
                    _processingStartTimes[file] = startTime;
                }

                var outputPath = GetOutputPath(file.FullPath);
                var durationSeconds = file.Duration?.TotalSeconds;
                var progress = new Progress<VideoProgress>(report =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        UpdateFileProgress(file, report);
                        RecalcGlobalProgress();
                    }, DispatcherPriority.Background);
                });

                try
                {
                    await Task.Run(
                        () => _reencoder.ReencodeAsync(file.FullPath, outputPath, fileSettings, progress, _cts?.Token ?? CancellationToken.None, durationSeconds),
                        _cts?.Token ?? CancellationToken.None);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        file.Status = "done";
                        file.Progress = 100;
                        file.OutputPath = outputPath;
                        file.CompressedSize = Math.Round(new FileInfo(outputPath).Length / (1024d * 1024d), 2);
                        file.CanOpen = File.Exists(outputPath);
                        file.Remaining = TimeSpan.Zero;
                        RecalcGlobalProgress();
                        UpdateStats();
                    });
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        file.Status = "pending";
                        file.Progress = 0;
                        file.Elapsed = null;
                        file.Remaining = null;
                        file.ExpectedSize = null;
                        RecalcGlobalProgress();
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        file.Status = "error";
                        file.ErrorMessage = ex.Message;
                        file.Progress = 0;
                        file.CanOpen = false;
                        RecalcGlobalProgress();
                    });
                }
                finally
                {
                    lock (_processingStartTimes)
                    {
                        _processingStartTimes.Remove(file);
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        private void RecalcGlobalProgress()
        {
            var files = Files;
            if (files.Count == 0)
            {
                GlobalProgress = 0;
                return;
            }

            double sum = 0;
            foreach (var f in files)
            {
                sum += f.Status switch
                {
                    "done" => 100.0,
                    "processing" => f.Progress,
                    _ => 0.0
                };
            }

            GlobalProgress = Math.Clamp(sum / files.Count, 0, 100);
        }

        [RelayCommand(CanExecute = nameof(CanCancelBatch))]
        private void CancelBatch()
        {
            if (!IsProcessing)
            {
                return;
            }

            _cts?.Cancel();
        }

        private bool CanCancelBatch() => IsProcessing;

        private bool CanStartBatch()
        {
            return !IsProcessing && Files.Any(f => f.Status == "pending");
        }

        [RelayCommand(CanExecute = nameof(CanStartImageCompression))]
        private async Task StartImageCompression()
        {
            if (IsImageProcessing)
            {
                return;
            }

            var pendingImages = ImageFiles.Where(f => string.Equals(f.Status, "Pending", StringComparison.OrdinalIgnoreCase)).ToList();
            if (pendingImages.Count == 0)
            {
                return;
            }

            _imageCts = new CancellationTokenSource();
			var token = _imageCts.Token;
            IsImageProcessing = true;
            ImageGlobalProgress = 0;

            var totalItems = pendingImages.Count;
            var completed = 0;

			foreach (var image in pendingImages)
			{
				if (token.IsCancellationRequested)
				{
					image.Status = "Pending";
					image.NewSizeBytes = null;
					image.CompressedPath = null;
					NotifyImageReplacementState();
					break;
				}

                image.Status = "Processing";
                image.ErrorMessage = null;
				image.CompressedPath = null;
				var outputPath = BuildImageOutputPath(image.FullPath);
			UpdateImageProgressSummary();

                try
                {
					await _compressor.CompressAsync(
						image.FullPath,
						outputPath,
						quality: Math.Clamp(ImageQuality, 1, 100),
						resizeWidth: ResizeImages ? Math.Max(0, ResizeWidth) : 0,
						resizeHeight: ResizeImages ? Math.Max(0, ResizeHeight) : 0);
					token.ThrowIfCancellationRequested();

					var newInfo = new FileInfo(outputPath);
					image.NewSizeBytes = newInfo.Length;
					image.CompressedPath = outputPath;
					image.Status = "Done";
					NotifyImageReplacementState();
				UpdateImagePredictions();
				UpdateImageProgressSummary();
                }
                catch (OperationCanceledException)
                {
                    image.Status = "Pending";
                    image.NewSizeBytes = null;
					image.CompressedPath = null;
					SafeDeleteFile(outputPath);
					NotifyImageReplacementState();
				UpdateImagePredictions();
				UpdateImageProgressSummary();
                    break;
                }
                catch (Exception ex)
                {
                    image.Status = "Error";
                    image.ErrorMessage = ex.Message;
					image.NewSizeBytes = null;
					image.CompressedPath = null;
					SafeDeleteFile(outputPath);
					NotifyImageReplacementState();
				UpdateImagePredictions();
				UpdateImageProgressSummary();
                }

                completed++;
                ImageGlobalProgress = Math.Clamp((double)completed / totalItems * 100.0, 0, 100);
            }

		UpdateImagePredictions();
	UpdateImageProgressSummary();

			_imageCts?.Dispose();
			_imageCts = null;
			IsImageProcessing = false;
			StartImageCompressionCommand.NotifyCanExecuteChanged();
			NotifyImageReplacementState();
        }

		private static void SafeDeleteFile(string? path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return;
			}

			try
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			catch
			{
				// ignore cleanup failures
			}
		}

		private Bitmap? TryLoadVideoThumbnail(string path)
		{
			try
			{
				using var stream = _reencoder.CaptureFrameAsync(path, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
				if (stream == null)
				{
					return null;
				}

				return new Bitmap(stream);
			}
			catch
			{
				return null;
			}
		}

		private Task EnsureImageThumbnailAsync(ImageFile image)
		{
			if (image.Thumbnail != null || string.IsNullOrWhiteSpace(image.FullPath))
			{
				return Task.CompletedTask;
			}

			Task loadTask;
			lock (_thumbnailLoadTasks)
			{
				if (_thumbnailLoadTasks.TryGetValue(image.FullPath, out loadTask!))
				{
					return loadTask;
				}

				loadTask = LoadThumbnailInternalAsync(image);
				_thumbnailLoadTasks[image.FullPath] = loadTask;
			}

			return loadTask;
		}

		private async Task LoadThumbnailInternalAsync(ImageFile image)
		{
			try
			{
				var bitmap = await Task.Run(() =>
				{
					using var stream = File.OpenRead(image.FullPath);
					return Bitmap.DecodeToWidth(stream, 192);
				});

				await Dispatcher.UIThread.InvokeAsync(() =>
				{
					image.Thumbnail = bitmap;
				}, DispatcherPriority.Background);
			}
			catch
			{
				// ignore preview failures
			}
			finally
			{
				lock (_thumbnailLoadTasks)
				{
					_thumbnailLoadTasks.Remove(image.FullPath);
				}
			}
		}

		private void NotifyImageReplacementState()
		{
			ReplaceImageOutputsCommand.NotifyCanExecuteChanged();
			UpdateImageProgressSummary();
		}

		private void UpdateImageProgressSummary()
		{
			var total = ImageFiles.Count;
			var completed = ImageFiles.Count(f => string.Equals(f.Status, "Done", StringComparison.OrdinalIgnoreCase));
			ImageProgressCountText = $"{completed}/{total}";
		}

        [RelayCommand(CanExecute = nameof(CanCancelImageCompression))]
        private void CancelImageCompression()
        {
            if (!IsImageProcessing)
            {
                return;
            }

            _imageCts?.Cancel();
        }

        private bool CanAddImages() => !IsImageProcessing && !IsAddingImages;

        private bool CanStartImageCompression()
        {
            return !IsImageProcessing && ImageFiles.Any(f => string.Equals(f.Status, "Pending", StringComparison.OrdinalIgnoreCase));
        }

        private bool CanCancelImageCompression() => IsImageProcessing;

        public async Task AddFilesFromPathsAsync(IEnumerable<string> paths)
        {
            if (IsProcessing || paths == null || IsAddingFiles)
            {
                return;
            }

            var normalizedPaths = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim('"'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .Where(IsSupportedVideoFile)
                .ToList();

            if (normalizedPaths.Count == 0)
            {
                return;
            }

            IsAddingFiles = true;
            try
            {
                var existingPaths = Files
                    .Select(f => f.FullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var newItems = await Task.Run(() =>
                {
                    var result = new List<VideoFile>();
                    foreach (var path in normalizedPaths)
                    {
                        if (existingPaths.Contains(path))
                        {
                            continue;
                        }

                        VideoMetadata? metadata = null;
                        try
                        {
                            metadata = _reencoder.ProbeInputMetadata(path);
                        }
                        catch
                        {
                            metadata = null;
                        }

                        // 码率过滤：设置了最低码率且文件码率已知且低于阈值时跳过
                        if (_minBitrateFilterKbps > 0
                            && metadata?.BitrateKbps.HasValue == true
                            && metadata.BitrateKbps.Value < _minBitrateFilterKbps)
                        {
                            continue;
                        }

                        try
                        {
                            var info = new FileInfo(path);
                            //var thumbnail = TryLoadVideoThumbnail(path);
                            result.Add(new VideoFile
                            {
                                Name = Path.GetFileName(path),
                                FullPath = path,
                                OriginalSize = Math.Round(info.Length / (1024d * 1024d), 2),
                                Status = "pending",
                                Duration = metadata?.Duration,
                                Metadata = metadata,
                                //Thumbnail = thumbnail
                            });
                        }
                        catch
                        {
                            // Ignore files that can't be accessed
                        }
                    }

                    return result;
                });

                if (newItems.Count == 0)
                {
                    return;
                }

                foreach (var item in newItems)
                {
                    if (Files.Any(f => string.Equals(f.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    item.Index = Files.Count + 1;
                    Files.Add(item);
                }

                ReindexFiles();
                UpdateStats();
            }
            finally
            {
                IsAddingFiles = false;
            }
        }

        public async Task AddImagesFromPathsAsync(IEnumerable<string> paths)
        {
            if (IsImageProcessing || paths == null || IsAddingImages)
            {
                return;
            }

            var normalizedPaths = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim('"'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .Where(IsSupportedImageFile)
                .ToList();

            if (normalizedPaths.Count == 0)
            {
                return;
            }

            IsAddingImages = true;
            try
            {
				var existing = ImageFiles.Select(f => f.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
				var newItems = await Task.Run(() =>
				{
					var items = new List<ImageFile>();
					foreach (var path in normalizedPaths)
					{
						if (existing.Contains(path))
						{
							continue;
						}

					try
					{
						var info = new FileInfo(path);
						items.Add(new ImageFile
						{
							FileName = Path.GetFileName(path),
							FullPath = path,
							OriginalSizeBytes = info.Length,
							Status = "Pending"
						});
					}
						catch
						{
							// ignore files that cannot be accessed
						}
					}

					return items;
				});

				foreach (var item in newItems)
				{
					if (ImageFiles.Any(f => string.Equals(f.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)))
					{
						continue;
					}

					ImageFiles.Add(item);
				}
            }
            finally
            {
                IsAddingImages = false;
                StartImageCompressionCommand.NotifyCanExecuteChanged();
				ReplaceImageOutputsCommand.NotifyCanExecuteChanged();
			UpdateImageProgressSummary();
            }
        }

        private static async Task<string[]?> ShowFilePickerAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
            {
                return null;
            }

            var dialog = new OpenFileDialog
            {
                AllowMultiple = true,
                Filters = new List<FileDialogFilter>
                {
                    new()
                    {
                        Name = "视频文件",
                        Extensions = new List<string>
                        {
                            "mp4", "mov", "mkv", "avi", "wmv", "m4v",
                            "flv", "rmvb", "rm", "mpg", "mpeg", "m2ts",
                            "ts", "webm", "ogv", "3gp", "3g2", "asf", "f4v"
                        }
                    }
                }
            };

            return await dialog.ShowAsync(desktop.MainWindow);
        }

        private static async Task<string[]?> ShowImagePickerAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
            {
                return null;
            }

            var dialog = new OpenFileDialog
            {
                AllowMultiple = true,
                Filters = new List<FileDialogFilter>
                {
                    new()
                    {
                        Name = "图片文件",
                        Extensions = new List<string> { "jpg", "jpeg", "png", "bmp", "webp", "tiff" }
                    }
                }
            };

            return await dialog.ShowAsync(desktop.MainWindow);
        }

        private VideoReencodeSettings BuildSettings()
        {
            var settings = new VideoReencodeSettings
            {
                VideoBitrate = ResolveTargetBitrate(),
                RemoveAudio = false,
                AudioCodec = "aac"
            };

            switch (SelectedEncoderIndex)
            {
                case 0: // H.264 x264
                    settings.UseHardwareAcceleration = false;
                    settings.VideoCodec = "libx264";
                    settings.Preset = "medium";
                    break;
                case 1: // H.265 x265
                    settings.UseHardwareAcceleration = false;
                    settings.VideoCodec = "libx265";
                    settings.Preset = "slow";
                    break;
                case 2: // NVENC H.264
                    if (_hardwareAccelerationAvailable)
                    {
                        settings.UseHardwareAcceleration = true;
                        settings.VideoCodec = "h264_nvenc";
                        settings.Preset = "p5";
                    }
                    else
                    {
                        settings.UseHardwareAcceleration = false;
                        settings.VideoCodec = "libx264";
                        settings.Preset = "medium";
                    }
                    break;
                case 3: // NVENC H.265
                    if (_hardwareAccelerationAvailable)
                    {
                        settings.UseHardwareAcceleration = true;
                        settings.VideoCodec = "hevc_nvenc";
                        settings.Preset = "p5";
                    }
                    else
                    {
                        settings.UseHardwareAcceleration = false;
                        settings.VideoCodec = "libx265";
                        settings.Preset = "slow";
                    }
                    break;
                default:
                    settings.UseHardwareAcceleration = false;
                    settings.VideoCodec = "libx264";
                    settings.Preset = "medium";
                    break;
            }

            settings.TargetWidth = null;
            settings.TargetHeight = null;
            settings.ScaleMultiplier = null;

            switch (SelectedResolutionMode)
            {
                case 1:
                    settings.TargetWidth = 1920;
                    settings.TargetHeight = 1080;
                    break;
                case 2:
                    settings.TargetWidth = 1280;
                    settings.TargetHeight = 720;
                    break;
                case 3:
                    settings.TargetWidth = EnsureEvenDimension(CustomWidth);
                    settings.TargetHeight = EnsureEvenDimension(CustomHeight);
                    break;
                case 4:
                    var clamped = Math.Clamp(ScaleFactor, 0.25, 3.0);
                    settings.ScaleMultiplier = clamped;
                    break;
                default:
                    break;
            }

            return settings;
        }

        private static int? EnsureEvenDimension(int value)
        {
            if (value <= 0)
            {
                return null;
            }

            var normalized = Math.Max(16, value);
            return normalized % 2 == 0 ? normalized : normalized - 1;
        }

        private void UpdateStats()
        {
            var doneFiles = Files.Where(f => f.Status == "done" && f.CompressedSize.HasValue).ToList();
            TotalFilesCount = doneFiles.Count;

            if (doneFiles.Count == 0)
            {
                TotalOriginalSize = "0 MB";
                TotalCompressedSize = "0 MB";
                SavedPercent = "0%";
                StartBatchCommand.NotifyCanExecuteChanged();
                ReplaceOutputsCommand.NotifyCanExecuteChanged();
                UpdateResolutionPreview();
                return;
            }

            double orig = doneFiles.Sum(f => f.OriginalSize);
            double comp = doneFiles.Sum(f => f.CompressedSize ?? 0);

            TotalOriginalSize = $"{orig:F0} MB";
            TotalCompressedSize = $"{comp:F0} MB";
            SavedPercent = orig > 0 ? $"{((orig - comp) / orig * 100):F1}%" : "0%";
            StartBatchCommand.NotifyCanExecuteChanged();
            ReplaceOutputsCommand.NotifyCanExecuteChanged();
            UpdateResolutionPreview();
        }

        private void UpdatePredictedSizes()
        {
            if (Files.Count == 0)
            {
                return;
            }

            var settings = BuildSettings();
            var (fallbackWidth, fallbackHeight) = GetReferenceDimensions();
            foreach (var file in Files)
            {
                if (!string.Equals(file.Status, "pending", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                file.ExpectedSize = EstimateExpectedSizeMb(file, settings, fallbackWidth, fallbackHeight);
            }
        }

		private void UpdateImagePredictions()
		{
			if (ImageFiles.Count == 0)
			{
				return;
			}

			foreach (var image in ImageFiles)
			{
				if (image.NewSizeBytes.HasValue)
				{
					image.PredictedSizeBytes = image.NewSizeBytes;
					continue;
				}

				image.PredictedSizeBytes = EstimateImageOutputSize(image);
			}
		}

		private long? EstimateImageOutputSize(ImageFile image)
		{
			if (image.OriginalSizeBytes <= 0)
			{
				return null;
			}

			double qualityFactor = Math.Clamp(ImageQuality / 100.0, 0.1, 1.0);
			var formatKey = GetEffectiveImageFormatKey(image);
			double formatFactor = formatKey switch
			{
				"PNG" => 0.85,
				"WEBP" => 0.55,
				"AVIF" => 0.45,
				_ => 0.7
			};

			double rawEstimate = image.OriginalSizeBytes * qualityFactor * formatFactor;
			double min = image.OriginalSizeBytes * 0.05;
			double max = image.OriginalSizeBytes * 0.95;
			double clamped = Math.Clamp(rawEstimate, min, max);
			return (long)Math.Max(512, clamped);
		}

        private double? EstimateExpectedSizeMb(VideoFile file, VideoReencodeSettings settings, int? fallbackWidth, int? fallbackHeight)
        {
            var duration = file.Duration ?? file.Metadata?.Duration;
            if (!duration.HasValue || duration.Value.TotalSeconds <= 0)
            {
                return null;
            }

            var bitrateMbps = ParseBitrateToMbps(settings.VideoBitrate)
                ?? (file.Metadata?.BitrateKbps.HasValue == true && file.Metadata.BitrateKbps.Value > 0
                    ? file.Metadata.BitrateKbps.Value / 1000.0
                    : (double?)null)
                ?? TargetBitrateMbps;

            var resolutionScale = ComputeResolutionScale(file, settings, fallbackWidth, fallbackHeight);
            bitrateMbps = Math.Clamp(bitrateMbps * resolutionScale, 0.1, 200.0);

            const double audioBitrateMbps = 0.192; // assume ~192 kbps AAC track
            var totalBitrate = bitrateMbps + audioBitrateMbps;
            var sizeMb = duration.Value.TotalSeconds * totalBitrate / 8.0;
            return Math.Round(sizeMb, 2);
        }

        private double ComputeResolutionScale(VideoFile file, VideoReencodeSettings settings, int? fallbackWidth, int? fallbackHeight)
        {
            var width = file.Metadata?.Width
                        ?? fallbackWidth
                        ?? (settings.TargetWidth.HasValue ? Math.Max(settings.TargetWidth.Value, 16) : 1920);
            var height = file.Metadata?.Height
                         ?? fallbackHeight
                         ?? (settings.TargetHeight.HasValue ? Math.Max(settings.TargetHeight.Value, 16) : 1080);

            if (width <= 0 || height <= 0)
            {
                return 1.0;
            }

            double sourcePixels = (double)width * height;
            double targetWidth = width;
            double targetHeight = height;

            if (settings.TargetWidth.HasValue && settings.TargetHeight.HasValue)
            {
                targetWidth = settings.TargetWidth.Value;
                targetHeight = settings.TargetHeight.Value;
            }
            else if (settings.ScaleMultiplier.HasValue)
            {
                var scale = settings.ScaleMultiplier.Value;
                targetWidth = width * scale;
                targetHeight = height * scale;
            }

            var targetPixels = Math.Max(1.0, targetWidth * targetHeight);
            var factor = targetPixels / sourcePixels;
            return Math.Clamp(factor, 0.1, 2.5);
        }

        private static double? ParseBitrateToMbps(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim();
            var suffix = text[^1];
            double multiplier = 1.0;

            if (char.IsLetter(suffix))
            {
                text = text[..^1];
                switch (char.ToUpperInvariant(suffix))
                {
                    case 'K':
                        multiplier = 0.001;
                        break;
                    case 'M':
                        multiplier = 1.0;
                        break;
                    case 'G':
                        multiplier = 1000.0;
                        break;
                }
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed * multiplier
                : null;
        }

        private void UpdateResolutionPreview()
        {
            var (sourceWidth, sourceHeight) = GetReferenceDimensions();
            ReferenceResolutionText = FormatResolution(sourceWidth, sourceHeight);
            TargetResolutionText = BuildTargetResolutionText(sourceWidth, sourceHeight);
        }

        private (int? width, int? height) GetReferenceDimensions()
        {
            var reference = Files.FirstOrDefault(f => f.Metadata?.Width > 0 && f.Metadata?.Height > 0)?.Metadata;
            if (reference?.Width > 0 && reference.Height > 0)
            {
                return (reference.Width, reference.Height);
            }

            return (null, null);
        }

        private string BuildTargetResolutionText(int? sourceWidth, int? sourceHeight)
        {
            return SelectedResolutionMode switch
            {
                1 => "1920x1080",
                2 => "1280x720",
                3 => FormatResolution(EnsureEvenDimension(CustomWidth), EnsureEvenDimension(CustomHeight)),
                4 => BuildScaleTargetText(sourceWidth, sourceHeight),
                _ => sourceWidth.HasValue && sourceHeight.HasValue
                    ? FormatResolution(sourceWidth, sourceHeight)
                    : "跟随源",
            };
        }

        private string BuildScaleTargetText(int? sourceWidth, int? sourceHeight)
        {
            var factor = Math.Clamp(ScaleFactor, 0.25, 3.0);
            if (sourceWidth.HasValue && sourceHeight.HasValue)
            {
                var targetWidth = EnsureEvenDimension((int)Math.Round(sourceWidth.Value * factor));
                var targetHeight = EnsureEvenDimension((int)Math.Round(sourceHeight.Value * factor));
                var resolution = FormatResolution(targetWidth, targetHeight);
                return $"{factor:0.##}x -> {resolution}";
            }

            return $"{factor:0.##}x";
        }

        private static string FormatResolution(int? width, int? height)
        {
            return width.HasValue && height.HasValue ? $"{width}x{height}" : "--";
        }

        private string? ResolveTargetBitrate()
        {
            if (SelectedBitrateMode == 2)
            {
                return null;
            }

            if (SelectedBitrateMode == 1)
            {
                var source = GetReferenceBitrateKbps();
                if (source.HasValue)
                {
                    return FormatBitrateFromKbps(source.Value * TargetBitrateMultiplier);
                }
            }

            return FormatBitrate(TargetBitrateMbps);
        }

        private double? GetReferenceBitrateKbps()
        {
            var reference = Files.FirstOrDefault(f => f.Metadata?.BitrateKbps > 0)?.Metadata;
            return reference?.BitrateKbps;
        }

        private void StartResourceMonitor()
        {
            _lastCpuTime = _currentProcess.TotalProcessorTime;
            _lastCpuSampleTime = DateTime.UtcNow;
            _resourceCts = new CancellationTokenSource();
            _ = Task.Run(() => ResourceMonitorLoopAsync(_resourceCts.Token));
        }

        private async Task ResourceMonitorLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                double cpuUsage = CalculateCpuUsage();
                double gpuUsage = GetGpuUsage();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CpuUsageText = $"CPU: {cpuUsage:F0}%";
                    GpuUsageText = double.IsNaN(gpuUsage) ? "GPU: --%" : $"GPU: {gpuUsage:F0}%";
                }, DispatcherPriority.Background);

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        partial void OnSelectedBitrateModeChanged(int value)
        {
            OnPropertyChanged(nameof(IsCustomBitrateMode));
            OnPropertyChanged(nameof(IsMultiplierBitrateMode));
            OnPropertyChanged(nameof(IsOriginalBitrateMode));
            OnPropertyChanged(nameof(VideoEncodingSummary));
            UpdatePredictedSizes();
        }

        partial void OnTargetBitrateMbpsChanged(double value)
        {
            OnPropertyChanged(nameof(VideoEncodingSummary));
            UpdatePredictedSizes();
        }

        partial void OnTargetBitrateMultiplierChanged(double value)
        {
            OnPropertyChanged(nameof(VideoEncodingSummary));
            UpdatePredictedSizes();
        }

        partial void OnSelectedEncoderIndexChanged(int value)
        {
            OnPropertyChanged(nameof(VideoEncodingSummary));
            UpdatePredictedSizes();
        }

        partial void OnMinBitrateFilterKbpsChanged(int value)
        {
            OnPropertyChanged(nameof(ImportFilterSummary));
        }

        partial void OnMaxConcurrentEncodesChanged(int value)
        {
            OnPropertyChanged(nameof(ParallelEncodingSummary));
        }

		partial void OnImageQualityChanged(int value) => UpdateImagePredictions();

		partial void OnSelectedImageFormatChanged(string value) => UpdateImagePredictions();

        private static string? FormatBitrateFromKbps(double? kbps)
        {
            if (!kbps.HasValue || kbps.Value <= 0)
            {
                return null;
            }

            var mbps = kbps.Value / 1000.0;
            return FormatBitrate(mbps);
        }

        private static string FormatBitrate(double mbps)
        {
            var clamped = Math.Clamp(mbps, 0.5, 100);
            return clamped.ToString("0.##", CultureInfo.InvariantCulture) + "M";
        }


        private double CalculateCpuUsage()
        {
            var now = DateTime.UtcNow;
            var cpuTime = _currentProcess.TotalProcessorTime;
            var cpuDelta = (cpuTime - _lastCpuTime).TotalSeconds;
            var totalTime = (now - _lastCpuSampleTime).TotalSeconds * Environment.ProcessorCount;
            _lastCpuTime = cpuTime;
            _lastCpuSampleTime = now;
            return totalTime > 0 ? Math.Clamp(cpuDelta / totalTime * 100.0, 0.0, 100.0) : 0.0;
        }

        private double GetGpuUsage()
        {
            if (!OperatingSystem.IsWindows())
            {
                return double.NaN;
            }

            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");

                using var collection = searcher.Get();
                double total = 0;
                foreach (System.Management.ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        var name = obj["Name"] as string;
                        if (string.IsNullOrWhiteSpace(name) ||
                            (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) &&
                             !name.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        var value = obj["UtilizationPercentage"];
                        if (value is uint u)
                        {
                            total += u;
                        }
                        else if (value is ulong ul)
                        {
                            total += ul;
                        }
                        else if (value is string s && double.TryParse(s, out var parsed))
                        {
                            total += parsed;
                        }
                    }
                }

                return Math.Clamp(total, 0.0, 100.0);
            }
            catch
            {
                return double.NaN;
            }
        }

        private void UpdateFileProgress(VideoFile file, VideoProgress report)
        {
            double? percent = report.Percent;
            if (!percent.HasValue && report.ProcessedTime.HasValue && file.Duration.HasValue && file.Duration.Value.TotalSeconds > 0)
            {
                percent = Math.Min(100, report.ProcessedTime.Value.TotalSeconds / file.Duration.Value.TotalSeconds * 100.0);
            }

            if (percent.HasValue)
            {
                file.Progress = percent.Value;
            }

            // 映射实际耗时/剩余时间（基于墙钟时间）
            DateTime? startedAt;
            lock (_processingStartTimes)
            {
                _processingStartTimes.TryGetValue(file, out var raw);
                startedAt = raw;
            }

            if (startedAt.HasValue)
            {
                var elapsedReal = DateTime.UtcNow - startedAt.Value;
                if (elapsedReal < TimeSpan.Zero)
                {
                    elapsedReal = TimeSpan.Zero;
                }

                file.Elapsed = elapsedReal;

                if (percent.HasValue && percent.Value > 0 && percent.Value < 100)
                {
                    var remainingSeconds = elapsedReal.TotalSeconds * (100 - percent.Value) / percent.Value;
                    if (remainingSeconds < 0)
                    {
                        remainingSeconds = 0;
                    }
                    file.Remaining = TimeSpan.FromSeconds(remainingSeconds);
                }
                else if (percent.HasValue && percent.Value >= 100)
                {
                    file.Remaining = TimeSpan.Zero;
                }
                else
                {
                    file.Remaining = null;
                }
            }
            else
            {
                file.Elapsed = report.ProcessedTime;
                file.Remaining = report.EstimatedRemaining;
            }

            // 预期大小（MB） = 已处理字节 / (percent/100)
            if (report.ProcessedBytes.HasValue && percent.HasValue && percent.Value > 0)
            {
                var expectedBytes = report.ProcessedBytes.Value / (percent.Value / 100.0);
                file.ExpectedSize = Math.Round(expectedBytes / (1024d * 1024d), 2);
            }
        }

        private void UpdateGlobalProgress(int completed, int totalItems, double currentFilePercent)
        {
            if (totalItems == 0)
            {
                GlobalProgress = 0;
                return;
            }

            var normalized = ((double)completed + currentFilePercent / 100d) / totalItems * 100d;
            GlobalProgress = Math.Clamp(normalized, 0, 100);
        }

		private void ApplyVideoSort(string? columnKey)
		{
			if (string.IsNullOrWhiteSpace(columnKey) || Files.Count <= 1)
			{
				return;
			}

			var key = columnKey.Trim().ToLowerInvariant();
			if (string.Equals(_videoSortKey, key, StringComparison.Ordinal))
			{
				_videoSortAscending = !_videoSortAscending;
			}
			else
			{
				_videoSortKey = key;
				_videoSortAscending = true;
			}

			Func<VideoFile, object?> selector = key switch
			{
				"name" => f => f.Name,
				"size" => f => f.OriginalSize,
				"bitrate" => f => f.Metadata?.BitrateKbps ?? -1,
				"resolution" => f => (f.Metadata?.Width ?? 0) * (f.Metadata?.Height ?? 0),
				"elapsed" => f => f.Elapsed?.TotalSeconds ?? double.MinValue,
				"remaining" => f => f.Remaining?.TotalSeconds ?? double.MaxValue,
				"progress" => f => f.Progress,
				"expected" => f => f.ExpectedSize ?? double.MaxValue,
				_ => f => f.Index
			};

			var comparer = Comparer<object?>.Create((a, b) =>
			{
				if (a is IComparable ca && b is not null)
				{
					return ca.CompareTo(b);
				}
				if (a == null && b == null)
				{
					return 0;
				}
				if (a == null)
				{
					return -1;
				}
				if (b == null)
				{
					return 1;
				}
				return StringComparer.OrdinalIgnoreCase.Compare(a.ToString(), b.ToString());
			});

			var sorted = (_videoSortAscending
				? Files.OrderBy(selector, comparer)
				: Files.OrderByDescending(selector, comparer)).ToList();

			Files.Clear();
			foreach (var file in sorted)
			{
				Files.Add(file);
			}

			ReindexFiles();
		}

		private void ApplyImageSort(string? columnKey)
		{
			if (string.IsNullOrWhiteSpace(columnKey) || ImageFiles.Count <= 1)
			{
				return;
			}

			var key = columnKey.Trim().ToLowerInvariant();
			if (string.Equals(_imageSortKey, key, StringComparison.Ordinal))
			{
				_imageSortAscending = !_imageSortAscending;
			}
			else
			{
				_imageSortKey = key;
				_imageSortAscending = true;
			}

			double GetReduction(ImageFile file)
			{
				var target = file.NewSizeBytes ?? file.PredictedSizeBytes;
				if (target.HasValue && file.OriginalSizeBytes > 0)
				{
					return 1.0 - (double)target.Value / file.OriginalSizeBytes;
				}
				return double.MinValue;
			}

			Func<ImageFile, object?> selector = key switch
			{
              "index" => f => f.Index,
				"name" => f => f.FileName,
				"original" => f => f.OriginalSizeBytes,
				"compressed" => f => f.NewSizeBytes ?? f.PredictedSizeBytes ?? long.MaxValue,
				"reduction" => f => GetReduction(f),
				"status" => f => f.Status,
             _ => f => f.Index
			};

			var comparer = Comparer<object?>.Create((a, b) =>
			{
				if (a is IComparable ca && b is not null)
				{
					return ca.CompareTo(b);
				}
				if (a == null && b == null)
				{
					return 0;
				}
				if (a == null)
				{
					return -1;
				}
				if (b == null)
				{
					return 1;
				}
				return StringComparer.OrdinalIgnoreCase.Compare(a.ToString(), b.ToString());
			});

			var sorted = (_imageSortAscending
				? ImageFiles.OrderBy(selector, comparer)
				: ImageFiles.OrderByDescending(selector, comparer)).ToList();

			ImageFiles.Clear();
			foreach (var file in sorted)
			{
				ImageFiles.Add(file);
			}

            ReindexImages();
		}

        private void ReindexImages()
        {
            for (int i = 0; i < ImageFiles.Count; i++)
            {
                ImageFiles[i].Index = i + 1;
            }
        }

        private void ReindexFiles()
        {
            for (int i = 0; i < Files.Count; i++)
            {
                Files[i].Index = i + 1;
            }
        }

        private void AddMockFiles()
        {
            var rnd = new Random();
            string[] names = { "Camera_Shot_A.mp4", "Drone_View_City.mov", "Interview_Main.mkv", "B-Roll_Nature.mp4" };

            for (int i = 0; i < 3; i++)
            {
                var name = names[rnd.Next(names.Length)];
                Files.Add(new VideoFile
                {
                    Index = Files.Count + 1,
                    Name = $"{Files.Count + 1}_{name}",
                    FullPath = Path.Combine("C:/Videos", name),
                    OriginalSize = rnd.Next(50, 550),
                    Status = "pending",
                    Duration = TimeSpan.FromMinutes(rnd.Next(1, 5))
                });
            }

            UpdateStats();
        }

        [RelayCommand]
        private void OpenOutputPath(VideoFile file)
        {
            if (file == null)
            {
                return;
            }

            var targetPath = !string.IsNullOrWhiteSpace(file.OutputPath) && File.Exists(file.OutputPath)
                ? file.OutputPath
                : file.FullPath;

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            try
            {
                if (OperatingSystem.IsWindows() && File.Exists(targetPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer",
                        Arguments = $"/select,\"{targetPath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = directory,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // ignore
            }
        }

        private static void DeleteToRecycleBin(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    FileSystem.DeleteFile(
                        path,
                        UIOption.OnlyErrorDialogs,
                        RecycleOption.SendToRecycleBin);
                }
                else
                {
                    // Non-Windows platforms: no recycle bin API; do a normal delete
                    File.Delete(path);
                }
            }
            catch
            {
                // If recycle bin operation fails, fallback to permanent delete to avoid leaving temp files
                try { File.Delete(path); } catch { }
            }
        }
    }
}
