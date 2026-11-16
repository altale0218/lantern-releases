# 发布供客户端检查的服务器更新

1. 更新 AppRelease 和双端项目版本。Android 使用原有签名密钥，并递增 versionCode。运行构建、启动检查和隐私审查。
2. 分别打包 Lantern-Android.apk 和 Lantern-Windows.zip。Windows 原路径更新器可随 ZIP 一起交付。
3. 生成纯文本 SHA256SUMS.txt，每行依次写一个文件的 64 位 SHA-256、两个空格和准确文件名。对两个安装包分别计算。
4. 在 GitHub 和 Gitee 用 vX.Y.Z 标签创建发行版，上传这三个附件。测试版勾选预发布，稳定版使用正式发行。更新日志要包含唯一一处 Android 的 versionCode 数字，例如 versionCode 40。
5. 发布后通过未登录的请求检查两个附件与校验清单。镜像上同一版本必须提供相同的字节数、摘要和 Android versionCode。
6. 使用 `tools/New-ServerCatalog.ps1` 生成核验过的渠道清单，然后同步到对应更新分支。单站附件受限时，可通过 `-Sources github` 或 `-Sources gitee` 只更新已验证下载的来源。另一个来源的清单继续指向实际可下载的版本，其发行说明可以提供已验证的外站下载链接。

客户端从项目固定的 HTTPS 发行接口读取版本，并对照同仓库发布的摘要。匹配的 Android 安装包还须通过原应用签名、包名和版本校验。Windows 使用经过摘要核对的 ZIP，由用户手动安装。发布资产和开发源码分别审查。
