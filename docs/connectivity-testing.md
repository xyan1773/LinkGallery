# Android 连接与回归测试

LinkGallery 的 Android 开发服务仅在应用页面位于前台时监听 `39570` 端口。模拟器与真实手机使用两种不同的网络路径，不应混用地址。

## Android 模拟器：ADB forward

模拟器中显示的 `10.0.2.x` 是 Android Emulator 的内部 NAT 地址，Windows 宿主机不能通过该地址直接访问应用。使用 ADB 将 Windows 的回环端口转发到模拟器：

```powershell
adb devices
adb forward tcp:39570 tcp:39570
curl.exe http://127.0.0.1:39570/api/v1/device
```

在 Windows 客户端中输入 `127.0.0.1:39570`。重启模拟器、重新连接设备或执行 `adb forward --remove-all` 后，需要重新建立转发。可用 `adb forward --list` 检查当前转发。

## 真实手机：同一 Wi-Fi

1. 将 Android 手机和 Windows 电脑连接到同一个 Wi-Fi。
2. 打开 LinkGallery Companion，授予照片和视频只读权限，并保持页面在前台。
3. 使用 Android 页面显示的局域网 IPv4 地址（通常为 `192.168.x.x` 或 `10.x.x.x`，但不是模拟器的 `10.0.2.x`）。
4. 在 Windows 上验证设备接口，再使用同一地址连接桌面客户端：

```powershell
Test-NetConnection PHONE_IP -Port 39570
curl.exe http://PHONE_IP:39570/api/v1/device
```

成功响应应为设备 JSON；随后 Windows 客户端应能同步并显示媒体列表。

## 故障分类

- **连接超时**：数据包没有得到响应。检查手机是否在线、IP 是否变化、两端是否在同一 Wi-Fi，以及 AP 客户端隔离。
- **连接被拒绝**：地址可到达，但 `39570` 没有服务监听。将 Android 应用切回前台并重试。
- **模拟器 NAT 地址不可达**：不要从 Windows 连接 `10.0.2.x`；执行 `adb forward` 并使用 `127.0.0.1:39570`。
- **HTTP 403**：Android 尚未授予照片和视频读取权限。

### Windows 防火墙

本流程是 Windows 主动访问手机，通常不需要开放 Windows 入站端口。若安全软件或出站规则拦截未知局域网连接，请允许 LinkGallery（或用于验证的 `curl.exe`）访问“专用网络”。可暂时用 `Test-NetConnection` 对比规则启用前后的结果；不要长期关闭防火墙。

### Wi-Fi AP 客户端隔离

访客网络、企业网络和部分路由器会启用 **AP isolation / Client isolation / 无线客户端隔离**，使同一 SSID 下的设备仍不能互访。将两台设备切换到允许客户端互访的家庭/专用网络，或在可信网络的路由器设置中关闭该功能。手机热点也可能实施隔离，应单独验证。

## 真机回归记录

每次发布前记录手机型号、Android 版本、Windows 网络类型、手机 LAN IP，以及以下结果：

- `GET /api/v1/device` 返回成功；
- Windows 客户端使用同一地址连接成功；
- 媒体索引完成并显示至少一张图片或一段视频；
- Android 应用退到后台后，Windows 明确报告“连接被拒绝”或不可达，而不是协议错误。
