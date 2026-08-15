using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace PhotoFrame
{
    /// <summary>
    /// Экран слайд-шоу и заодно домашний экран рамки.
    /// </summary>
    /// <remarks>
    /// Категория HOME добавлена, чтобы рамка включалась сразу в показ. Своего лаунчера
    /// в прошивке нет — его роль играло приложение Frameo, а оно вдобавок переставляло
    /// часовой пояс на свой (проверено: через одиннадцать секунд после старта пояс
    /// менялся с выбранного на UTC+2) и занимало 230 МБ из 976 МБ памяти устройства.
    /// С этой категорией Frameo можно отключить, ничего не потеряв.
    /// </remarks>
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter(
        new[] { Intent.ActionMain },
        Categories = new[] { Intent.CategoryHome, Intent.CategoryDefault })]
    public class MainActivity : MauiAppCompatActivity
    {
    }
}
