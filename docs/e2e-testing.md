# 端到端用户行为测试

`scripts/run-e2e.ps1` 联合驱动 Android Companion、只读 HTTP API 和 Windows
客户端。测试使用独立桌面数据目录；媒体源目录只读，所有派生文件只写入
`/sdcard/DCIM/LinkGalleryE2E` 和 `artifacts/e2e`。

## 首次准备

当前运行中的模拟器若仍是 10 GB，需要显式重建。此操作会清空
`Pixel_10_Pro_XL`：

```powershell
.\scripts\run-e2e.ps1 -Profile Smoke -RecreateAvd
```

本机命令行 SDK 尚未提供 Pixel 10 Pro XL 的设备定义，因此脚本保留原 AVD
名称，但默认使用尺寸最接近的 `pixel_9_pro_xl` 硬件模板；可通过
`-AvdDevice` 覆盖。100 GB 分区在首次创建时需要约 120 GB 宿主空间，默认
将 AVD 放在 `D:\AndroidAVD`；可用 `-AvdHome` 指向其他空间充足的磁盘。

脚本在复制素材前检查 `/sdcard` 至少有 80 GB 可用。若容量扩展未生效，测试立即
停止，不会推送大文件。

## 测试档位

```powershell
# 一张照片、一段短视频；适合开发回归
.\scripts\run-e2e.ps1 -Profile Smoke

# 约 58 GB DJI 混合素材，包含一段约 16 GB 视频
.\scripts\run-e2e.ps1 -Profile Experience

# 5,000 个派生图片条目，验证分页和首屏性能
.\scripts\run-e2e.ps1 -Profile Scale

# 连接循环和 30 分钟稳定性测试
.\scripts\run-e2e.ps1 -Profile Soak -SoakMinutes 30

# 真机只读冒烟；不写入或清除真机媒体
.\scripts\run-e2e.ps1 -Profile Physical `
  -DeviceSerial DEVICE_SERIAL `
  -PhysicalAddress PHONE_IP:39570 `
  -SkipMedia
```

`Experience`、`Scale` 和 `Soak` 应分开执行，每档开始前只清理模拟器中的
`LinkGalleryE2E` 测试目录。默认素材目录为
`D:\系统导航\Pictures\DJI_001`，可用 `-SourceMediaRoot` 覆盖。

## 结果

每次执行在 `artifacts/e2e/<时间>-<档位>` 生成：

- `summary.json`：总体结论、媒体数量、崩溃和 ANR 统计；
- `desktop-e2e.json` / `desktop-e2e.xml`：UI 旅程与耗时；
- `readonly-api.json`：DELETE、PATCH、PUT 只读边界检查；
- `android-logcat.txt`、Android/Windows 截图和导入文件；
- `media-manifest.json`：从源目录选择的只读素材清单。

任何 Windows 异常退出、Android `FATAL EXCEPTION`/ANR、写 API 可用或 UI
旅程失败都会使脚本返回非零退出码。该套件依赖交互式 Windows 桌面和本地 Android
模拟器，因此不会加入普通 CI。
