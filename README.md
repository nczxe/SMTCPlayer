# SMTC Player

Windows 媒体远程控制器 —— 通过局域网用手机浏览器控制电脑上的音乐播放，支持系统 SMTC 和网易云音乐增强。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Python 3.8+](https://img.shields.io/badge/Python-3.8+-green.svg)](https://www.python.org/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-lightgrey.svg)]()
[![Version](https://img.shields.io/badge/Version-1.1.1-blue.svg)]() [![Build](https://img.shields.io/badge/Build-26082323500S-blue.svg)]()

**官网：[splay.asia](https://splay.asia)**

---

## 三种版本

| 版本 | 技术栈 | 适用场景 |
|------|--------|----------|
| **WinUI** | .NET 10 + Windows App SDK 1.8 | Windows 11 原生体验（Mica 背景、Acrylic 面板） |
| **WPF** | .NET 10 + WPF | Windows 10/11 兼容性好，经典桌面 UI |
| **Python** | Python + Tkinter | 轻量级，跨 Python 版本运行 |

三种版本共享同一个 Python 服务端，网易云增强功能由 NeteaseEnhance 插件托管。

---

## 功能

### 媒体控制
- 播放 / 暂停、上一首、下一首
- 歌曲标题、艺术家、专辑封面实时显示
- 播放进度与总时长
- 系统音量控制与静音切换

### 网易云音乐增强
- 精确播放进度（由网易云增强插件托管的原创 `NeteaseWatcher` 监视器获取，比系统 SMTC 更精准；自动扫描所有网易云子进程，主窗口关闭后依然可用）
- 播放进度偏移校准（设置中手动微调 ±5 秒，修正进度显示偏差，仅 WinUI 版）
- 高清专辑封面
- 歌曲搜索、用户歌单浏览
- 网页拉起播放（`MUSIC_U` Cookie，Windows DPAPI 加密存储）
- Cookie 清除功能

### 原生 UI（WinUI / WPF）
- 手机扫码连接（二维码实时生成）
- 横竖屏自适应布局
- 深色 / 浅色 / 跟随系统主题切换
- 设置持久化（主题、调试模式等保存到本地）
- 系统托盘图标（右键菜单：显示窗口、退出）
- 日志系统（Debug / Info / Warn / Error 四级，自动轮转和清理）
- 调试控制台（实时查看日志输出）
- PIN 码管理（首次设置 / 修改）

### 移动端 Web
- 浏览器直接访问，支持 PWA（添加到主屏幕）
- 深色主题、玻璃拟态 UI、响应式布局
- PIN 码认证 + Token 鉴权

### 插件系统
- 基于 .NET AssemblyLoadContext 的可收集插件加载
- 插件契约接口：IPlugin、ISearchProvider、IPlaybackController
- 事件广播机制：SongChanged、PlaybackStateChanged、VolumeChanged、ServerStateChanged
- 插件隔离设置存储和日志系统
- 支持热更新和动态加载/卸载
- 内置插件：网易云增强、Spotify 支持

---

## 快速开始

### 方式一：安装版（推荐）

下载对应安装包，双击安装即可运行，无需任何依赖：

| 安装包 | 大小 | 说明 |
|--------|------|------|
| `SMTCPlayer_WinUI_Setup_v1.1.1.exe` | ~69 MB | WinUI 版（SelfContained，含 .NET 运行时） |
| `SMTCPlayer_WPF_Setup_v1.1.1.exe` | ~74 MB | WPF 版（SelfContained，含 .NET 运行时） |

### 方式二：Python 版

```bat
cd server
pip install -r requirements.txt
python main.py
```

或无 GUI 模式：

```bat
python server\main.py --no-gui
```

### 使用方法

1. 启动应用后点击「启动服务」
2. 手机扫描二维码或访问显示的局域网地址（如 `http://192.168.1.100:8888`）
3. 首次访问网页时设置 4-16 位 PIN 码
4. 网易云客户端中需要开启SMTC与自动唤起客户端功能

---

## 命令行参数（Python 版）

| 参数 | 说明 |
|------|------|
| `--port <PORT>` | 指定 HTTP 端口（默认: 8888） |
| `--save-port` | 将端口保存到 config.json |
| `--no-gui` | 无 GUI 模式（仅 Flask 服务） |
| `--set-pin <PIN>` | 设置 / 重置 PIN（4-16 位） |

端口优先级：`--port` > `SMTC_PORT` 环境变量 > `config.json` > `8888`

---

## 安全机制

### PIN 认证
- 4-16 位，可含字母、数字和 `!@#$%^&*()_-+=[]{}:;,.?/|~`
- PBKDF2-SHA256（200,000 次迭代）哈希存储
- HMAC 恒定时间比较，防止时序攻击
- 首次访问强制设置，后续用 PIN 换取 session token

### Cookie 加密
- 网易云 Cookie 使用 Windows DPAPI 加密
- 加密存储在 `%ProgramData%\SMTCPlayer\.ncm_cache\`
- 仅当前 Windows 用户可解密

---

## API 端点

所有 `/api/*` 端点需要 `X-SMTC-Token` 认证（auth 路由和本地回环除外）。

### 媒体控制

| 方法 | 路径 | 说明 |
|------|------|------|
| `GET` | `/api/status` | 当前播放状态 |
| `POST` | `/api/play_pause` | 播放 / 暂停 |
| `POST` | `/api/next` | 下一首 |
| `POST` | `/api/previous` | 上一首 |
| `POST` | `/api/volume` | 设置音量 `{"volume": 0-100}` |
| `POST` | `/api/volume/toggle_mute` | 切换静音 |

### 认证

| 方法 | 路径 | 说明 |
|------|------|------|
| `GET` | `/api/auth/status` | PIN 是否已设置 |
| `POST` | `/api/auth/setup` | 首次设置 PIN |
| `POST` | `/api/auth/login` | PIN 登录，返回 token |
| `POST` | `/api/auth/reset_pin` | 重置 PIN（无需旧 PIN） |

### 网易云音乐

| 方法 | 路径 | 说明 |
|------|------|------|
| `POST` | `/api/ncm/login_cookie` | Cookie 登录 |
| `POST` | `/api/ncm/clear_cookies` | 清除 Cookie |
| `GET` | `/api/ncm/search?keyword=xxx` | 搜索歌曲 |
| `GET` | `/api/ncm/user/playlist` | 用户歌单 |
| `GET` | `/api/ncm/playlist/detail?id=xxx` | 歌单详情 |
| `GET` | `/api/ncm/song/detail?id=xxx` | 歌曲详情 |
| `POST` | `/api/ncm/open_web` | 网页打开歌曲 |

### 诊断

| 方法 | 路径 | 说明 |
|------|------|------|
| `GET` | `/api/health` | 服务状态诊断 |

---

## 项目结构

```
NCM-SMTCPlayer/
├── smtc-ui/                           # .NET UI 解决方案
│   ├── SMTCPlayer.sln                 # 解决方案（6 个项目）
│   ├── SMTCPlayer.PluginApi/          # 插件契约程序集（零依赖）
│   │   ├── IPlugin.cs                 #   插件主接口
│   │   ├── IPluginContext.cs          #   插件上下文接口
│   │   ├── ISearchProvider.cs         #   搜索提供者接口
│   │   └── PluginEvent.cs             #   事件类型定义
│   ├── SMTCPlayer.Core/               # 核心类库（共享）
│   │   ├── Models/                    #   数据模型
│   │   ├── Services/                  #   SmtcApiClient, FlaskServerManager, Logger
│   │   ├── ViewModels/               #   MainViewModel（MVVM）
│   │   └── Plugins/                  #   插件宿主框架
│   │       ├── PluginHost.cs          #     插件发现/加载/事件广播
│   │       ├── PluginRegistry.cs      #     启用状态注册表
│   │       └── PluginEventDispatcher.cs #   事件 diff 引擎
│   ├── SMTCPlayer.WinUI/             # WinUI3 原生 UI
│   │   ├── Dialogs/                   #   PinSetup、PositionOffset 校准对话框
│   │   ├── Services/                  #   AppSettings 持久化
│   │   ├── SettingsPanel.xaml         #   全窗口设置面板
│   │   ├── MainWindow.xaml            #   主窗口（Mica + Acrylic）
│   │   └── App.xaml                   #   应用入口
│   └── SMTCPlayer.Wpf/              # WPF UI
│       ├── Themes/                    #   深色/浅色主题
│       ├── MainWindow.xaml            #   主窗口
│       └── SettingsWindow.xaml        #   设置窗口
├── plugins/                           # 插件源码（编译后部署到 smtc-ui\plugins\）
│   ├── SMTCPlayer.Plugins.NeteaseEnhance/   # 网易云增强插件（托管 NeteaseWatcher）
│   └── SMTCPlayer.Plugins.SpotifySupport/   # Spotify 支持插件
├── server/                            # Python 服务端
│   ├── main.py                        # 入口：参数解析、Flask、GUI
│   ├── app.py                         # Flask API + 静态文件托管
│   ├── smtc_controller.py             # Windows SMTC API 封装
│   ├── netease_watcher_client.py      # NeteaseWatcher HTTP 客户端
│   ├── netease_watcher_server.py      # 原创网易云状态监视器（行为启发式内存扫描）
│   ├── volume_controller.py           # 系统音量控制（pycaw）
│   ├── ncm_music_api.py               # 网易云音乐 API（WEAPI 加密）
│   ├── security.py                    # PIN / Token / DPAPI 加密
│   ├── gui.py                         # Tkinter 桌面 GUI
│   └── static/                        # 移动端 Web 前端
│       ├── index.html                  #   主页面
│       ├── player.js                  #   播放器逻辑
│       ├── auth.js                    #   认证流程
│       └── style.css                  #   深色主题样式
├── tests/
│   └── test_security.py               # 安全模块单元测试
├── build.bat                          # 构建脚本（Python + .NET + 安装包）
├── release.bat                        # 发布脚本（测试 + 构建 + 打包）
├── setup.iss                          # Inno Setup - WPF 安装包脚本
├── setup-winui.iss                    # Inno Setup - WinUI 安装包脚本
├── SMTCPlayer.spec                    # PyInstaller 构建配置
└── LICENSE                            # MIT 许可证
```

---

## 架构

```
┌─────────────────────────────────────────────────────────────┐
│              手机浏览器 / PWA                                │
│        http://<PC-IP>:8888  (局域网 HTTP)                   │
└────────────────────────┬────────────────────────────────────┘
                         │  REST API (JSON + Token Auth)
                         ▼
┌─────────────────────────────────────────────────────────────┐
│                 Flask Server (app.py)                        │
│  ┌──────────────┐ ┌───────────────┐ ┌────────────────────┐  │
│  │ security.py  │ │ ncm_music_    │ │ netease_watcher_   │  │
│  │ PIN/Token    │ │ api.py        │ │ client.py          │  │
│  │ DPAPI 加密    │ │ 网易云 API     │ │ → localhost:3574   │  │
│  └──────────────┘ └───────────────┘ └─────────┬──────────┘  │
│  ┌──────────────┐ ┌───────────────┐           │              │
│  │ smtc_ctrl    │ │ volume_ctrl   │           │              │
│  │ Windows SMTC │ │ pycaw 音量     │           │              │
│  └──────────────┘ └───────────────┘           │              │
└───────────────────────────────────────────────┼──────────────┘
                                                │
┌───────────────────────────────────────────────▼──────────────┐
│                 .NET 插件宿主 (PluginHost)                    │
│  ┌─────────────────────┐  ┌─────────────────────────────┐   │
│  │  NeteaseEnhance     │  │  SpotifySupport             │   │
│  │  网易云增强插件       │  │  Spotify 支持插件            │   │
│  │  (搜索/播放/监视器)  │  │  (搜索/播放)                 │   │
│  └─────────────────────┘  └─────────────────────────────┘   │
│  事件广播: SongChanged / PlaybackStateChanged / VolumeChanged│
└──────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│              桌面 UI（三选一）                               │
│  ┌─────────────┐  ┌─────────────┐  ┌────────────────────┐   │
│  │  WinUI 3    │  │    WPF      │  │  Python Tkinter    │   │
│  │  Mica 背景   │  │  深浅主题    │  │  轻量级 GUI        │   │
│  │  .NET 10    │  │  .NET 10    │  │  纯 Python         │   │
│  └─────────────┘  └─────────────┘  └────────────────────┘   │
│         通过 SmtcApiClient 调用 Flask API                    │
└─────────────────────────────────────────────────────────────┘
```

---

## 构建与发布

### 构建全部（Python 服务端 + .NET UI + 安装包）

```bat
build.bat
```

流程：
1. 检查 Python 环境 → 安装 PyInstaller 和依赖
2. PyInstaller 打包 Python 服务端 → `dist\SMTCPlayer\`
3. 编译 WinUI 项目（`dotnet build`，SelfContained）
4. 发布 WPF 项目（`dotnet publish --self-contained`）
5. Inno Setup 分别构建两个安装包 → `dist\SMTCPlayer_WPF_Setup_v*.exe` 和 `dist\SMTCPlayer_WinUI_Setup_v*.exe`

### 完整发布流程

```bat
release.bat
```

流程：Python 语法检查 → 单元测试 → 完整构建 → 复制 UI 产物到 dist

### 单独编译 .NET 项目

```bat
cd smtc-ui
dotnet build SMTCPlayer.sln -c Release
```

---

## 测试

```bat
python -m compileall server tests
python -m pytest tests -v
```

测试覆盖 `security.py` 中的 PIN 策略验证与哈希校验。

---

## 技术栈

| 层级 | 技术 |
|------|------|
| WinUI | .NET 10, Windows App SDK 1.8, H.NotifyIcon.WinUI, QRCoder |
| WPF | .NET 10, WPF, QRCoder |
| Python 服务端 | Flask, pycaw, qrcode, pystray, Pillow, pycryptodome, numpy, winrt-*, requests |
| 插件系统 | .NET AssemblyLoadContext (可收集), 自定义 PluginApi 契约 |
| 安全 | PBKDF2-SHA256 (200,000 次迭代), Windows DPAPI, HMAC 恒定时间比较 |
| 构建工具 | PyInstaller, dotnet CLI, Inno Setup 6/7 |

---

## 许可证

[MIT License](LICENSE) © 2026 FR-NEXT

---

## 致谢

- [WinRT for Python](https://github.com/Microsoft/xlang/tree/master/src/tool/python) — Windows SMTC API
- [pycaw](https://github.com/AndreMiras/pycaw) — Windows 音量控制
- [NeteaseCloudMusicApi](https://gitlab.com/Binaryify/neteasecloudmusicapi) — 网易云音乐 API 参考
- [QRCoder](https://github.com/codebude/QRCoder) — QR 码生成
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) — WinUI 托盘图标
