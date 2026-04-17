# ChewCombine – osu!段位谱合并

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**ChewCombine** 是一个专为 osu! 4K 段位吃和谱师设计的命令行工具，能够将多个独立的谱面自动合并成一个完整的段位谱。  
自动处理音频裁剪，淡入淡出，休息段插入，并最终输出可直接导入 osu! 的 `.osz` 包。

## ✨ 功能特性

- 按顺序合并任意数量谱面
- 为每个谱面独立指定裁剪起止时间
- 自动为每个音频片段添加 **1 秒淡入 + 1 秒淡出**
- 可自动插入一个自定义**休息段**
- 合并后自动打包为 `.osz` 文件，包含音频、谱面文件和背景图

## 📦 下载与依赖

### 依赖
- **[FFmpeg](https://ffmpeg.org/download.html)**（必需）  
  下载静态编译的 `ffmpeg.exe`（例如从 [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) 获取），并放置于：
  - 程序同目录下，或
  - 程序同目录下的 `ffmpeg\` 子文件夹中

### 运行时
- 需要 **.NET 8.0 运行时**（如未安装，可从 [Microsoft 官网](https://dotnet.microsoft.com/en-us/download) 下载）

## 🚀 快速开始

### 1. 准备文件结构
在程序根目录下创建以下文件夹：
```shell
程序根目录/
├── songs/
│ ├── 1/ 
│ ├── 2/
│ │ └── ...
│ └── 3/
├── relax/
│ └── rest.mp3
├── img/
│ └── bg.png
└── Create/
```

你有多少个谱面需要处理，就在 `songs` 目录下建立多少个文件夹，请文件夹以"1","2"..."i"等格式命名

对于每个`songs\i\`，请至少包含一个音频文件和.osu文件

### 2. 运行程序
- 双击 `ChewCombine.exe` 启动
- 按提示依次输入每个谱面的裁剪起止时间（格式 `00:00:000 03:30:681`）
- 程序自动处理音频、合并谱面、打包 `.osz`
- 合成好的谱面将放在 `Create/` 文件夹下，文件名为 `1.osz`、`2.osz` …

---

**Happy mapping & gaming!** 🎵
