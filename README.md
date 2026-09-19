# 如果你需要下载安装包
在release中寻找latest
下载 monmusutdx-vxxx-mod-xxx.apk

# MonsterMusumeTD Android 自动打包

上传原版游戏 APK 到 GitHub Release，工作流生成包含 LemonLoader、汉化插件、字体和最新翻译的独立包名 APK。支持与原版共存，使用 GitHub 托管 Windows runner，无需本地电脑一直开机，也不需要 DMM 下载接口。

## 独立包名与发布标签

| game | 原版包名 | Mod 包名 | 发布标签示例 |
| --- | --- | --- | --- |
| monmusutd | com.dmm.dmmgames.monmusutd | com.dmm.dmmgames.monmusutd.mod | mod-monmusutd-v174 |
| monmusutdx | jp.co.dmm.fanzagames.monmusutdx | jp.co.dmm.fanzagames.monmusutdx.mod | mod-monmusutdx-v174 |

目标映射在 `ci/targets.json`。发布标签固定为 `mod-<game>-v<source_version>`。此流程没有接入 DMM Store API，`source_version` 直接读取上传 APK 的 Android `versionCode`，不使用显示版本 `versionName`，也不使用插件版本。

同一游戏原版版本的 Mod 更新会替换该 Release 下的产物，不增加流水号标签；新的原版版本生成新的标签。Git tag 保留创建时的提交，实际构建提交和指纹以 Release 说明、`build-info.json` 为准。

独立版会修改安装包名、Provider authorities、自定义权限及其资源/Smali 字符串引用，保留 Java 类名和 DMM 登录 URI schemes。原版与 Mod 的 Android 应用数据分开，旧汉化版的数据不会自动迁移。登录回调可能让系统弹出应用选择器；DMM 登录、支付、包名校验及游戏启动仍需实机测试，不能仅凭构建成功保证兼容。

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

独立包名步骤另外下载 Apktool 2.12.1，下载地址和 SHA256 固定在 `ci/toolchain.json`。已有 `build-tools-v1` ZIP 无需重新上传。

### 3. 配置固定签名

在 **Settings → Secrets and variables → Actions** 添加四个 Repository secrets：

| Secret | 内容 |
| --- | --- |
| APK_KEYSTORE_BASE64 | 签名密钥文件的 Base64 |
| APK_KEY_ALIAS | 密钥 alias |
| APK_STORE_PASSWORD | 密钥库密码 |
| APK_KEY_PASSWORD | 私钥密码 |

继续使用 `D:\mms\MomusuMod-Android\Signing\monmusu-test.p12`，alias 为 `monmusu-test`，四个 Secrets 不变。密钥和密码没有放进此项目。首次安装独立包名版是新增应用，不会覆盖原版或之前保留原包名的汉化版；后续相同独立包名、相同签名的 Mod 可以覆盖更新。

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
3. `apk_release_tag` 填 `game-input`；只有一个 APK 时 `apk_asset_name` 留空。标签留空时自动选择每个游戏最新的标准命名原版 Release。
4. 勾选 `publish_release` 并运行；取消勾选则强制构建且只保存 Actions artifact。需要重新构建已有相同输入时，勾选 `force`。
5. 成功后在带有 GitHub `Latest` 标记的 `mod-<game>-v<versionCode>` Release 下载签名 APK，或下载 Actions 页对应产物。输入未改变时自动跳过，不生成新产物。

Run workflow 表单不支持上传文件，所以这里用 Release 附件作为 APK 输入。

### 上传后自动运行

推荐使用 `apk-<game>-v<versionCode>` 标签，例如普通版 `apk-monmusutd-v174`、FANZA 版 `apk-monmusutdx-v174`。其中数字必须与附件 APK 的 versionCode 一致。**先上传且只上传一个 APK，再点击 Publish release**，自动构建对应游戏目标。

触发方式：

- 发布或编辑 `apk-*` Release：检查该输入并构建对应目标。
- 向默认分支推送插件源码或构建配方（Android/Core/Services/Patches/ci/工作流）：检查每个游戏的最新输入。
- 每 6 小时检查一次：检测最新原版 Release 的 APK 附件替换和远端汉化清单更新。GitHub 定时执行可能延迟。
- 手动 Run workflow，或发送 `repository_dispatch` 的 `mod-input-updated` 事件。

只替换 Release 附件不一定立即产生 Release 事件，定时检查会补上，也可手动运行。输出标签使用 `mod-*`，不会循环触发；构建成功后该 Mod Release 会被标记为仓库 Latest。GitHub 每个仓库只有一个 Latest 标记，多游戏目标时最后完成的构建会成为 Latest。没有上传原版时，自动检查会跳过。

自动选择按各游戏的原版 versionCode 数值选最新版本。老的 `apk-174` 标签仍可手动指定；仓库完全没有标准命名输入时，也会回退检查最近发布的 `apk-*` Release。多版本并存时建议统一标准命名，旧版本需要手动指定重建。

构建指纹包含原 APK 字节、Mod 源码、构建脚本/配置、当前目标包名配置、固定依赖校验值和翻译清单。插件部署包在 CI 中根据这些输入生成；输入变更才重建，无变化跳过。本地 `D:\mms\MomusuMod-Android\Build` 的 DLL 不会被自动同步，插件源码更新仍须推送到这个仓库。字体更新要更新依赖包及配置中的 SHA256。

## 构建检查和限制

- 包名允许 `com.dmm.dmmgames.monmusutd` 和 `jp.co.dmm.fanzagames.monmusutdx`，继续检查 ARM64 IL2CPP 文件和 Unity 版本，不支持缺少资源的 split APK。允许包名不代表已经完成该版本的实机兼容验证。
- 按上传 APK 生成安卓 Interop，再编译插件并运行回归检查。
- 拉取 `Berumic/MonsterMusumeTDChineseTranslation` 的 main，校验翻译 manifest 的大小和 SHA256，记录实际提交号。
- 重建独立包名后，进行 16 KiB 对齐、固定密钥签名、签名验证、最终包名/版本/Provider 检查及 APK 部署文件逐项哈希校验。
- 产物为签名 APK、Mod 文件 ZIP、identity.json、build-info.json 和 SHA256SUMS.txt。私钥在 finally 和 always 步骤清理，不上传工作目录。
- 发布先上传到草稿，成功后公开。更新已有 Release 时会暂时转为草稿；失败时从 Actions 下载产物，重新运行即可修复未完成草稿。
- 当前固定 Unity `2022.3.62f2`；游戏升级 Unity 会明确报错，需要更新依赖及适配。支持 ARM64 手机。
- 不自动从 DMM 下载 APK；定时检查的是你自己仓库中上传的 Release 附件。

## 本地构建和维护

需要 Windows、PowerShell 7、Python 3.9+、.NET SDK 10、.NET 6 运行时、Java 21，以及与配置哈希匹配的 Apktool JAR。

入口 `ci/Build-Apk.ps1` 的必需参数：`-Apk`、`-Toolchain`（解压后的依赖目录）、`-TranslationsRoot`（汉化 Git 仓库）、`-Keystore`、`-KeyAlias`、`-Apktool`（JAR 路径）。可指定 `-ExpectedGame`、`-SourceReleaseTag`、`-Java`、`-Workspace`、`-Output`。工作目录必须全新，输出目录必须为空；密码取自环境变量 `APK_STORE_PASSWORD` 和 `APK_KEY_PASSWORD`。建议使用 ASCII 工作路径，避免旧 Android 工具的编码问题。

`ci/Make-Toolchain.ps1` 可从本机现有工具目录重建依赖。重建后使用新 Release 标签/附件名，并更新 `ci/toolchain.json` 的标签、文件名和 SHA256。Unity 变化时同步修改 Prepare/Build 脚本中的依赖库路径，不要覆盖旧版固定哈希的包。

GitHub 实际执行需要先配置仓库、依赖 Release 和 Secrets。本地验证不能代替 GitHub 权限与网络验证。
