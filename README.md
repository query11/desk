# Desk

Windows 11 桌面美化工具（开发中），当前版本 **0.6.0**：

- **浮岛式任务栏**：居中的应用岛、右侧的托盘岛和左侧的信息岛（实时网速、CPU、内存），背景实时模糊，空白处露出壁纸；
- **毛玻璃开始菜单**：新版开始菜单的背景换成可调强度的实时模糊。

原生功能（托盘图标、输入法、音量/网络弹窗、跳转列表、拖放等）都保留，Desk 只修改它们的外观。
完整的计划和设计说明见 [docs/PLAN.md](docs/PLAN.md)。

## 使用

1. 在 [Actions](https://github.com/query11/desk/actions) 里打开最新一次成功的 build，下载 `Desk-…` 压缩包，解压到一个固定的文件夹。
2. 运行 `Desk.exe`：任务栏和开始菜单会在几秒内变成新样式，同时打开 Desk 的设置窗口。
3. 设置窗口里可以：
   - 一键启用/停用美化，查看任务栏和开始菜单是否已生效。「已生效」表示引擎确认当前设置已经应用到界面；
     其他状态会说明正在做什么（连接、初始化、等待开始菜单打开）或失败的原因和下次自动重试的时间；
   - 切换主题，选择「通透 / 均衡 / 清晰」材质预设并展开效果示意；分别调整图标大小、按钮间距、上下留白、圆角、模糊和透明程度，
     高级设置提供明暗、饱和度和颗粒感。修改实时生效，按主题保存；恢复默认和调整均支持会话内撤销；
   - 「外观方案」提供 7 套推荐和 3 套经典主题，支持当前壁纸预览、15 秒试用和自动还原；
   - 「保存为我的方案」将当前两页的浅/深色外观保存为用户主题；支持收藏、复制、重命名、删除、导入/导出和逐项还原微调；
   - 「显示器」为每块屏幕设置独立尺寸、材质、信息岛和最大化时的整条/浮岛行为；开始菜单与自动隐藏仍为全局设置；
   - 浮岛按屏幕与托盘实际空间自适应，拥挤时收紧间距、减少信息岛内容，空间恢复后自动还原；每个参数可单独重置；
   - 开关左侧信息岛，选择显示网速、CPU、内存中的哪几项；
   - 选择开始菜单的配色（跟随系统、深色玻璃、浅色玻璃）、窗口最大化时的表现、空白处点击穿透；
   - 开关开机自动启动（默认打开，登录后立即在后台启动，不弹窗口），开关下方显示实际设置的情况；
   - 「导出诊断」：把版本、系统、显示器、引擎状态、最近的错误和 Desk 的日志打包成 zip，反馈问题时用。
4. 关闭窗口后 Desk 留在托盘里。单击托盘图标或再次运行 `Desk.exe` 可以重新打开窗口，右键托盘图标可以退出并还原。

详细使用方法和兼容性范围见 [0.6.0 使用指南](docs/THEME-MANAGEMENT.md)。通透浮岛默认采用 10 px 圆角、4 px 边缘留白和均匀细描边；旧版本升级保留已保存的微调。

**出问题时**：

- 按 `Ctrl + Alt + Shift + D` 可以立即停用美化，再按一次恢复。
- 资源管理器如果在短时间内反复重启，Desk 会自动停用美化。
- Desk 崩溃或被结束时，任务栏和开始菜单会自动恢复原样（引擎发现 Desk 不在了）；再运行 `Desk.exe` 会重新美化。
  Desk 临时开启的「自动隐藏」会在再次启动 Desk 或运行下面的紧急还原命令时恢复。
- 设置窗口打不开、Desk 卡住时，运行 `Desk.exe --restore`：它会让正在运行的 Desk 退出（卡住的会被结束），
  把任务栏、开始菜单和 Desk 改过的「自动隐藏」恢复原样，并把美化保存为停用。加 `--quiet` 不弹窗。
  完整还原返回退出码 `0`；写配置、保存停用设置或恢复自动隐藏失败时返回 `1`，日志中会说明未完成的部分。
- 日志在 `%LOCALAPPDATA%\Desk\logs`（自动轮转，保留最近几份），设置窗口里也能直接打开或导出诊断。

## 自定义主题

主题是 JSON 文件，放在 `%APPDATA%\Desk\themes` 里（设置窗口 → 设置 →「主题文件夹」）。保存后会立即生效。
规则写法兼容 Windhawk「Windows 11 Taskbar Styler」的选择器语法，示例见 [themes/desk-islands.json](themes/desk-islands.json)。

## 构建

推送后由 GitHub Actions 在 Windows 上编译。在本地构建需要 Visual Studio（含 C++ 桌面开发组件和 vcpkg）以及 .NET 10 SDK：

```powershell
cmake -S src/DeskTap -B build/DeskTap -A x64 -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake" -DVCPKG_TARGET_TRIPLET=x64-windows-static
cmake --build build/DeskTap --config Release
build\DeskTap\Release\DeskTapCoreTests.exe
build\DeskTap\Release\DeskTapEffectsTests.exe
build\DeskTap\Release\DeskTapConfigTests.exe
dotnet test tests/Desk.Tests
dotnet publish src/Desk/Desk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/Desk
copy build\DeskTap\Release\DeskTap.dll dist\Desk\
xcopy /E /I themes dist\Desk\themes
```

`tests/Desk.Tests` 和引擎的 `src/DeskTap/tests/core_tests.cpp` 不依赖 Windows 桌面，在 Linux 上也能运行（见 [docs/TESTING.md](docs/TESTING.md) 第 16 节）。
`DeskTapEffectsTests` 在 Windows 上直接创建 Composition 效果，验证颗粒合成和模糊、着色的动态调整；不会加载进资源管理器。
`DeskTapConfigTests` 使用生产配置解析器验证独立显示器规则、缓存标识和宿主失联恢复。
`dotnet run --project tests/Desk.Ui.Tests -c Release` 独立验证新的外观控件并生成浅/深色、宽/窄截图，不修改用户设置。
设置窗口另有一个 UI 冒烟测试 `tests/ui-smoke.ps1`，每次构建都在 Windows 上运行，截图在构建的 `ui-smoke-…` 附件里。

## 致谢与许可

样式引擎的做法和部分代码来自 [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB) 和
[Windhawk](https://github.com/ramensoftware/windhawk) 的 [Windows 11 Taskbar Styler](https://github.com/ramensoftware/windhawk-mods)，
它们都采用 GPL-3.0 许可。设置窗口使用 [WPF UI](https://github.com/lepoco/wpfui)（MIT）。Desk 以 [GPL-3.0](LICENSE) 发布。
