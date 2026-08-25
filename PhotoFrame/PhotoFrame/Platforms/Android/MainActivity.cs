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
        /// <summary>Пакет предустановленного Frameo — тот самый лаунчер, что переставляет пояс.</summary>
        private const string FrameoPackage = "net.frameo.frame";

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            KillFrameoIfRunning();
        }

        /// <summary>
        /// Frameo лаунчером больше не становится, но процесс всё равно поднимается фоном
        /// и спустя секунды после старта переставляет часовой пояс на свой — так и терялась
        /// ручная правка на Europe/Minsk. Убиваем его при каждом своём запуске.
        /// </summary>
        private void KillFrameoIfRunning()
        {
            try
            {
                if (GetSystemService(ActivityService) is ActivityManager manager)
                {
                    manager.KillBackgroundProcesses(FrameoPackage);
                }
            }
            catch (Java.Lang.Throwable killFailure)
            {
                // Пакета может не быть на прошивке вовсе — тогда и убивать нечего.
                FrameLog.Warn($"Не удалось остановить Frameo: {killFailure.Message}");
            }
        }
    }
}
