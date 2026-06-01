# ⚡ ProBatch Compressor — UltraCompress

一款跨平台桌面批量压缩工具，支持 **视频** 和 **图片** 的高效压缩。

![Avalonia](https://img.shields.io/badge/Avalonia-11.3.9-blueviolet)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)

---

## 📸 截图

> 自定义暗色主题 · 双标签切换 · 实时进度跟踪

![screenshot](Assets/avalonia-logo.ico)

---

## ✨ 功能特性

### 🎬 视频压缩
- **支持格式** — MP4、MOV、MKV、AVI、WMV、FLV、WebM 等 18+ 种格式
- **多种编码器**
  - `x264` / `x265`（软件编码）
  - `NVENC H.264` / `NVENC H.265`（NVIDIA GPU 硬件加速）
- **码率控制**
  - 自定义目标码率（Mbps）
  - 原始码率倍数（0.25× ~ 3×）
  - 保持原始码率
- **分辨率控制**
  - 预设：1080p / 720p
  - 自定义宽高
  - 缩放倍数
- **CRF 质量控制**（0~51）
- **智能降级策略**：全 GPU → NVENC + CPU 滤镜 → 纯软件，自动容错
- **并行编码**：支持多路并发（默认 2 路）

### 🖼 图片压缩
- **支持格式** — JPG、PNG、BMP、WebP、TIFF
- **品质控制**（1~100）
- **元数据剥离**（`Strip()`）减小体积
- **PNG 有损量化**（类似 TinyPNG，256色 + 抖动）
- **BMP 压缩**（256色 RLE 编码）
- **GIF 优化**（128色 Floyd-Steinberg 抖动）
- **缩放**：自定义宽高或等比缩放
- **格式转换**：同源 / JPG / PNG / WebP

### 🖥 UI
- 全自定义暗色主题（Slate / Teal / Blue 色系）
- AcrylicBlur 毛玻璃效果
- 自定义标题栏（拖拽移动 / 最小化 / 最大化 / 关闭）
- 拖放导入文件（支持文件夹递归）
- 可排序列的表格视图
- 实时进度条 + 剩余时间估算
- 状态栏显示 CPU / GPU 占用率

---

## 🛠 技术栈

| 组件 | 技术 |
|---|---|
| UI 框架 | Avalonia 11.3.9 |
| 语言 | C# / .NET 8 |
| MVVM | CommunityToolkit.Mvvm 8.2.1 |
| 视频编码 | FFmpeg (via `FFmpeg.AutoGen`) |
| 图片处理 | ImageMagick (`Magick.NET-Q8-AnyCPU`) |
| 硬件加速 | NVIDIA NVENC + CUDA |
| 系统监控 | `System.Management` (WMI) |

---

## 🚀 快速开始

### 前置要求
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [FFmpeg](https://ffmpeg.org/download.html)（将 `ffmpeg.exe` 放入输出目录）
- NVIDIA GPU（可选，用于硬件加速）

### 构建运行

```bash
# 克隆仓库
git clone https://github.com/mafawu/UltraCompress.git
cd UltraCompress

# 还原依赖
dotnet restore

# 运行
dotnet run --project UltraCompress

# 发布
dotnet publish -c Release -o ./publish
```

### FFmpeg 配置

将 `ffmpeg.exe` 和 `ffprobe.exe` 放在程序运行目录下（与 `UltraCompress.exe` 同级）。

---

## 📁 项目结构

```
UltraCompress/
├── App.axaml / .cs          # 应用入口
├── Program.cs                # 启动代码
├── ViewLocator.cs            # ViewModel → View 解析器
├── Models/
│   ├── ImageFile.cs          # 图片模型
│   └── VideoFile.cs          # 视频模型
├── ViewModels/
│   ├── ViewModelBase.cs      # MVVM 基类
│   └── MainWindowViewModel.cs # 主窗口逻辑（~2300行）
├── Views/
│   ├── MainWindow.axaml      # UI 布局（暗色主题）
│   └── MainWindow.axaml.cs   # 拖放 / 窗口控制
├── Services/
│   ├── ImageCompressor.cs    # 图片压缩引擎
│   ├── VideoReencoder.cs     # 视频转码引擎
│   ├── VideoMetadata.cs      # 视频元数据 DTO
│   └── VideoProgress.cs      # 进度信息 DTO
├── FFmpegNVENCTranscoder.cs  # NVENC 独立封装（备用）
└── UltraCompress.csproj      # 项目文件
```

---

## 📝 使用说明

1. **启动程序** — 默认进入视频压缩标签
2. **添加文件** — 点击「添加文件」按钮或直接拖拽文件/文件夹到界面
3. **配置参数** — 在右侧面板选择编码器、码率、分辨率等
4. **开始压缩** — 点击「开始压缩」，实时查看进度
5. **替换原片** — 压缩完成后可一键替换源文件
6. **图片压缩** — 切换到「图片压缩」标签，操作流程类似

---

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

---

## 📄 许可证

[MIT License](LICENSE)
