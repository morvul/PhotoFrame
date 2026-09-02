using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace PhotoFrame
{
    /// <summary>
    /// Живой поток камеры Home Assistant, со звуком. Можно переключаться между камерами.
    /// </summary>
    /// <remarks>
    /// Если живой поток не поднимается (см. <see cref="HomeAssistantClient.GetCameraStreamUrlAsync"/> —
    /// у облачных камер вроде Tuya подписанная ссылка RTSP может истечь раньше, чем до неё
    /// доберётся ffmpeg), страница сама переходит на резерв: снимок камеры, обновляемый по
    /// таймеру. Без звука и с задержкой около секунды, но не зависит от того же RTSP-адреса.
    /// </remarks>
    public partial class CameraViewPage : ContentPage
    {
        private const int SnapshotIntervalMilliseconds = 1200;

        private readonly HomeAssistantClient _client = new();

        private readonly System.Timers.Timer _snapshotTimer =
            new(SnapshotIntervalMilliseconds) { AutoReset = true };

        private List<string> _cameraEntityIds = new();

        private int _cameraIndex;

        private string? _snapshotEntityId;

        public CameraViewPage()
        {
            InitializeComponent();

            CameraPlayer.PlaybackFailed += OnPlaybackFailed;
            _snapshotTimer.Elapsed += OnSnapshotTimerElapsed;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (!HomeAssistantClient.IsConfigured)
            {
                ShowStatus("Не задан адрес и токен Home Assistant в secrets.props.");
                return;
            }

            ShowStatus("Поиск камер...");

            try
            {
                // Список запрашивается заново при каждом входе на страницу: список камер
                // мог измениться, а рамка не должна показывать камеру, которой уже нет.
                _cameraEntityIds = await _client.GetCameraEntityIdsAsync().ConfigureAwait(true);
            }
            catch (PhotoSourceException lookupFailure)
            {
                ShowStatus(lookupFailure.Message);
                return;
            }

            if (_cameraEntityIds.Count == 0)
            {
                ShowStatus("В Home Assistant нет ни одной камеры.");
                return;
            }

            NextCameraButton.IsVisible = _cameraEntityIds.Count > 1;
            _cameraIndex = 0;

            await PlayCurrentCameraAsync().ConfigureAwait(true);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            StopSnapshotPolling();

            // Отдаём декодер сразу: страница закрыта, а рамке скоро снова понадобится
            // ExoPlayer под слайд-шоу.
            CameraPlayer.Stop();
        }

        private async Task PlayCurrentCameraAsync()
        {
            string entityId = _cameraEntityIds[_cameraIndex];

            StopSnapshotPolling();
            SnapshotImage.IsVisible = false;
            CameraPlayer.Stop();
            HeaderLabel.Text = $"Камера ({_cameraIndex + 1}/{_cameraEntityIds.Count}) — {entityId}";
            ShowStatus("Подключение...");

            try
            {
                string streamUrl = await _client.GetCameraStreamUrlAsync(entityId).ConfigureAwait(true);

                CameraPlayer.IsMuted = false;
                CameraPlayer.SourcePath = streamUrl;
                CameraPlayer.Play();
                StatusLabel.IsVisible = false;
            }
            catch (PhotoSourceException streamFailure)
            {
                FrameLog.Warn(
                    $"{entityId}: живой поток не открылся ({streamFailure.Message}), "
                    + "переходим на снимки");
                StartSnapshotPolling(entityId);
            }
        }

        private void StartSnapshotPolling(string entityId)
        {
            CameraPlayer.Stop();
            SnapshotImage.IsVisible = true;
            ShowStatus("Загрузка снимка...");

            _snapshotEntityId = entityId;
            _snapshotTimer.Start();

            // Не ждать первого тика таймера — первый снимок нужен сразу.
            _ = RefreshSnapshotAsync(entityId);
        }

        private void StopSnapshotPolling()
        {
            _snapshotTimer.Stop();
            _snapshotEntityId = null;
        }

        private async Task RefreshSnapshotAsync(string entityId)
        {
            // Пока снимок ждали, могли переключить камеру или уйти со страницы —
            // устаревший ответ показывать не нужно.
            if (_snapshotEntityId != entityId)
            {
                return;
            }

            try
            {
                byte[] jpegBytes = await _client.GetCameraSnapshotAsync(entityId).ConfigureAwait(true);

                if (_snapshotEntityId != entityId)
                {
                    return;
                }

                SnapshotImage.Source = ImageSource.FromStream(() => new MemoryStream(jpegBytes));
                StatusLabel.IsVisible = false;
            }
            catch (PhotoSourceException snapshotFailure)
            {
                if (_snapshotEntityId == entityId)
                {
                    ShowStatus($"{entityId}: {snapshotFailure.Message}");
                }
            }
        }

        private void OnSnapshotTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            string? entityId = _snapshotEntityId;
            if (entityId is null)
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() => _ = RefreshSnapshotAsync(entityId));
        }

        private void ShowStatus(string text)
        {
            StatusLabel.Text = text;
            StatusLabel.IsVisible = true;
        }

        private void OnPlaybackFailed(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                string entityId = _cameraEntityIds[_cameraIndex];
                FrameLog.Warn($"{entityId}: видео не воспроизвелось, переходим на снимки");
                StartSnapshotPolling(entityId);
            });
        }

        private async void OnNextCameraClicked(object? sender, EventArgs e)
        {
            if (_cameraEntityIds.Count == 0)
            {
                return;
            }

            _cameraIndex = (_cameraIndex + 1) % _cameraEntityIds.Count;
            await PlayCurrentCameraAsync().ConfigureAwait(true);
        }

        private async void OnBackClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}
