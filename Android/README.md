# MonsterMusumeTD Android Mod 0.1.122

0.1.122 一次性将未迁移旧配置中的 UiTextFaceDilate=0.2 升级为 0.35，保留其他自定义值。迁移后可手动改回 0.2，不会再次覆盖。启动日志输出实际配置值；styles.json 明确指定的 faceDilate（包括 0）仍优先。

0.1.121 在未指定 fontColor 时继承原材质面色及其后续变化，取消额外套用按钮 disabledColor。文字颜色、渐变和淡出由游戏控制；显式路径样式与 0.35 默认增厚仍保留。不会复制旧字体图集。

0.1.120 修正按钮暗态刷新中的材质反复切换、阴影可见性不一致及重复覆盖文字颜色；切换字体时保留游戏当前的颜色和淡出状态。按钮暗态与恢复正常的实际效果仍需设备验证。

0.1.119 将通用 UI 字体增厚默认值与回退值统一为 0.35。已有配置会保留原值；旧安装需在 UserData/MonsterMusumeTDMod/config.json 中将 Translation.UIAppearance.UiTextFaceDilate 改为 0.35 后重启。匹配的 styles.json 规则仍优先生效。

适用于本项目配套 ARM64 LemonLoader（.NET 10 / Il2CppInterop）和 Unity 2022.3.62f2。

部署包包含 Mods/MonsterMusumeTDMod.Android.dll、UserData/alimama-android 和汉化数据。合并到兼容加载器运行目录，不要放进 PC 的 BepInEx 或 Plugins 目录。首次启动日志中的 Android translations 显示数据位置。

支持剧情、选项、名字、UI、技能翻译，数字与任意文本变量、名称拆分、styles.json、UTAGE 富文本与停顿、增量更新，以及 R18 界面样式和马赛克处理。

0.1.118 修正安卓 TMP 描边、阴影变体和被裁剪 API 的调用。实际显示以设备测试为准。APK 更新刷新 DLL、升级未被用户修改的字体，保留个人配置和运行时翻译。

文本更新源为 Berumic/MonsterMusumeTDChineseTranslation 的 main/translations；字体源为 main/assets/android，远端未发布时使用内置字体。在线字体更新需要将构建目录 Publish/assets/android 的字体及 manifest.json 一并发布到该路径。

完整 GitHub 构建方法见仓库根目录 README.md。
