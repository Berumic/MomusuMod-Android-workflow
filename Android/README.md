# MonsterMusumeTD Android Mod 0.1.118

适用于本项目配套 ARM64 LemonLoader（.NET 10 / Il2CppInterop）和 Unity 2022.3.62f2。

部署包包含 Mods/MonsterMusumeTDMod.Android.dll、UserData/alimama-android 和汉化数据。合并到兼容加载器运行目录，不要放进 PC 的 BepInEx 或 Plugins 目录。首次启动日志中的 Android translations 显示数据位置。

支持剧情、选项、名字、UI、技能翻译，数字与任意文本变量、名称拆分、styles.json、UTAGE 富文本与停顿、增量更新，以及 R18 界面样式和马赛克处理。

0.1.118 修正安卓 TMP 描边、阴影变体和被裁剪 API 的调用。实际显示以设备测试为准。APK 更新刷新 DLL、升级未被用户修改的字体，保留个人配置和运行时翻译。

文本更新源为 Berumic/MonsterMusumeTDChineseTranslation 的 main/translations；字体源为 main/assets/android，远端未发布时使用内置字体。在线字体更新需要将构建目录 Publish/assets/android 的字体及 manifest.json 一并发布到该路径。

完整 GitHub 构建方法见仓库根目录 README.md。
