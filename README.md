# 闪传 Lantern 0.7.4 测试版

面向 Windows 和 Android 的局域网文件传输及设备聊天工具。同一网络内，经配对后可互传文件、图片和消息。

## 下载

请打开本仓库的 Releases / 发行版，选择 **0.7.4 测试版**。

- Android 下载 Lantern-Android.apk。此次发布的 Android versionCode 为 37。
- Windows 下载 Lantern-Windows.zip，解压后运行 Lantern.exe。
- SHA256SUMS.txt 可用于校验下载的两个文件。

## 使用

在两端打开闪传并连接到同一局域网。发现设备后使用匹配码配对，再在两端确认。配对完成后，选中发送对象和文件即可发送。

## 源码状态

主分支的 ui/ 收录从 0.7.4 两个安装包中提取并逐文件比对的 HTML、CSS、JavaScript 和 SVG，可用于阅读和界面开发。设备发现、配对、加密传输与系统安装依赖原生客户端。完整的原生项目和可重建源码快照待补齐。

可以用浏览器打开 ui/index.html 查看界面。下载附件和版本说明请参阅 0.7.4 发行页。
