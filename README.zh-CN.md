# LinkGallery

[English](README.md) | **简体中文**

> 一个相册，连接每一台设备。

LinkGallery 是一款本地优先的跨设备媒体浏览工具，目标是让 Windows 成为浏览和保存个人设备照片、
视频的统一入口。当前 Alpha 版本由 Windows 桌面客户端和 Android 手机伴侣应用组成，两端通过局域网
连接。

[![CI](https://github.com/xyan1773/LinkGallery/actions/workflows/ci.yml/badge.svg)](https://github.com/xyan1773/LinkGallery/actions/workflows/ci.yml)

## 项目缘起

LinkGallery 源于我使用 Microsoft Phone Link 管理 Android 手机照片时遇到的一个小烦恼：Phone Link
可以让 Windows 访问手机近期照片，但面对较大的个人媒体库时，它还不像一个完整的相册。

我希望能在同一个界面中浏览时间线，了解每项媒体来自哪个相册和设备，预览照片与视频，并把选中的
原文件保存到 Windows，而不必在多个互不相连的工具之间切换。同时，这套设计不应局限于一部手机。
个人媒体还可能来自 Windows 文件夹、DJI Pocket 相机、SD 卡、移动硬盘、NAS，以及未来的其他平台。

在没有找到完全符合这一产品构想的项目后，我决定围绕一个目标构建 LinkGallery：

> 让 Windows 成为浏览和管理不同个人设备媒体内容的统一终端。

## 当前能够做什么

目前的 Android 到 Windows 原型支持：

- 在局域网中发现 Android 设备并记住已配对设备；
- 通过 MediaStore 读取 Android 照片、视频、相册和媒体元数据；
- 在 Windows 中按时间线浏览，支持分页、筛选、相册、缩略图缓存和离线 SQLite 索引；
- 预览照片并播放视频；
- 使用持久化任务、`.partial` 临时文件、断点续传、Range 校验和安全发布，将选中的原文件复制到
  Windows；
- 识别部分 DJI Pocket 3 和 DJI Mimo 媒体特征；
- 由 Android 与 Windows 共享一份带版本的 OpenAPI 契约。

手机始终是一个**只读媒体源**。LinkGallery 不提供在 Android 上删除、移动、重命名、编辑或上传媒体
的操作。

## 产品原则

- **本地优先。** 媒体浏览和传输都在局域网中完成，不依赖云服务。
- **源设备只读。** Android 伴侣应用只提供元数据、缩略图和原文件读取流。
- **文件输出由 Windows 管理。** 目标路径、重名处理、传输状态和最终文件发布均由桌面客户端控制。
- **失败必须可恢复。** 中断的传输从临时文件继续，不能留下看起来完整但实际损坏的文件。
- **设备差异隐藏在适配器之后。** 新增媒体来源时，不应重写整个相册界面。

## 设计预览

仓库包含一份早期产品和组件设计探索。图中使用合成占位内容，不包含真实个人相册。

![LinkGallery 设计系统与产品探索](figma-linkgallery-preview.png)

## 架构

```text
Windows WPF 应用
  ├─ 时间线、相册、预览与传输界面
  ├─ SQLite 媒体索引与缩略图缓存
  ├─ 设备发现与已配对设备存储
  └─ 可靠复制与断点续传协调
                 │
       局域网 HTTP API + Range
                 │
Android 伴侣应用
  ├─ MediaStore 元数据与内容流
  ├─ 需要认证的只读 HTTP 路由
  ├─ 配对与本地凭据存储
  └─ NSD/mDNS 与 UDP 发现
```

桌面端采用分层结构：

```text
Desktop / Infrastructure → Application → Domain
```

Android 应用将媒体访问、发现、配对、服务端、设备身份和界面代码相互分离，避免平台相关行为渗透到
共享协议中。

## 仓库结构

```text
LinkGallery/
├── desktop/          # C#、.NET 8 与 WPF 桌面应用
├── android/          # Kotlin 与 Jetpack Compose 伴侣应用
├── protocol/         # OpenAPI 契约与跨平台测试夹具
├── e2e/              # 端到端验收工具
├── docs/             # 架构决策、产品范围、测试与路线图
├── scripts/          # 可复现的构建与测试入口
└── website/          # 静态项目网站
```

## 技术栈

| 范围 | 技术 |
| --- | --- |
| Windows | C#、.NET 8、WPF、SQLite |
| Android | Kotlin、Jetpack Compose、MediaStore、Android 前台服务 |
| 连接 | Android NSD/mDNS、UDP 发现、HTTP/1.1、Bearer 认证、HTTP Range |
| 协议 | OpenAPI、共享 JSON 夹具、Redocly |
| 质量 | MSTest、JUnit、Android UI 测试、端到端测试、GitHub Actions、Dependabot |

## 本地构建

Windows 下的主构建入口会恢复、构建并测试 .NET solution，随后使用 Gradle Wrapper 构建 Android
Debug APK：

```powershell
.\scripts\build.ps1
```

也可以使用 Visual Studio 或 Rider 打开 `LinkGallery.sln`，并使用 Android Studio 打开
`android/`。详细说明见[开发环境](docs/development.md)、[连接测试](docs/connectivity-testing.md)和
[端到端测试](docs/e2e-testing.md)。

## Alpha 状态与安全说明

LinkGallery 会读取私人照片和视频，因此安全优先于功能便利。当前仓库属于**早期 Alpha 原型**，尚未
达到生产发布标准。在加密传输、正式配对加固、凭据生命周期、依赖更新、隐私安全演示内容和正式签名
发布完成之前，请仅在可信局域网中使用，并优先使用测试或非敏感媒体。

API 有意不提供任何 Android 媒体写入路由，私有媒体路由必须使用已配对凭据。安全回归测试覆盖认证
失败、凭据撤销、配对过期、路径穿越、无效 Range 请求和传输文件发布边界。

请不要在公开 GitHub Issue 中披露安全问题。报告方式见 [SECURITY.md](SECURITY.md)。

## Codex 与 GPT-5.6 如何参与项目

开发过程中，Codex 与 GPT-5.6 被用于：

- 审查并改进跨设备架构；
- 将产品路线图拆分为 GitHub Issue 和里程碑；
- 跨 C# 与 Kotlin 实现和调试功能；
- 对比共享 API 契约在两端的行为；
- 改进错误处理、测试、文档与发布流程；
- 审查安全、隐私、依赖和分发风险。

最有价值的经验是：AI 不只能生成代码，也可以充当工程审查者。它帮助我们质疑假设、比较实现与预期
设计，并识别在扩大使用范围前必须完成的工作。

## 挑战与收获

让两个应用保持一致，比单独构建任何一端都更困难。Android 与 Windows 必须对媒体身份、分页、连接
状态、认证、Range 行为和失败语义达成一致。

可靠保存也远不只是下载字节。它需要持久化任务、安全的目标路径规划、重名处理、重试、磁盘与权限
错误分类、中断恢复，以及不会意外覆盖有效文件的最终发布步骤。

这个项目让我更清楚地认识到，跨设备软件同时涉及用户体验、协议、网络、移动端生命周期、存储完整性、
隐私和发布工程。

## 下一步

接下来的优先事项是：

1. 加固配对流程，并加入加密、经过认证的传输；
2. 完成生产级凭据轮换和正式发布签名；
3. 完善“保存到电脑”体验及其失败恢复界面；
4. 改进后台重连和真机验收测试；
5. 扩展大规模媒体库的性能测试；
6. 在 Android 到 Windows 链路稳定后，增加新的媒体来源适配器。

iPhone、公网访问、云同步、人脸识别、AI 搜索、Android 媒体编辑，以及 Windows 直接连接 Pocket 3
均不在当前 MVP 范围内。

## 文档

- [产品范围](docs/product-scope.md)
- [架构说明](docs/architecture.md)
- [开发路线图](docs/roadmap.md)
- [前端契约](docs/frontend-contract.md)
- [安全回归矩阵](docs/security-regression.md)
- [贡献指南](CONTRIBUTING.md)

---

**一个相册，连接每一台设备。**
