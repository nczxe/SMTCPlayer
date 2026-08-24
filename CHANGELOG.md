# 更新日志

本文档记录 SMTC Player 的所有重要更新和变更。

---

## [1.1.1] - 2026-08-23

> 构建号：26082323500S

### 外观问题

- **全屏设置** 我们将设置改为全屏实现意旨给予一个更接近 Windows 原生应用程序的全屏体验

### 新增功能

- **检查更新**（WinUI 设置面板）：新增检查更新功能，用户可以在设置中手动检查是否有新版本可用
- **播放进度偏移校准**（WinUI 设置面板）：手动微调 ±5 秒（±1/±10ms 步进 + 直接输入），校准窗口实时预览当前进度，仅对网易云增强监视器提供的进度生效，本项更改是因为我们重新实现了进度获取功能

### 问题修复

- 修复网易云监视器仅扫描单一进程，导致主窗口关闭后无法获取播放进度与歌曲信息的问题（现自动扫描所有网易云子进程并合并定位）
- 修复监视器未锁定进度时 `-1` 哨兵值污染界面进度显示的问题
- 修复监视器子进程中文日志乱码（输出统一 UTF-8 编码）

## [1.1.0] - 2026-08-20

### 新增功能

#### 插件系统
- **插件架构**：基于 .NET AssemblyLoadContext 的可收集插件加载系统
- **插件契约**：新增 `SMTCPlayer.PluginApi` 零依赖程序集，定义 `IPlugin`、`ISearchProvider`、`IPlaybackController` 等接口
- **事件广播**：实现 `SongChanged`、`PlaybackStateChanged`、`VolumeChanged`、`ServerStateChanged` 事件分发
- **插件宿主**：新增 `PluginHost` 核心框架，支持插件发现/加载/启用禁用/事件广播
- **隔离设置**：每个插件独立的设置存储（`%LocalAppData%/SMTCPlayer/plugins-data/<plugin-id>/settings.json`）
- **插件日志**：插件专属日志系统，自动带插件标识前缀

#### 内置插件
- **网易云增强插件** (`netease-enhance`)：
  - 搜索/播放功能
  - 登录状态监控
  - 退出时自动清除 Cookie（可配置）
  - 托管 NeteaseWatcher 子进程，提供精准播放进度
- **Spotify 支持插件** (`spotify-support`)：
  - SMTC 来源识别
  - 搜索/播放功能（Web API Client Credentials）
  - 支持 `play-request.json` 命令文件

#### WinUI 版本
- **原生 UI**：基于 Windows App SDK 1.8 的 Mica 背景和 Acrylic 面板
- **系统托盘**：H.NotifyIcon.WinUI 托盘图标，支持右键菜单
- **设置面板**：主题切换、调试模式、PIN 码管理

#### WPF 版本
- **主题系统**：深色/浅色主题切换
- **调试控制台**：实时查看日志输出
- **设置持久化**：主题、调试模式等保存到本地

#### 移动端 Web
- **PWA 支持**：添加到主屏幕，离线访问
- **响应式布局**：横竖屏自适应
- **玻璃拟态 UI**：深色主题，毛玻璃效果

### 改进优化

#### 核心架构
- **MVVM 模式**：重构为 `MainViewModel` 统一管理状态
- **Flask 服务端**：优化 API 响应，新增 `/api/health` 健康检查端点
- **安全增强**：
  - PIN 认证：PBKDF2-SHA256（200,000 次迭代）哈希存储
  - Cookie 加密：Windows DPAPI 加密，仅当前用户可解密
  - HMAC 恒定时间比较，防止时序攻击

#### Python 服务端
- **SMTC 控制**：WinRT API 封装，支持播放/暂停/上下首/音量控制
- **音量控制**：pycaw 系统音量控制，支持静音切换
- **网易云 API**：WEAPI 加密，支持搜索/歌单/歌曲详情
- **NeteaseWatcher**：原创行为启发式内存扫描，提供精准播放进度

#### 构建系统
- **PyInstaller**：打包 Python 服务端为独立可执行文件
- **Inno Setup**：生成安装包（WinUI/WPF 两个版本）
- **SelfContained**：安装包包含 .NET 运行时，无需额外依赖

### 问题修复

- 修复 PIN 码验证时序攻击漏洞
- 修复插件热更新时内存泄漏问题
- 修复网易云监视器子进程意外退出未重启的问题
- 修复 Spotify 搜索结果解析错误

---

## [1.0.0] - 2026-08-01

### 初始版本

#### 核心功能
- **媒体控制**：播放/暂停、上一首、下一首、音量控制
- **状态显示**：歌曲标题、艺术家、专辑封面实时显示
- **局域网控制**：通过手机浏览器访问 `http://<PC-IP>:8888` 控制电脑播放

#### UI 版本
- **WinUI 版**：Windows 11 原生体验
- **WPF 版**：Windows 10/11 兼容性好
- **Python 版**：轻量级，跨 Python 版本运行

#### 安全机制
- **PIN 认证**：4-16 位，首次访问强制设置
- **Token 鉴权**：PIN 换取 session token

#### 移动端 Web
- **浏览器访问**：支持 PWA
- **深色主题**：响应式布局

---

## 版本说明

### 版本号规则

采用语义化版本：`主版本号.次版本号.修订号`

- **主版本号**：不兼容的 API 修改
- **次版本号**：向下兼容的功能性新增
- **修订号**：向下兼容的问题修正

### 更新类型

- **新增功能**：新特性、新插件、新 UI 组件
- **改进优化**：性能提升、代码重构、用户体验改进
- **问题修复**：Bug 修复、安全漏洞修复
- **文档更新**：README、API 文档、插件开发指南

---

## 贡献指南

欢迎提交 Issue 和 Pull Request！

1. Fork 项目仓库
2. 创建功能分支：`git checkout -b feature/my-feature`
3. 提交更改：`git commit -m 'Add my feature'`
4. 推送分支：`git push origin feature/my-feature`
5. 创建 Pull Request

---

## 许可证

[MIT License](LICENSE) © 2026 FR-NEXT