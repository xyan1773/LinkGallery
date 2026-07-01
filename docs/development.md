# 开发环境

## 本机约定

当前开发机工具位于：

```text
.NET SDK:       E:\tools\dotnet8
Android Studio: E:\coding_ide\android
Android JDK:    E:\coding_ide\android\jbr
Android SDK:    E:\tools\android-sdk
```

这些绝对路径不写入源码或提交文件。`scripts/dev-env.ps1` 会在存在时自动发现它们，
也允许使用标准的 `DOTNET_ROOT`、`JAVA_HOME` 和 `ANDROID_HOME` 覆盖。

## 构建

```powershell
.\scripts\build.ps1
```

脚本会依次：

1. 恢复、编译和测试 .NET solution；
2. 使用 Gradle Wrapper 编译 Android debug APK；
3. 把构建产物保留在各工具默认目录，运行数据不会进入 Git。

## IDE

- Windows：Visual Studio 2022 或 Rider 打开 `LinkGallery.sln`。
- Android：Android Studio 打开 `android/`。
- Android Studio 的 SDK Location 应指向 `E:\tools\android-sdk`。

## Android 连接测试

模拟器必须先执行 `adb forward tcp:39570 tcp:39570`，然后由 Windows 连接
`127.0.0.1:39570`；不要使用模拟器内部的 `10.0.2.x` NAT 地址。真实手机则必须和
Windows 位于同一 Wi-Fi，并使用 Android 页面显示的 LAN IP。

完整的真机回归、防火墙和 Wi-Fi AP 客户端隔离排查步骤见
[Android 连接与回归测试](connectivity-testing.md)。

## 协议变更

`protocol/openapi.yaml` 是两端通信的唯一规范源。破坏兼容性的修改需要：

1. 新增 ADR；
2. 提升 API 主版本；
3. 说明旧客户端和旧手机端的行为。
