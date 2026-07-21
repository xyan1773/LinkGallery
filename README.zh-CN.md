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

## 关键工程决策

这些决策是在项目的具体阶段做出的，而不是在代码完成后补写的说明：

| 决策阶段 | 核心问题 | 决策与原因 |
| --- | --- | --- |
| 产品边界 | 是否允许桌面端整理手机上的文件？ | Android 严格保持只读。相册程序的缺陷不能删除或改写源媒体库。 |
| 跨平台协议 | 如何避免 C# 与 Kotlin 在不知不觉中产生差异？ | 将 `protocol/openapi.yaml` 和共享 JSON 夹具作为唯一事实来源，并让两端实现都通过契约测试。 |
| 可靠保存 | 路径、重名和中断文件由谁负责？ | 所有输出由 Windows 管理，并使用持久化任务、`.partial` 文件、Range 校验和最终发布。 |
| 未来媒体源 | 手机、相机、文件夹和 NAS 如何共用一个相册？ | 将设备差异隐藏在发现、媒体源和传输接口之后。 |
| 发布成熟度 | 能运行的本地原型是否可以作为正式软件发布？ | 不可以。安全与发布审查促成了明确的 Alpha 标识和公开使用前的加固路线图。 |

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

## 快速开始

### 环境要求

- 带有 PowerShell 的 Windows；
- .NET 8 SDK（`global.json` 当前指定 `8.0.422`，并允许使用最新的 8.0 补丁版本）；
- Android Studio、Android SDK 36、平台工具/ADB 和 JDK 21；
- Android 10 以上的真机，或 Android 模拟器；
- 使用真机时，两端需要处于可信的同一局域网。

环境发现脚本支持标准的 `DOTNET_ROOT`、`JAVA_HOME`、`ANDROID_HOME` 和
`ANDROID_SDK_ROOT` 环境变量。如果工具未被自动发现，请查看[开发环境](docs/development.md)。

### 1. 克隆并验证项目

```powershell
git clone https://github.com/xyan1773/LinkGallery.git
cd LinkGallery
.\scripts\build.ps1
```

该命令会恢复、构建并测试 .NET solution，随后构建 Android Debug APK 并运行其单元测试。只开发
一端时可以使用 `-SkipDesktop`、`-SkipAndroid`，也可以传入 `-Configuration Release`。

### 2. 安装 Android 伴侣应用

连接已开启 USB 调试的设备，然后安装构建产生的 APK：

```powershell
adb devices
adb install -r android\app\build\outputs\apk\debug\app-debug.apk
```

在 Android 上打开 LinkGallery，授予照片/视频只读访问权限和通知权限，并保持媒体服务运行。使用模拟器
时，连接前需要转发服务端口：

```powershell
adb forward tcp:39570 tcp:39570
```

### 3. 启动 Windows 应用

```powershell
dotnet run --project desktop\LinkGallery.Desktop\LinkGallery.Desktop.csproj
```

在 Android 上开启两分钟配对窗口。在 Windows 中选择 **查找设备**或**配对设备**，输入 Android 显示
的地址码，然后选择手机浏览时间线和相册。如果设备发现被阻止，请允许 LinkGallery 通过 Windows
专用网络防火墙，并查看[连接指南](docs/connectivity-testing.md)。

### 可选的可复现演示数据

正常使用会读取手机现有的 MediaStore 媒体库，因此不强制需要示例数据。如果需要隐私安全、可重复的
模拟器演示，请在单独目录放入至少一个非敏感 `.JPG` 和 `.MP4`，然后运行：

```powershell
.\scripts\run-e2e.ps1 -Profile Smoke `
  -SourceMediaRoot C:\path\to\safe-demo-media
```

Smoke 档位会选择最小的 JPG 和 MP4，只将它们复制到专用的
`/sdcard/DCIM/LinkGalleryE2E` 目录，随后运行 Android/API/Windows 用户旅程，并将证据写入
`artifacts/e2e/<时间>-smoke`。源目录始终只读。如果没有合适的模拟器，请先阅读
[端到端测试](docs/e2e-testing.md)，再考虑使用 `-RecreateAvd`；该参数会重建指定 AVD，并需要大量
磁盘空间。

## Alpha 状态与安全说明

LinkGallery 会读取私人照片和视频，因此安全优先于功能便利。当前仓库属于**早期 Alpha 原型**，尚未
达到生产发布标准。在加密传输、正式配对加固、凭据生命周期、依赖更新、隐私安全演示内容和正式签名
发布完成之前，请仅在可信局域网中使用，并优先使用测试或非敏感媒体。

API 有意不提供任何 Android 媒体写入路由，私有媒体路由必须使用已配对凭据。安全回归测试覆盖认证
失败、凭据撤销、配对过期、路径穿越、无效 Range 请求和传输文件发布边界。

请不要在公开 GitHub Issue 中披露安全问题。报告方式见 [SECURITY.md](SECURITY.md)。

## GPT-5.6 与 Codex 如何加速工作流程

GPT-5.6 与 Codex 承担了不同但互补的角色。**GPT-5.6** 主要用于产品推理、方案权衡、威胁建模，以及
在实现前质疑已有假设。**Codex** 作为理解仓库上下文的工程代理，负责检查现有代码和 Git 历史、同时
修改 C# 与 Kotlin、运行构建和测试、将实现与 OpenAPI 契约对照，并准备可审查的 GitHub 变更。

实际工作循环是：

1. 描述用户旅程与验收边界；
2. 使用 GPT-5.6 比较设计方案，并找出缺失的失败场景和安全场景；
3. 让 Codex 跨 Android、Windows、协议、测试和文档追踪受影响代码；
4. 实现最小但完整的纵向功能切片；
5. 运行针对性测试、完整构建与 CI，并在合并前审查差异。

具体应用如下：

| 阶段 | GPT-5.6 / Codex 的具体用法 | 决策或产物 | 如何加速工作 |
| --- | --- | --- | --- |
| 产品规划 | 将最初对 Phone Link 的不满整理为用户旅程、排除项、里程碑和适合 GitHub 的任务。 | `docs/product-scope.md`、路线图和聚焦的 Issue | 将开放式原型转化为可以验收的纵向切片。 |
| 架构 | 比较设备专用界面与共享媒体模型、适配器边界；随后由 Codex 追踪 solution 内的依赖关系。 | Domain/Application/Infrastructure 分层与媒体源接口 | 后续媒体源可以复用相册，而不是复制整套界面。 |
| 跨平台协议 | 在一次仓库级审查中对照 `protocol/openapi.yaml`、Kotlin 模型和 C# DTO，并加入共享夹具和契约测试。 | 两个平台共同遵循一个 OpenAPI 边界 | 将两次人工审查变成一次可重复的兼容性检查。 |
| 可靠传输 | 在实现前枚举断线、重试、磁盘、权限、重名、响应截断和重启场景。 | 持久化任务、`.partial` 文件、Range 检查和安全发布测试 | 在失败场景演变成界面缺陷或损坏文件前发现它们。 |
| 安全与隐私 | 扫描受跟踪文件和 Git 历史，检查配对、令牌、依赖和发布产物，并审查演示截图。 | Alpha 警告、隐私安全预览、加固优先级和安全回归矩阵 | 避免发布私人媒体，并将安全审查提前到发布流程中。 |
| 交付 | 运行仓库检查，将无关工作树改动排除在提交外，准备双语文档、推送分支并创建 PR。 | 带有 CI 证据的小型可审计提交 | 减少实现、验证和 GitHub 交付之间的上下文切换。 |

这里的加速并不只是更快地产生代码，更重要的是缩短了“决策—跨平台实现—测试—审查证据”之间的反馈
循环。由于没有实际记录开发时长，本项目不虚构节省时间的数字；上表所列的仓库产物就是 AI 改变工作
流程的位置证据。

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
