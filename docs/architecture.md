# 架构说明

## 系统边界

```text
Windows WPF 客户端
  ├─ 展示时间线、预览和传输任务
  ├─ 缓存媒体索引与缩略图
  └─ 通过 HTTPS 只读访问媒体
                │
        局域网 HTTPS + Range
                │
Android Companion
  ├─ 通过 MediaStore 查询照片和视频
  ├─ 提供缩略图与原文件流
  ├─ 广播 mDNS 服务
  └─ 保存配对授权
```

Android 端只暴露读取能力。媒体的复制、去重、临时文件、校验和最终命名均在
Windows 端完成。

## 模块职责

### Desktop

- `LinkGallery.Domain`：无框架依赖的领域模型。
- `LinkGallery.Application`：用例和端口接口。
- `LinkGallery.Infrastructure`：网络、发现、SQLite、缩略图和文件系统实现。
- `LinkGallery.Desktop`：WPF 展示层。

依赖方向为 Desktop / Infrastructure → Application → Domain。

### Android

- `media`：封装 MediaStore 查询、元数据和缩略图。
- `server`：只读 HTTP 路由及认证。
- `pairing`：二维码配对、密钥和 Android Keystore。
- `discovery`：mDNS / NSD 广播。
- `background`：前台服务和连接状态。

## 安全边界

- API 不定义媒体写入、修改或删除端点。
- 未配对设备不得读取媒体列表或文件。
- 正式连接使用 HTTPS、证书指纹固定和短期访问令牌。
- 日志不得记录令牌、私钥、原始二维码内容或完整媒体路径。

## 可靠传输

1. 创建持久化传输任务。
2. 写入 `<filename>.partial`。
3. 使用 HTTP Range 从已写入偏移继续。
4. 校验文件大小，必要时校验 SHA-256。
5. 原子重命名为最终文件。
6. 记录本地副本，避免重复复制。

同名文件不得覆盖；应生成带序号的新名称或按日期、来源分目录。

