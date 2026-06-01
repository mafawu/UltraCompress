using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PathShape = Avalonia.Controls.Shapes.Path;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UltraCompress.Services;
using UltraCompress.ViewModels;

namespace UltraCompress.Views
{
    public partial class MainWindow : Window
    {
        private PathShape? _maxRestoreIcon;
        private Border? _videoDropSurface;
		private Border? _imageDropSurface;
		private IBrush? _videoDefaultBackground;
		private IBrush? _imageDefaultBackground;
		private IBrush? _videoDefaultBorderBrush;
		private IBrush? _imageDefaultBorderBrush;
		private Thickness _videoDefaultBorderThickness;
		private Thickness _imageDefaultBorderThickness;
		private static readonly IBrush DropHintBackground = new SolidColorBrush(Color.Parse("#1223344A"));
		private static readonly IBrush DropHintBorderBrush = new SolidColorBrush(Color.Parse("#335B86B7"));
        private static readonly Geometry MaximizeGeometry = Geometry.Parse("M3,3 H13 V13 H3 Z");
        private static readonly Geometry RestoreGeometry = Geometry.Parse("M4,6 H12 V14 H4 Z M6,4 H14 V12 H6 Z");
        private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;
        
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowViewModel();
            _maxRestoreIcon = this.FindControl<PathShape>("MaxRestoreIcon");
            _videoDropSurface = this.FindControl<Border>("VideoDropSurface");
			_imageDropSurface = this.FindControl<Border>("ImageDropSurface");

			_videoDefaultBackground = _videoDropSurface?.Background;
			_imageDefaultBackground = _imageDropSurface?.Background;
			_videoDefaultBorderBrush = _videoDropSurface?.BorderBrush;
			_imageDefaultBorderBrush = _imageDropSurface?.BorderBrush;
			_videoDefaultBorderThickness = _videoDropSurface?.BorderThickness ?? default;
			_imageDefaultBorderThickness = _imageDropSurface?.BorderThickness ?? default;

            PropertyChanged += OnWindowPropertyChanged;
            UpdateMaxRestoreIcon();

            //string input = @"D:\1.jpg";
            //string output = @"D:\1_compressed.jpg";
            //// 1. 获取原图大小 (用于对比)
            //long oldSize = new FileInfo(input).Length;

            // 2. 开始压缩 (异步等待，不会卡界面)
            //_compressor.CompressAsync(input, output, quality: 75);

            // 3. 获取新图大小
            //long newSize = new FileInfo(output).Length;

            //// 4. 打印或显示结果
            //Console.WriteLine($"压缩完成！");
            //Console.WriteLine($"原体积: {FormatSize(oldSize)}");
            //Console.WriteLine($"新体积: {FormatSize(newSize)}");
            //Console.WriteLine($"节省了: {((oldSize - newSize) / (double)oldSize):P2}");
        }

        // 辅助方法：把字节转成 KB/MB
        private string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
            return $"{bytes / 1024.0 / 1024.0:F2} MB";
        }

		private void OnFilesDragOver(object? sender, DragEventArgs e)
        {
          var files = GetRawDraggedPaths(e.Data);

			if (ViewModel == null)
			{
				e.DragEffects = DragDropEffects.None;
				SetDropFeedback(false);
				e.Handled = true;
				return;
			}

			// 即使拖放数据中读不到文件名，也放行让 Drop 事件处理
			// （某些 Windows/Avalonia 版本下 DragOver 阶段数据不可用是已知问题）
			if (files == null || files.Length == 0)
			{
				e.DragEffects = DragDropEffects.Copy;
				e.Handled = true;
				return;
			}

			var hasDirectories = files.Any(Directory.Exists);
			var existingFiles = files.Where(File.Exists).ToArray();
			var allow = false;

			if (ViewModel.IsImageView)
			{
				var hasImages = existingFiles.Any(MainWindowViewModel.IsSupportedImageFile);
             allow = (hasImages || hasDirectories) && !ViewModel.IsImageProcessing && !ViewModel.IsAddingImages;
				e.DragEffects = allow ? DragDropEffects.Copy : DragDropEffects.None;
			}
			else
			{
				var hasVideos = existingFiles.Any(MainWindowViewModel.IsSupportedVideoFile);
               allow = (hasVideos || hasDirectories) && !ViewModel.IsProcessing && !ViewModel.IsAddingFiles;
				e.DragEffects = allow ? DragDropEffects.Copy : DragDropEffects.None;
			}

			SetDropFeedback(allow);

			e.Handled = true;
        }

		private void OnFilesDragLeave(object? sender, RoutedEventArgs e)
		{
			SetDropFeedback(false);
		}

        private async void OnFilesDrop(object? sender, DragEventArgs e)
        {
            e.Handled = true;

			if (ViewModel == null)
            {
             SetDropFeedback(false);
                return;
            }

            try
			{
				var resolvedFiles = ResolveDroppedFiles(GetRawDraggedPaths(e.Data));
				if (resolvedFiles.Count == 0)
				{
					return;
				}

				if (ViewModel.IsImageView)
				{
					if (ViewModel.IsImageProcessing || ViewModel.IsAddingImages)
					{
						return;
					}
					var images = resolvedFiles.Where(MainWindowViewModel.IsSupportedImageFile).ToArray();
					if (images.Length == 0)
					{
						return;
					}
					await ViewModel.AddImagesFromPathsAsync(images);
				}
				else
				{
					if (ViewModel.IsProcessing || ViewModel.IsAddingFiles)
					{
						return;
					}
					var videos = resolvedFiles.Where(MainWindowViewModel.IsSupportedVideoFile).ToArray();
					if (videos.Length == 0)
					{
						return;
					}
					await ViewModel.AddFilesFromPathsAsync(videos);
				}
			}
			finally
			{
				SetDropFeedback(false);
			}
        }

		private void SetDropFeedback(bool allowDrop)
		{
			var imageView = ViewModel?.IsImageView == true;
			ApplyDropFeedback(_videoDropSurface, _videoDefaultBackground, _videoDefaultBorderBrush, _videoDefaultBorderThickness, allowDrop && !imageView);
			ApplyDropFeedback(_imageDropSurface, _imageDefaultBackground, _imageDefaultBorderBrush, _imageDefaultBorderThickness, allowDrop && imageView);
		}

		private static void ApplyDropFeedback(
			Border? surface,
			IBrush? defaultBackground,
			IBrush? defaultBorderBrush,
			Thickness defaultBorderThickness,
			bool active)
		{
			if (surface == null)
			{
				return;
			}

			surface.Background = active ? DropHintBackground : defaultBackground;
			surface.BorderBrush = active ? DropHintBorderBrush : defaultBorderBrush;
			surface.BorderThickness = active ? new Thickness(1.5) : defaultBorderThickness;
		}

        private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private void OnMinimizeClick(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }

        private void UpdateMaxRestoreIcon()
        {
            if (_maxRestoreIcon == null)
            {
                return;
            }

			_maxRestoreIcon.Data = WindowState == WindowState.Maximized ? RestoreGeometry : MaximizeGeometry;
        }

		private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
		{
			if (e.Property == Window.WindowStateProperty)
			{
				UpdateMaxRestoreIcon();
			}
		}

		protected override void OnClosed(EventArgs e)
		{
			base.OnClosed(e);
			PropertyChanged -= OnWindowPropertyChanged;
		}

		private static IReadOnlyList<string> ResolveDroppedFiles(IEnumerable<string>? rawItems)
		{
			if (rawItems == null)
			{
				return Array.Empty<string>();
			}

			var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var item in rawItems)
			{
				if (string.IsNullOrWhiteSpace(item))
				{
					continue;
				}

				var normalized = item.Trim('"');
				if (File.Exists(normalized))
				{
					results.Add(normalized);
					continue;
				}

				if (!Directory.Exists(normalized))
				{
					continue;
				}

				try
				{
					foreach (var file in Directory.EnumerateFiles(normalized, "*.*", SearchOption.AllDirectories))
					{
						if (File.Exists(file))
						{
							results.Add(file);
						}
					}
				}
				catch
				{
					// 忽略无法访问的目录
				}
			}

			return results.ToArray();
		}

		private static string[] GetRawDraggedPaths(IDataObject data)
		{
			var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			// 链路 A: 从 FileDrop 格式提取（资源管理器标准格式）
			var fileNames = data.GetFileNames();
			if (fileNames != null)
			{
				foreach (var name in fileNames)
				{
					if (!string.IsNullOrWhiteSpace(name))
					{
						// 清理路径：去引号、去不可见字符、正规化
						var clean = name.Trim().Trim('"').TrimEnd('\0', '\r', '\n', ' ');
						if (!string.IsNullOrWhiteSpace(clean))
						{
							results.Add(clean);
						}
					}
				}
			}

			// 链路 B: 从文本格式提取（Everything 等工具会提供）
			var text = data.GetText();
			if (!string.IsNullOrWhiteSpace(text))
			{
				foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
				{
					var trimmed = line.Trim();
					if (string.IsNullOrWhiteSpace(trimmed))
					{
						continue;
					}

					if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.IsFile)
					{
						results.Add(uri.LocalPath);
					}
					else
					{
						var clean = trimmed.Trim('"').TrimEnd('\0', '\r', '\n', ' ');
						if (!string.IsNullOrWhiteSpace(clean))
						{
							results.Add(clean);
						}
					}
				}
			}

			// 最后兜底：如果文件路径为空但文件名不为空，尝试用文件名在当前目录查找
			// （防止某种数据格式下路径字段缺失）

			return results.ToArray();
		}
    }
}