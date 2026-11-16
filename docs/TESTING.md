# 测试

普通检查：

```powershell
pwsh -File tools/Run-Checks.ps1
pwsh -File tools/Test-NativeStartup.ps1
```

Run-Checks 默认执行共享代码/Windows 构建、通知状态与存储回归、JavaScript 语法和 Java 启动契约测试。JDK 不在 JAVA_HOME 或 PATH 时，Java 检查会说明缺失环境并失败。

需要协议及局域网相关检查时，显式加上 -Network：

```powershell
pwsh -File tools/Run-Checks.ps1 -Network
```

网络检查使用生成的临时身份和内存密钥，可能发出测试发现广播。使用独立临时测试目录，不传入应用真实数据目录。其他长时间、多设备、Shell/剪贴板及真机测试可单独运行 tests 中的项目，并先阅读参数和副作用。

Java WindowStyle 子目录中的 android 类型是 JVM 契约测试替身，只用于检查启动顺序和异常处理，不是 Android Framework，也不会进入应用项目。API 数字只是模拟条件，不能作为对应系统或机型已经实测的证据。

不附原工作区的测试报告、手机诊断、截图、日志或接收文件；公开报告请重新生成并脱敏。

## 可选 UI 回归

进入 tests/ui，运行 npm install、npx playwright install chromium、npm test。脚本使用 Playwright 1.62.1 的开发依赖和虚构数据，不连接真实设备，不读个人文件或配对资料。结果与演示截图保存在系统临时目录；不要提交本机测试产物。

UI 检查还覆盖双端深浅主题的触摸高亮、嵌套图标、弹窗、键盘焦点、文字选择与更新方式说明。

触摸回归使用浏览器触摸输入模拟及兼容悬停，覆盖浅色/深色、安卓小屏/横屏、按钮/开关/弹窗和外接键盘焦点；同时验证 Windows 鼠标反馈。这不是 Android 真机或厂商 WebView 认证。

0.6.9 的版面检查包含宽屏、手机小屏/横屏、字号和界面缩放；宽屏统计按可用内容宽度切换四列或两列，避免出现三加一的不齐整布局。开源演示图已刷新，仍使用虚构设备与记录。

0.6.10 新增 compact-chat.cjs，npm test 会执行此检查。使用虚构会话验证移动端紧凑输入栏的触摸区域、无障碍名称、文字行高、横屏、大字号、模拟键盘视口与状态刷新后的多行草稿保留；这不代表 Android 真机输入法已实测。

0.7.0 新增 Lantern.SpeedTests，使用临时身份和内存数据验证认证后的双向吞吐、数据量与时长边界、篡改/回执拒绝、取消、撤销信任、离线以及文件传输优先。使用 Run-Checks.ps1 -Network 执行。UI 检查新增 speed-test.cjs，覆盖四页顶部对齐、方向与单位、自动开始、停止/返回、迟到响应及失败不伪装成功。
