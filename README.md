# Desk

Windows 11 桌面美化工具（开发中）。第一阶段做两件事：

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
   - 切换主题，用滑块调整浮岛和开始菜单的圆角、模糊、着色、亮度、饱和度和颗粒感（实时生效）。
     调整按主题分别保存，切换主题时各用各的；「恢复默认」只恢复当前主题、当前页面；
   - 开关左侧信息岛，选择显示网速、CPU、内存中的哪几项；
   - 选择开始菜单的配色（跟随系统、深色玻璃、浅色玻璃）、窗口最大化时的表现、空白处点击穿透；
   - 开关开机自动启动（默认打开，登录后立即在后台启动，不弹窗口），开关下方显示实际设置的情况；
   - 「导出诊断」：把版本、系统、显示器、引擎状态、最近的错误和 Desk 的日志打包成 zip，反馈问题时用。
4. 关闭窗口后 Desk 留在托盘里。单击托盘图标或再次运行 `Desk.exe` 可以重新打开窗口，右键托盘图标可以退出并还原。

**出问题时**：

- 按 `Ctrl + Alt + Shift + D` 可以立即停用美化，再按一次恢复。
- 资源管理器如果在短时间内反复重启，Desk 会自动停用美化。
- Desk 崩溃或被结束时，任务栏和开始菜单会自动恢复原样（引擎发现 Desk 不在了）；再运行 `Desk.exe` 会重新美化。
- 设置窗口打不开、Desk 卡住时，运行 `Desk.exe --restore`：它会让正在运行的 Desk 退出（卡住的会被结束），
  把任务栏、开始菜单和 Desk 改过的「自动隐藏」恢复原样，并把美化保存为停用。加 `--quiet` 不弹窗。
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
dotnet test tests/Desk.Tests
dotnet publish src/Desk/Desk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/Desk
copy build\DeskTap\Release\DeskTap.dll dist\Desk\
xcopy /E /I themes dist\Desk\themes
```

`tests/Desk.Tests` 和引擎的 `src/DeskTap/tests/core_tests.cpp` 不依赖 Windows 桌面，在 Linux 上也能运行（见 [docs/TESTING.md](docs/TESTING.md) 第 16 节）。

## 致谢与许可

样式引擎的做法和部分代码来自 [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB) 和
[Windhawk](https://github.com/ramensoftware/windhawk) 的 [Windows 11 Taskbar Styler](https://github.com/ramensoftware/windhawk-mods)，
它们都采用 GPL-3.0 许可。设置窗口使用 [WPF UI](https://github.com/lepoco/wpfui)（MIT）。Desk 以 [GPL-3.0](LICENSE) 发布。
