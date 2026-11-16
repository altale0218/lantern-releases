# 构建与签名

## 环境

- .NET 10 SDK，遵循 global.json 的版本及 roll-forward 配置。
- Windows 项目需 Windows 与 .NET 8 Windows Desktop Runtime；构建使用对应目标引用包。
- Android 项目需 .NET Android workload、Android SDK 36 和 JDK 21。先按官方安装流程接受所需 SDK 许可。
- Java 启动契约测试需要 JDK 21；运行检查脚本使用 PowerShell 7 与 Node.js 20 或更新版本（仅用于 JavaScript 语法检查）。

源码不含 SDK、依赖缓存、NuGet 凭据或本地工具路径。通过公开 NuGet 源恢复 PackageReference。

## Windows

```powershell
dotnet restore src/Lantern.Windows/Lantern.Windows.csproj
dotnet publish src/Lantern.Windows/Lantern.Windows.csproj -c Release -r win-x64 --self-contained false -o dist/windows
```

运行前安装 WebView2 Runtime 和匹配的 .NET Windows Desktop Runtime。dist 是本机生成目录，不提交源码仓库。

## Android

先配置 ANDROID_HOME（SDK 路径）和 JAVA_HOME（JDK 21 路径），然后：

```powershell
dotnet workload install android
dotnet publish src/Lantern.Android/Lantern.Android.csproj -c Release -p:AndroidSdkDirectory="$env:ANDROID_HOME" -p:JavaSdkDirectory="$env:JAVA_HOME" -o dist/android
```

本地构建使用构建工具生成或本机配置的开发签名；仓库不提供原开发者证书、指纹或私钥。请使用独立测试设备/配置，不要卸载或覆盖含重要资料的安装来试验不同签名。

自建包与原安装签名不同，不能直接覆盖更新，也不能通过原客户端的签名校验门槛。正式发布需自行保管签名密钥并稳定延续；不要把密钥、证书、密码、签名指纹或签名后的应用上传此源码仓库。

Android 工程保留程序集存储、压缩与 JNI 兼容配置。修改后应运行 NativeStartup 的符号检查，并检查 APK 16 KB ZIP/ELF 对齐。编译成功和生命周期模拟不等同于真机启动成功。

项目不默认配置厂商超级岛/胶囊 App ID，也不包含厂商准入凭据。

## 发行元数据隐私

Directory.Build.props 在 Release 构建关闭自有调试符号，并映射本地源码路径；Debug 开发配置不受影响。发布前运行 tools/Check-ReleasePrivacy.ps1 -Directory <发行目录>，检查当前本机构建身份和调试/私密材料。该检查不证明所有压缩内容或发布者身份绝对匿名；APK 的公开签名仍用于验证更新来源。
