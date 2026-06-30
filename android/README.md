# LinkGallery Companion

Android 端负责通过 MediaStore 读取媒体，并在局域网暴露只读 API。

工程基于 AGP 9.2.1、内置 Kotlin 和 Jetpack Compose。开发机使用
`E:\tools\SDK26`；该机器安装 Android SDK 后，可在 Android Studio 中直接导入
本目录。

```text
app/src/main/java/com/linkgallery/companion/
├── ui/
├── media/
├── server/
├── pairing/
├── discovery/
└── background/
```

当前骨架已经包含 Compose 入口和媒体权限请求。首个可运行切片继续实现：

- 查询 MediaStore 并分页映射到协议模型；
- 提供 `/api/v1/device` 和 `/api/v1/media`；
- 用 Android 前台服务承载本地服务器。

服务端不得提供删除、重命名、移动、编辑或上传媒体的路由。

本地构建：

```powershell
$env:JAVA_HOME = 'E:\tools\SDK26'
.\gradlew.bat :app:assembleDebug
```

Gradle Wrapper 需要在 Android Studio 首次导入或可用 Gradle 环境中生成。

