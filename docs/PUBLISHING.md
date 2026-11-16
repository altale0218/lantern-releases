# GitHub 公开前检查

此目录是独立源码准备包。不要上传原工作区、现有 Windows 安装目录、work、outputs、应用数据目录或旧 Git 历史。

## 发布材料

- README、LICENSE、第三方声明、SECURITY、PRIVACY 与 CONTRIBUTING。
- 当前 src、ui、tests、tools，以及不含本地路径和凭据的项目/构建配置。
- CHANGELOG、构建/测试/发布说明、GitHub 问题与 PR 模板、只读检查工作流。

仓库名称建议 Lantern；简介：Windows 与 Android 的局域网认证加密文件传输和设备聊天工具。可选主题标签：android、windows、lan、file-transfer、dotnet、webview2、end-to-end-encryption。不需要填写维护者姓名、邮箱、公司、地址或测试手机信息。

## 上传前

1. 确认项目原创内容及第三方资源有权公开，并审核准备的 MIT 许可证选择；第三方许可另行保留。
2. 运行 tools/Check-PublicSource.ps1 -StrictExport（严格导出检查）。确保未包含签名、证书、应用状态、聊天/传输历史、真实截图/日志或编译产物。工具不代替人工审查。
3. 从本目录建立一个全新的仓库，不复用原工作区的 .git，不复制全局 Git 配置。
4. 在 GitHub 账户设置开启邮箱隐私，并使用平台提供的 noreply 地址作为公开提交邮箱；作者名称使用你愿意公开的项目署名或笔名。
5. 在提交前检查 git diff --cached 与暂存文件清单；有疑问的文件不要提交。gitignore 不会清除已经提交的历史。
6. 确认新仓库的账号名、归属和公开作者信息不会关联不希望公开的个人身份。可先使用私有仓库检查，再由维护者决定公开。

## 官方参考

- [GitHub 仓库许可](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)
- [GitHub 提交邮箱隐私](https://docs.github.com/en/account-and-profile/how-tos/email-preferences/setting-your-commit-email-address)
- [MIT 标准文本](https://opensource.org/license/mit)

本准备过程没有创建远程仓库、提交、推送或公开发布。

## 后续应用更新

在拥有独立开发源和公开镜像的工作区，使用 tools/Sync-PublicSource.ps1 -Source <开发源> -Destination <公开目录> 同步，再加 -Verify 校验。工具维护审核过的源码、UI、工程、测试和许可，保留公开文档，不复制构建产物或运行数据。发布前更新公开说明与虚构截图，再生成新的 SOURCE-MANIFEST.json 和唯一源码压缩包。
