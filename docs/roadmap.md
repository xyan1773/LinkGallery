# 开发路线图

## Phase 1：打通最小链路

- Android 查询 MediaStore。
- Android 提供 `GET /device` 和 `GET /media`。
- Windows 支持手动输入手机 IP。
- Windows 展示基础媒体列表。

验收：同一 Wi-Fi 下，Windows 能稳定列出 Android 的照片和视频元数据。

## Phase 2：相册浏览

- 游标分页。
- 缩略图接口和本地缓存。
- 时间线界面。
- SQLite 增量索引。
- 照片预览和视频基础播放。

## Phase 3：可靠复制

- 原文件流和 HTTP Range。
- `.partial` 临时文件。
- 暂停、恢复和网络中断续传。
- 文件大小 / SHA-256 校验。
- 去重和同名文件处理。

## Phase 4：无感连接

- mDNS 自动发现。
- 二维码配对。
- HTTPS 和证书指纹固定。
- 后台自动重连和设备在线状态。

## Phase 5：Pocket 3 分类

- 识别 DJI 文件和导出目录。
- 区分 Pocket 3 原始文件与 DJI Mimo 编辑导出。
- 按来源筛选和组织本地目录。

## MVP 明确不做

iPhone、公网访问、云同步、照片编辑、人脸识别、AI 搜索、电脑向手机上传、
手机媒体整理，以及 Windows 直接连接 Pocket 3。

