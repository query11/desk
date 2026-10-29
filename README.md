# Desk

Windows 11 桌面美化工具（开发中）。第一阶段做两件事：

- **浮岛式任务栏**：居中的应用岛和右侧的托盘岛，背景实时模糊，空白处露出壁纸；
- **毛玻璃开始菜单**：新版开始菜单的背景换成可调强度的实时模糊。

原生功能（托盘图标、输入法、音量/网络弹窗、跳转列表、拖放等）都保留，Desk 只修改它们的外观。
完整的计划和设计说明见 [docs/PLAN.md](docs/PLAN.md)。

## 使用

1. 在 [Actions](https://github.com/query11/desk/actions) 里打开最新一次成功的 build，下载 `Desk-…` 压缩包并解压。
2. 运行 `Desk.exe`，托盘里会出现 Desk 图标，任务栏和开始菜单会在几秒内变成新样式。
3. 右键托盘图标可以：
   - 切换主题；
   - 选择窗口最大化时的表现（恢复成整条任务栏 / 自动隐藏 / 保持浮岛）；
   - 开关开始菜单美化和点击穿透；
   - 设置开机自动启动；
   - 退出并还原。

**出问题时**：

- 按 `Ctrl + Alt + Shift + D` 可以立即停用美化，再按一次恢复。
- 资源管理器如果在短时间内反复重启，Desk 会自动停用美化。
- 日志在 `%LOCALAPPDATA%\Desk\logs`，托盘菜单里也能直接打开。

## 自定义主题

主题是 JSON 文件，放在 `%APPDATA%\Desk\themes` 里（托盘菜单「打开主题文件夹」）。保存后会立即生效。
规则写法兼容 Windhawk「Windows 11 Taskbar Styler」的选择器语法，示例见 [themes/desk-islands.json](themes/desk-islands.json)。

## 构建

推送后由 GitHub Actions 在 Windows 上编译。在本地构建需要 Visual Studio（含 C++ 桌面开发组件和 vcpkg）以及 .NET 10 SDK：

```powershell
cmake -S src/DeskTap -B build/DeskTap -A x64 -DCMAKE_TOOLCHAIN_FILE="$env:VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake" -DVCPKG_TARGET_TRIPLET=x64-windows-static
cmake --build build/DeskTap --config Release
dotnet publish src/Desk/Desk.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/Desk
copy build\DeskTap\Release\DeskTap.dll dist\Desk\
xcopy /E /I themes dist\Desk\themes
```

## 致谢与许可

样式引擎的做法和部分代码来自 [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB) 和
[Windhawk](https://github.com/ramensoftware/windhawk) 的 [Windows 11 Taskbar Styler](https://github.com/ramensoftware/windhawk-mods)，
它们都采用 GPL-3.0 许可。Desk 同样以 [GPL-3.0](LICENSE) 发布。
