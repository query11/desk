# Desk 0.5.0：界面与成套外观

## 设计与实现

沿用 .NET 10 WPF 与 WPF-UI 4.3.0，没有新增运行时依赖。使用现有 Fluent 导航、按钮、对话框、通知和图标。
页面采用统一的 28 px 标题、13 px 说明、8 px 控件间距、12/16 px 卡片圆角，颜色跟随 Windows 浅深色模式。

- 概览：当前方案示意、总开关、任务栏与开始菜单真实状态；维护操作收进折叠区。
- 外观方案：推荐、全部、我的方案；搜索、浅深色示意、应用反馈与撤销切换。
- 任务栏/开始菜单：保留独立参数、材质预设、单项重置、撤销和保存个人方案。
- 设置：保留开机启动、日志、主题目录与诊断导出，以及已有加载和失败反馈。
- 窗口变窄后导航收为图标；图库在内容宽度 620 px 以下变成单列，长内容滚动。

图库图片由本地 WPF 矢量绘制，读取主题与已保存的参数；是布局和材质的近似示意，并非真实桌面截图。
预览配色切换不会更改 Windows 配色。实际菜单结构仍由 Windows 提供。

## 新的成套预设

| 方案 | 任务栏 | 开始菜单 | 适合 |
| --- | --- | --- | --- |
| 通透浮岛 | 14 px 圆角，4 px 留白，低浓度玻璃 | 20 px 圆角，18 模糊，浅色着色 12% | 展示壁纸、日常使用 |
| 柔雾白瓷 | 16 px 圆角，5 px 留白，柔暖雾面 | 24 px 圆角，38 模糊，浅色着色 58% | 复杂或鲜艳壁纸 |
| 暮色蓝调 | 10 px 圆角，蓝灰着色 | 18 px 圆角，30 模糊，深色着色 50% | 喜欢沉静配色 |
| 专注横栏 | 原生整条布局，较高遮盖度 | 12 px 圆角，32 模糊，浅色着色 68% | 工作、文字辨识 |

四套均有浅色/深色参数，默认 28 px 图标、48 px 按钮间距。旧的三套主题继续放在“全部”中。
升级沿用原来的主题和微调，不自动套用新预设。切换后返回原方案，会恢复该方案自己的微调。
保存个人方案时包含任务栏和开始菜单的浅深色参数；个人方案不会进入推荐列表。

## 联网参考（2026-11-10）

| 一手来源 | 核实到的设计或功能 | 本版借鉴 |
| --- | --- | --- |
| [TranslucentTB 官方项目](https://github.com/TranslucentTB/TranslucentTB) | Clear / Acrylic 等状态；根据最大化、开始菜单等场景切换外观 | 透明与易读两端明确可选 |
| [Windhawk 任务栏主题指南](https://github.com/ramensoftware/windows-11-taskbar-styling-guide) | 大量可组合主题及控件级样式 | 用成套方案呈现布局与材质 |
| [DockLike](https://github.com/ramensoftware/windows-11-taskbar-styling-guide/blob/main/Themes/DockLike/README.md) | Dock 式任务栏、布局及浮起间距说明 | 浮岛、圆角和克制留白 |
| [TranslucentStartMenu](https://github.com/ramensoftware/windows-11-start-menu-styling-guide/blob/main/Themes/TranslucentStartMenu/README.md) | 通透亚克力背景；新旧开始菜单的样式规则 | 任务栏与菜单材质成套匹配 |
| [Start11 官方产品页](https://www.stardock.com/products/start11/) | 外观图库、配色/透明度/尺寸调整、Taskbar Islands | 图库先看效果，再显式应用 |
| [StartAllBack 官方产品页](https://www.startallback.com/) | 图标尺寸和边距、任务栏分段、动态透明 | 尺寸与间距独立，提供专注横栏 |

没有安装同类软件、复制其截图资产或移植替代开始菜单的引擎。本版主题基于 Desk 已验证的规则，
不承诺 Start11 的分组、搜索或 StartAllBack 的经典菜单重构功能。
TranslucentTB 对 Blur 支持有系统版本限制，不将它视为所有 Windows 11 构建均可用。

## 稳定性边界

复用 0.4.4 原生 DLL：保留开始菜单 `BackdropSource="Backdrop"`，不增加 TaskbarFrame 的动态 Margin/MaxWidth。
图库中无法解析的 JSON 文件会显示不可应用状态，应用前再次读取验证；保存设置失败时保留原方案。
“方案已切换”表示配置已更新，真实引擎生效状态仍以概览中的状态为准。

## 验证

- 123 项 .NET 测试通过，新增成套主题编译、Backdrop、布局限制、损坏文件和个人方案保存检查。
- 16 组独立 WPF 场景通过：8 组参数页、4 组图库、4 组整窗布局。覆盖浅深色、宽窄内容区，验证切换、撤销、搜索、错误与空状态。
- 截图输出：`build/ui-redesign`。测试程序不启动 Desk 宿主，不注入系统进程，也不改动用户或 Windows 设置。
- WPF 构建成功，无警告、无错误；没有更改原生引擎源码。

这些检查不代替不同 Windows 构建、多屏、各种缩放的实机兼容性测试。

## 本机升级验收

已运行 0.5.0 发布包，开机启动路径同步更新。
升级过程未重启 Explorer 或开始菜单进程；原生 DLL 哈希保持一致。
实际打开概览和图库，界面可正常导航；通过界面应用“通透浮岛”后，两部分均回报 `applied`，
配置序号与应用序号一致，样式错误、材质回退和材质失败均为 0。
随后通过“撤销切换”回到原来的 `desk-islands`，逐项比对设置，只有开机启动路径发生变化。
实机截图为 `build/ui-redesign/home-live.png`、`gallery-live.png`；其余截图是隔离渲染测试。
