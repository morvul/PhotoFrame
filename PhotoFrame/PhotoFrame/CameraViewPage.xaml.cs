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
    /// Снимки камер Home Assistant, обновляемые по таймеру.
    /// </summary>
    /// <remarks>
    /// Загружает снимок камеры и обновляет его каждую ~1.2 секунды. Можно переключаться между камерами.
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

            PlayCurrentCamera();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            StopSnapshotPolling();
        }

        private void PlayCurrentCamera()
        {
            string entityId = _cameraEntityIds[_cameraIndex];

            HeaderLabel.Text = $"Камера ({_cameraIndex + 1}/{_cameraEntityIds.Count}) — {entityId}";

            StartSnapshotPolling(entityId);
        }

        private void StartSnapshotPolling(string entityId)
        {
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

        private void OnNextCameraClicked(object? sender, EventArgs e)
        {
            if (_cameraEntityIds.Count == 0)
            {
                return;
            }

            _cameraIndex = (_cameraIndex + 1) % _cameraEntityIds.Count;
            PlayCurrentCamera();
        }

        private async void OnBackClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}
