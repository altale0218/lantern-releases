# 自行发布与 OTA 配套

1. 更新 AppRelease（版本、Android code 和更新日志）及两个项目的显示版本，保持双端一致。
2. 运行相关构建、协议/存储/启动检查及 Android 真机验证。
3. 使用自己安全保管的稳定签名构建 APK。源码发布与二进制发布分别审核；不要从开发目录直接上传全部产物。
4. 在 Windows 发布目录创建 Updates，放置唯一配套 APK，文件名为 Lantern-Android.apk；不要复制原开发者的安装包或证书到公开源码。
5. 同目录的 android-release.json 必须与实际 APK 一致：

| 字段 | 取值 |
| --- | --- |
| protocol | 1 |
| packageId | com.lantern.transfer |
| version / versionCode | AppRelease 中对应的版本与 Android code |
| size | APK 的实际字节数 |
| sha256 | APK 的 SHA-256，小写十六进制 |
| notes | 对应版本的公开更新日志 |

可使用 Get-Item 获取长度、Get-FileHash -Algorithm SHA256 计算摘要，以 UTF-8 编写 JSON。不要填旧包摘要或把版本号改大来绕过校验。WindowsUpdateStore 会读取该配套目录；元数据不匹配时不会提供更新。

Android 下载仍经已配对设备的认证加密通道；安装前校验包体、应用标识、版本和签名。公开源码没有提供原发行签名，自行构建的分支必须使用自己的签名链。

tools/Update-Windows.ps1 是原路径更新器源码，运行时应位于完整 Windows 发布包根目录，并配套 release-files.json 文件清单。它不是源码仓库里的“一键发布/覆盖”命令；本准备包不会执行更新器或修改本机客户端。

GitHub Actions 只进行检查，不上传构建产物、不配置签名、不发布 Release、不需要自定义账户密钥。

Windows 客户端不支持互联网在线更新，请前往本项目 GitHub 仓库的 Releases 页面下载最新版并按包内说明在原目录手动更新。Windows 向已认证安卓设备提供局域网 OTA 的功能保持可用。

可独立发布 Lantern-Android.apk 与 Lantern-Windows.zip，方便两端分别下载。Windows 的 Updates 目录保留唯一当前配套 APK 供 OTA 使用；独立 APK 必须与其包名、版本、签名及 SHA-256 完全一致。源代码公开目录仍不得包含 APK 或已签名二进制。
