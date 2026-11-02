using Xunit;

namespace KNE.Tests;

public sealed class WindowsEditorNotificationsTests
{
    private sealed class WindowsToastFactAttribute : FactAttribute
    {
        public WindowsToastFactAttribute() { if (Environment.GetEnvironmentVariable("KNE_TOAST_TEST") != "1") Skip = "Opt-in real Windows Action Center test (KNE_TOAST_TEST=1)."; }
    }
    [WindowsToastFact]
    public Task RealWindowsActionCenterAcceptsUnicodeToastWithoutKeepingEditorInBackground() => WorkspaceTabsTests.Sta(async () =>
    {
        using var delivery = new WindowsEditorNotifications(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string result = await delivery.ShowAsync(new("test", "Проверка KNE: резервные копии и автосохранение работают через настройки редактора."), new() { Toasts = true }, timeout.Token);
        Assert.Equal("Тост передан в Windows Action Center.", result);
        delivery.Dispose(); Assert.Equal("Закрыто", await delivery.ShowAsync(new("test", "Не отправлять после закрытия"), new(), timeout.Token));
    });
}
