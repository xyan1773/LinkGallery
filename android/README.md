# LinkGallery Companion

Android 端负责通过 MediaStore 读取媒体，并在局域网暴露只读 API。

工程基于 AGP 9.2.1、内置 Kotlin 和 Jetpack Compose。开发机使用 Android
Studio 自带的 JDK 21 与 `E:\tools\android-sdk`，可在 Android Studio 中直接导入
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
$env:JAVA_HOME = 'E:\coding_ide\android\jbr'
$env:ANDROID_HOME = 'E:\tools\android-sdk'
.\gradlew.bat :app:assembleDebug
```

## Development HTTP API

While the companion activity is visible, the development server listens on port
`39570`. Grant photo/video read permission first, find the phone's LAN address,
then verify the first read-only endpoints from Windows:

```powershell
curl.exe http://PHONE_IP:39570/api/v1/device
curl.exe "http://PHONE_IP:39570/api/v1/media?limit=20&type=image,video"
```

The server stops when the activity leaves the foreground. This development slice
uses HTTP without authentication; pairing and HTTPS are added by later issues.
Media upload and all `DELETE`, `PATCH`, and `PUT` routes are intentionally absent.
