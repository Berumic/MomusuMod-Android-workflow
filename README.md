# MonsterMusumeTD Android 自动打包

上传原版游戏 APK 到 GitHub Release，然后在 Actions 点击运行，生成包含 LemonLoader、汉化插件、字体和最新翻译的签名 APK。使用 GitHub 托管 Windows runner，无需本地电脑一直开机，也不需要 DMM 下载接口。

## 首次配置

### 1. 上传源码

将本目录源码上传到自己仓库的默认分支，必须包含隐藏目录 `.github`。也可使用 `release-assets/workflow-source.zip` 中的源码。

不要把整个目录直接拖到网页上传：`.venv`、`work`、`dist`、`release-assets` 已被 `.gitignore` 排除，Git 推送会自动忽略它们。原版 APK 和依赖 ZIP 使用 Release 附件，不放进 Git。

### 2. 上传依赖包（一次）

在 **Releases → Draft a new release** 创建标签 `build-tools-v1`，上传本地 `release-assets/android-toolchain-v1.zip`，然后发布。标签和文件名必须一致；它不会触发游戏打包。

依赖包约 142 MiB，SHA256：

```text
580721eb3bafccbfa02dc9add6f9df3d69e7fca57f9ca944d6df0dd9e0c90b0b
```

包含 LemonLoader patcher/runtime、Cpp2IL、Il2CppInterop、Unity 托管库、修正后的安卓字体和 Android build-tools 35，不包含原版 APK、签名私钥或抓包环境。附带工具原有许可证；各第三方组件及字体的许可分别适用。

### 3. 配置固定签名

在 **Settings → Secrets and variables → Actions** 添加四个 Repository secrets：

| Secret | 内容 |
| --- | --- |
| APK_KEYSTORE_BASE64 | 签名密钥文件的 Base64 |
| APK_KEY_ALIAS | 密钥 alias |
| APK_STORE_PASSWORD | 密钥库密码 |
| APK_KEY_PASSWORD | 私钥密码 |

覆盖已安装测试汉化版时，继续使用 `D:\mms\MomusuMod-Android\Signing\monmusu-test.p12`，alias 为 `monmusu-test`。密钥和密码没有放进此项目。换密钥后不能覆盖旧汉化版；Mod 签名也不能覆盖官方签名原版。

推荐安装 GitHub CLI，运行 `gh auth login`，然后在 PowerShell 7 执行助手，直接设置 Secrets，不生成密钥明文文件：

```powershell
./ci/Set-SigningSecrets.ps1 -Repository "你的用户名/你的仓库" -Keystore "D:\mms\MomusuMod-Android\Signing\monmusu-test.p12" -KeyAlias "monmusu-test"
```

助手提示输入密钥库密码和私钥密码。手动配置时，可用下列命令将 Base64 复制到剪贴板，粘贴至 GitHub Secret 输入框：

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('D:\mms\MomusuMod-Android\Signing\monmusu-test.p12')) | Set-Clipboard
```

启用 GitHub Actions。工作流申请 `contents: write` 发布产物；若组织策略禁止，需要管理员允许。

## 每次打包

### 手动一键运行

1. 创建 Release，例如标签 `game-input`，上传完整原版 `.apk` 附件并发布。
2. 进入 **Actions → Build Mod APK → Run workflow**。
3. `apk_release_tag` 填 `game-input`；只有一个 APK 时 `apk_asset_name` 留空。
4. 勾选 `publish_release` 并运行；取消勾选则只保存 Actions artifact。
5. 成功后在新建的 `mod-*` Release 下载签名 APK，或下载 Actions 页的 `signed-mod-apk-*` 产物。

Run workflow 表单不支持上传文件，所以这里用 Release 附件作为 APK 输入。

### 上传后自动运行

创建标签以 `apk-` 开头的 Release，例如 `apk-174`，**先上传且只上传一个 APK，再点击 Publish release**，即自动构建发布。

已发布 Release 再追加或替换附件不会自动触发，请手动 Run workflow。输出使用 `mod-*` 标签，不会循环触发。

## 构建检查和限制

- 包名允许 `com.dmm.dmmgames.monmusutd` 和 `jp.co.dmm.fanzagames.monmusutdx`，继续检查 ARM64 IL2CPP 文件和 Unity 版本，不支持缺少资源的 split APK。允许包名不代表已经完成该版本的实机兼容验证。
- 按上传 APK 生成安卓 Interop，再编译插件并运行回归检查。
- 拉取 `Berumic/MonsterMusumeTDChineseTranslation` 的 main，校验翻译 manifest 的大小和 SHA256，记录实际提交号。
- 进行 16 KiB 对齐、固定密钥签名、签名验证及 APK 部署文件逐项哈希校验。
- 产物为签名 APK、Mod 文件 ZIP、build-info.json 和 SHA256SUMS.txt。私钥在 finally 和 always 步骤清理，不上传工作目录。
- 发布先上传到草稿，成功后公开；失败时从 Actions 下载产物，并检查残留草稿。
- 当前固定 Unity `2022.3.62f2`；游戏升级 Unity 会明确报错，需要更新依赖及适配。支持 ARM64 手机。
- 不自动从 DMM 下载 APK，不包含版本轮询。

## 本地构建和维护

需要 Windows、PowerShell 7、.NET SDK 10、.NET 6 运行时和 Java 21。

入口 `ci/Build-Apk.ps1` 的必需参数：`-Apk`、`-Toolchain`（解压后的依赖目录）、`-TranslationsRoot`（汉化 Git 仓库）、`-Keystore`、`-KeyAlias`。可指定 `-Java`、`-Workspace`、`-Output`。工作目录必须全新，输出目录必须为空；密码取自环境变量 `APK_STORE_PASSWORD` 和 `APK_KEY_PASSWORD`。建议使用 ASCII 工作路径，避免旧 Android 工具的编码问题。

`ci/Make-Toolchain.ps1` 可从本机现有工具目录重建依赖。重建后使用新 Release 标签/附件名，并更新 `ci/toolchain.json` 的标签、文件名和 SHA256。Unity 变化时同步修改 Prepare/Build 脚本中的依赖库路径，不要覆盖旧版固定哈希的包。

GitHub 实际执行需要先配置仓库、依赖 Release 和 Secrets。本地验证不能代替 GitHub 权限与网络验证。
