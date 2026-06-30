# LinkGallery

LinkGallery 是一套局域网无线相册工具：Windows 客户端自动发现已配对的
Android 手机，浏览照片和视频，并把选中的原文件可靠地复制到电脑。

核心约束：

- 手机媒体始终只读，不提供删除、移动、重命名、编辑或上传接口。
- 所有复制状态和本地文件都由 Windows 客户端管理。
- 传输使用临时文件、断点续传和完成校验，避免留下损坏的成品文件。
- MVP 中，DJI Pocket 3 内容先通过 DJI Mimo 进入手机，再由 LinkGallery 读取。

## 仓库结构

```text
LinkGallery/
├── desktop/          # C# / .NET 8 / WPF 客户端
├── android/          # Kotlin / Jetpack Compose 手机端
├── protocol/         # 两端共享的 HTTP API 契约
└── docs/             # 架构决策与开发路线图
```

## MVP 技术栈

- Windows：C#、.NET 8、WPF、SQLite
- Android：Kotlin、Jetpack Compose、MediaStore、Ktor Server
- 通信：mDNS / DNS-SD、HTTPS REST API、HTTP Range

## 开发状态

当前仓库是初始化骨架。第一条纵向链路是：

1. Android 从 MediaStore 读取设备信息和媒体列表。
2. Android 在局域网提供只读 HTTP API。
3. Windows 通过手动 IP 连接并展示媒体列表。
4. 验证可行后再加入缩略图、自动发现、配对和可靠传输。

详细边界与里程碑见 [架构说明](docs/architecture.md) 和
[开发路线图](docs/roadmap.md)。

