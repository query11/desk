namespace Desk;

internal sealed record ThemeTrial(string OriginalTheme, string TrialTheme, DateTime ExpiresUtc);

/// <summary>A persisted rollback journal; the host owns the timer, independently of settings windows.</summary>
internal sealed class ThemeTrialController(Settings settings, Func<bool> save, Action apply, TimeProvider clock)
{
    public event Action? Changed;
    public ThemeTrial? Pending => settings.PendingThemeTrial;
    public int SecondsLeft => Pending is { } trial ? Math.Max(0, (int)Math.Ceiling((trial.ExpiresUtc - clock.GetUtcNow().UtcDateTime).TotalSeconds)) : 0;

    public static bool RecoverInterrupted(Settings settings)
    {
        if (settings.PendingThemeTrial is not { } trial) return false;
        settings.Theme = trial.OriginalTheme;
        settings.PendingThemeTrial = null;
        return true;
    }

    public void Begin(string id)
    {
        if (id == settings.Theme && Pending == null) return;
        string original = Pending?.OriginalTheme ?? settings.Theme;
        Change(id, new ThemeTrial(original, id, clock.GetUtcNow().UtcDateTime.AddSeconds(15)));
    }

    public void Keep()
    {
        if (Pending == null) return;
        if (SecondsLeft == 0) { Cancel(); return; }
        Change(settings.Theme, null);
    }

    public void Cancel()
    {
        if (Pending is { } trial) Change(trial.OriginalTheme, null);
    }

    public void Tick()
    {
        if (Pending == null) return;
        if (SecondsLeft == 0) Cancel();
        else Changed?.Invoke();
    }

    public void Select(string id) => Change(id, null);

    private void Change(string id, ThemeTrial? pending)
    {
        string previous = settings.Theme;
        var previousTrial = Pending;
        settings.Theme = id;
        settings.PendingThemeTrial = pending;
        if (!save())
        {
            settings.Theme = previous;
            settings.PendingThemeTrial = previousTrial;
            throw new IOException("无法保存设置，原来的外观已保留。");
        }
        apply();
        Changed?.Invoke();
    }
}
