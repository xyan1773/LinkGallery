# 贡献指南

## 开发原则

- Android 媒体访问必须保持只读。
- 协议变更先修改 `protocol/openapi.yaml`，再修改两端实现。
- 领域规则进入 `LinkGallery.Domain`，用例进入 `LinkGallery.Application`。
- 基础设施代码不得反向渗透到 Domain / Application。
- 安全相关决定和跨模块架构决定需要新增 ADR。

## 分支与提交

- 从 `main` 创建短生命周期分支，例如 `feature/media-listing`。
- 一个提交只表达一个完整意图。
- 提交信息使用祈使句，例如 `Add media paging contract`。
- 合并前保持测试通过，不提交密钥、SDK 路径、数据库或媒体文件。

## 本地检查

```powershell
.\scripts\build.ps1
```

只检查桌面端：

```powershell
.\scripts\build.ps1 -SkipAndroid
```

只检查 Android：

```powershell
.\scripts\build.ps1 -SkipDesktop
```

## Pull Request

PR 应说明：

- 改了什么、为什么改；
- 用户或开发者会感受到什么变化；
- 如何验证；
- 是否影响协议、隐私、安全或迁移。

