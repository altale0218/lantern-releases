# 闪传 Lantern · iPhone 和 iPad

`Lantern.iOS.csproj` 是一个 iPhone 和 iPad 通用目标。它和 Windows、Android 共享配对、文件校验、聊天与测速代码，并且打包使用同一套界面资源。iPad 支持系统标签栏与可切换的侧栏。使用 Xcode 26 SDK 构建后，iOS 26、iPadOS 26 会为系统控件应用 Liquid Glass。

## 在 Mac 上构建

安装 .NET 10、匹配该 iOS 工作负载版本的 Xcode 26，并通过 `xcode-select` 选择对应的 Xcode。Apple 芯片 Mac 上可以先构建模拟器版本。

```sh
dotnet workload install ios
dotnet build src/Lantern.iOS/Lantern.iOS.csproj -c Debug -r iossimulator-arm64
```

真机调试使用 `ios-arm64`，由 Mac 上的开发签名和描述文件签名。项目内的 `ApplicationId` 用于源码标识，开发者可在本机通过 `-p:ApplicationId=…` 配置其团队拥有的标识。Apple 端的更新随 App Store 或 TestFlight 分发。

## 连接和文件

- 双方打开闪传并接入同一局域网。首次点「刷新设备」或「配对新设备」时，请允许系统的本地网络访问。iPhone、iPad 和 0.7.5 起的 Windows、Android 客户端可以通过同一项 Bonjour 服务交换发现地址。列表仍为空时，可以查看目标设备的局域网 IPv4 地址和端口，手动输入匹配码并在双方确认。
- 发现记录只提供连接地址。未配对的条目显示平台和设备短编号，配对完成后显示已认证设备的名称。已记住的设备地址发生变化时，可使用「我的设备」中的手动连接，连接仍需通过已保存密钥验证。
- 接收文件在完成解密和完整性校验后保存到「文件」App 中的「我的 iPhone/iPad / 闪传 Lantern / Received」。聊天记录与设置位于私有容器，身份与密钥保存在本机钥匙串。接收过程中请保持 App 在前台。
- 使用系统文件选择器发送附件，接收、重试、多目标发送、设备聊天、记录管理和双向测速走共享逻辑。闪传内支持指定 Office 与文本格式预览，常用视频可生成系统缩略图，其它格式可尝试通过 Quick Look 打开。
- Apple 设备使用「文件」App 查看收到的内容，使用 App Store 或 TestFlight 分发更新。Apple 项目当前无系统传输通知和后台常驻接收。

## 真机验证

先在 iPhone 和 iPad 确认启动、深浅主题、字体大小和减少动态效果，检查 iPad 侧栏、分屏及横竖屏。分别验证允许、拒绝和重新开放局域网访问，确认四类设备可双向发现，再走一次双方确认的配对流程。与 Windows、Android 双向发送图片、大文件和消息，比较文件 SHA-256，在「文件」App 查找接收文件。验证设备换网、退出再上线、手动连接、外部文件导入、视频缩略图、Quick Look、删除确认和重启后的密钥保留。
