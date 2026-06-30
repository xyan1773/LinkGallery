# LinkGallery Protocol

`openapi.yaml` 是 Windows 和 Android 之间通信的唯一规范源。

协议约束：

- 媒体 API 只允许 GET。
- 原文件端点必须支持 HTTP Range。
- 除配对握手外，所有端点都要求短期 Bearer Token。
- API 中的 `mediaId` 是不透明标识，不能被客户端当作文件路径。
- 破坏兼容性的变更必须提升 `/api/v{major}` 并新增 ADR。

配对端点会改变设备授权状态，但不会修改照片或视频。

