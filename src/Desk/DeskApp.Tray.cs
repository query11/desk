using System.Drawing;
using System.Windows.Forms;

namespace Desk;

/// <summary>The tray icon's menu.</summary>
internal sealed partial class DeskApp
{
    private ToolStripMenuItem _enabledItem = null!;
    private ToolStripMenuItem _themeMenu = null!;
    private ToolStripMenuItem _maximizeMerge = null!;
    private ToolStripMenuItem _maximizeAutoHide = null!;
    private ToolStripMenuItem _maximizeKeep = null!;
    private ToolStripMenuItem _startMenuItem = null!;

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("打开 Desk", null, (_, _) => ShowWindow());
        open.Font = new Font(open.Font, FontStyle.Bold);
        _enabledItem = new ToolStripMenuItem("启用美化", null, (_, _) => Enabled = !Enabled);
        _themeMenu = new ToolStripMenuItem("主题");

        var maximizeMenu = new ToolStripMenuItem("窗口最大化时");
        _maximizeMerge = new ToolStripMenuItem("恢复成整条任务栏", null, (_, _) => MaximizeMode = MaximizeMode.Merge);
        _maximizeAutoHide = new ToolStripMenuItem("自动隐藏任务栏", null, (_, _) => MaximizeMode = MaximizeMode.AutoHide);
        _maximizeKeep = new ToolStripMenuItem("保持浮岛", null, (_, _) => MaximizeMode = MaximizeMode.Keep);
        maximizeMenu.DropDownItems.AddRange([_maximizeMerge, _maximizeAutoHide, _maximizeKeep]);

        _startMenuItem = new ToolStripMenuItem("美化开始菜单", null, (_, _) => StyleStartMenu = !StyleStartMenu);

        menu.Items.AddRange([
            open,
            new ToolStripSeparator(),
            _enabledItem,
            _themeMenu,
            maximizeMenu,
            _startMenuItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("重新应用", null, (_, _) => Reapply()),
            new ToolStripMenuItem("重启资源管理器", null, (_, _) => RestartExplorer()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("退出并还原", null, (_, _) => Exit()),
        ]);

        menu.Opening += (_, _) => RefreshMenu();
        return menu;
    }

    private void RefreshMenu()
    {
        _enabledItem.Checked = _settings.Enabled;
        _maximizeMerge.Checked = _settings.MaximizeMode == MaximizeMode.Merge;
        _maximizeAutoHide.Checked = _settings.MaximizeMode == MaximizeMode.AutoHide;
        _maximizeKeep.Checked = _settings.MaximizeMode == MaximizeMode.Keep;
        _startMenuItem.Checked = _settings.StyleStartMenu;

        _themeMenu.DropDownItems.Clear();
        foreach (var theme in GetThemes())
        {
            string id = theme.Id;
            var item = new ToolStripMenuItem(theme.Name, null, (_, _) => ThemeId = id)
            {
                Checked = string.Equals(ThemeId, id, StringComparison.OrdinalIgnoreCase),
            };
            _themeMenu.DropDownItems.Add(item);
        }
        if (_themeMenu.DropDownItems.Count == 0)
        {
            _themeMenu.DropDownItems.Add(new ToolStripMenuItem("（没有找到主题）") { Enabled = false });
        }
    }
}
