using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Timers;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace PhotoFrame
{
    public partial class MainPage : ContentPage
    {
        private readonly GooglePhotosService _photosService = new GooglePhotosService();
        private List<string> _localPhotoPaths = new List<string>();
        private int _currentPhotoIndex = 0;
        private System.Timers.Timer? _slideshowTimer;

        public MainPage()
        {
            InitializeComponent();
            SetupSlideshowTimer();
            LoadPhotosFromCache();
        }

        private async void OnLoginAndSyncClicked(object sender, EventArgs e)
        {
            StatusLabel.Text = "Открытие браузера для авторизации...";
            SyncButton.IsEnabled = false;

            bool isAuthenticated = await _photosService.AuthenticateAsync();
            if (isAuthenticated)
            {
                StatusLabel.Text = "Авторизация успешна! Скачивание альбома...";

                // Вставьте сюда ID альбома, полученный через Google API Explorer Sandbox
                string myAlbumId = "ВАШ_GOOGLE_PHOTOS_ALBUM_ID";

                var urls = await _photosService.GetPhotoUrlsFromAlbumAsync(myAlbumId);

                if (urls != null && urls.Count > 0)
                {
                    StatusLabel.Text = $"Найдено {urls.Count} фото. Сохранение на рамку...";
                    await _photosService.DownloadPhotosLocallyAsync(urls);

                    LoadPhotosFromCache();
                    StatusLabel.Text = $"Синхронизировано! Всего фото: {_localPhotoPaths.Count}";
                }
                else
                {
                    StatusLabel.Text = "Альбом пуст или не найден.";
                }
            }
            else
            {
                StatusLabel.Text = "Ошибка авторизации.";
            }

            SyncButton.IsEnabled = true;
        }

        private void LoadPhotosFromCache()
        {
            string appDataDir = FileSystem.AppDataDirectory;
            _localPhotoPaths = Directory.GetFiles(appDataDir, "*.jpg").ToList();

            if (_localPhotoPaths.Count > 0)
            {
                _slideshowTimer.Start();
                ShowNextPhoto();
            }
        }

        private void SetupSlideshowTimer()
        {
            // Интервал показа одной фотографии: 10000 мс = 10 секунд
            _slideshowTimer = new System.Timers.Timer(10000);
            _slideshowTimer.Elapsed += OnTimerElapsed;
            _slideshowTimer.AutoReset = true;
        }

        private void OnTimerElapsed(object sender, ElapsedEventArgs e)
        {
            // Перенаправляем отрисовку картинки в главный UI-поток Android
            MainThread.BeginInvokeOnMainThread(ShowNextPhoto);
        }

        private void ShowNextPhoto()
        {
            if (_localPhotoPaths.Count == 0) return;

            if (_currentPhotoIndex >= _localPhotoPaths.Count)
                _currentPhotoIndex = 0;

            SlideshowImage.Source = ImageSource.FromFile(_localPhotoPaths[_currentPhotoIndex]);
            _currentPhotoIndex++;
        }
    }
}
