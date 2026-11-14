using System.Drawing;
using System.Windows.Forms;

namespace Desk;

/// <summary>The tray icon's menu.</summary>
internal sealed partial class DeskApp
{
    private ContextMenuStrip _menu = null!;
    private ToolStripMenuItem _enabledItem = null!;
    private ToolStripMenuItem _themeMenu = null!;
    private ToolStripMenuItem _maximizeMenu = null!;
    private ToolStripMenuItem _maximizeMerge = null!;
    private ToolStripMenuItem _maximizeAutoHide = null!;
    private ToolStripMenuItem _maximizeKeep = null!;
    private ToolStripMenuItem _startMenuItem = null!;
    private ToolStripMenuItem _trialText = null!;
    private ToolStripMenuItem _trialKeep = null!;
    private ToolStripMenuItem _trialCancel = null!;
    private ToolStripSeparator _trialSeparator = null!;

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _menu = menu;

        var open = new ToolStripMenuItem("打开 Desk", null, (_, _) => ShowWindow());
        open.Font = new Font(open.Font, FontStyle.Bold);

        // A theme on trial: what it is, and both ways to end it, as at the
        // top of the settings window. Shown only while one runs.
        _trialText = new ToolStripMenuItem { Enabled = false };
        _trialKeep = new ToolStripMenuItem("保留这套", null, (_, _) => TrayAction(KeepThemeTrial));
        _trialCancel = new ToolStripMenuItem("立即还原", null, (_, _) => TrayAction(CancelThemeTrial));
        _trialSeparator = new ToolStripSeparator();

        _enabledItem = new ToolStripMenuItem("启用美化", null, (_, _) => Enabled = !Enabled);
        _themeMenu = new ToolStripMenuItem("主题");

        _maximizeMenu = new ToolStripMenuItem("窗口最大化时");
        _maximizeMerge = new ToolStripMenuItem("恢复成整条任务栏", null, (_, _) => MaximizeMode = MaximizeMode.Merge);
        _maximizeAutoHide = new ToolStripMenuItem("自动隐藏任务栏", null, (_, _) => MaximizeMode = MaximizeMode.AutoHide);
        _maximizeKeep = new ToolStripMenuItem("保持浮岛", null, (_, _) => MaximizeMode = MaximizeMode.Keep);
        _maximizeMenu.DropDownItems.AddRange([_maximizeMerge, _maximizeAutoHide, _maximizeKeep]);

        _startMenuItem = new ToolStripMenuItem("美化开始菜单", null, (_, _) => StyleStartMenu = !StyleStartMenu);

        menu.Items.AddRange([
            open,
            new ToolStripSeparator(),
            _trialText,
            _trialKeep,
            _trialCancel,
            _trialSeparator,
            _enabledItem,
            _themeMenu,
            _maximizeMenu,
            _startMenuItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("重新应用", null, (_, _) => Reapply()),
            new ToolStripMenuItem("重启资源管理器", null, (_, _) => RestartExplorer()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("退出并还原", null, (_, _) => Exit()),
        ]);

        menu.Opening += (_, _) => RefreshMenu();
        foreach (var dropDown in new ToolStripDropDown[] { menu, _themeMenu.DropDown, _maximizeMenu.DropDown })
        {
            dropDown.Opened += (_, _) => TrayMenuRenderer.RoundCorners(dropDown);
        }
        return menu;
    }

    private void RefreshMenu()
    {
        // The colors of the Windows mode, read again in case it changed.
        if ((_menu.Renderer as TrayMenuRenderer)?.Light != _lightMode)
        {
            var renderer = TrayMenuRenderer.For(_lightMode);
            _menu.Renderer = _themeMenu.DropDown.Renderer = _maximizeMenu.DropDown.Renderer = renderer;
        }

        _enabledItem.Checked = _settings.Enabled;
        _maximizeMerge.Checked = _settings.MaximizeMode == MaximizeMode.Merge;
        _maximizeAutoHide.Checked = _settings.MaximizeMode == MaximizeMode.AutoHide;
        _maximizeKeep.Checked = _settings.MaximizeMode == MaximizeMode.Keep;
        _startMenuItem.Checked = _settings.StyleStartMenu;

        var trial = PendingTrial;
        _trialText.Visible = _trialKeep.Visible = _trialCancel.Visible = _trialSeparator.Visible = trial != null;
        if (trial != null)
        {
            _trialText.Text = Escape($"正在试用「{ThemeName}」，{TrialSecondsLeft} 秒后还原");
        }

        _themeMenu.DropDownItems.Clear();
        IReadOnlyList<ThemeMenuItem?> items;
        try
        {
            items = ThemeMenu.Build(GetAppearanceOptions(), ThemeId, trial);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Theme list for the tray menu: {e.Message}");
            items = [];
        }
        foreach (var item in items)
        {
            if (item == null)
            {
                _themeMenu.DropDownItems.Add(new ToolStripSeparator());
                continue;
            }
            string id = item.Id;
            _themeMenu.DropDownItems.Add(new ToolStripMenuItem(Escape(item.Text), null, (_, _) => TrayAction(() => ThemeId = id))
            {
                Enabled = item.Enabled,
                Checked = item.Checked,
            });
        }
        if (_themeMenu.DropDownItems.Count == 0)
        {
            _themeMenu.DropDownItems.Add(new ToolStripMenuItem("（没有找到主题）") { Enabled = false });
        }
    }

    /// <summary>A theme's name as menu text: "&amp;" would underline the next letter.</summary>
    private static string Escape(string text) => text.Replace("&", "&&");

    /// <summary>A menu action that can fail (a theme file moved away): say so instead of failing silently.</summary>
    private void TrayAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Warn($"Tray menu action failed: {e.Message}");
            Notify("操作未完成", e.Message);
        }
    }
}
