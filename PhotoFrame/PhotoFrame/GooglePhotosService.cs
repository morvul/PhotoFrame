using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Maui.Authentication;
using Microsoft.Maui.Storage;

namespace PhotoFrame
{
    public class GooglePhotosService
    {
        private readonly HttpClient _httpClient = new HttpClient();
        private string _accessToken = string.Empty;

        // Ваш проверенный OAuth Client ID
        private const string ClientId = "://googleusercontent.com";
        private const string RedirectUri = "com.morvul.photoframe://oauth2redirect";

        // 1. Авторизация через системное веб-окно OAuth2
        public async Task<bool> AuthenticateAsync()
        {
            try
            {
                var authUrl = $"https://google.com?" +
                              $"client_id={ClientId}&" +
                              $"redirect_uri={Uri.EscapeDataString(RedirectUri)}&" +
                              $"response_type=token&" +
                              $"scope={Uri.EscapeDataString("https://googleapis.com")}";

                var callbackUrl = new Uri(RedirectUri);
                var authResult = await WebAuthenticator.Default.AuthenticateAsync(new Uri(authUrl), callbackUrl);

                if (authResult != null && authResult.Properties.ContainsKey("access_token"))
                {
                    _accessToken = authResult.Properties["access_token"];
                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 2. Получение списка прямых ссылок на изображения из альбома Google
        public async Task<List<string>> GetPhotoUrlsFromAlbumAsync(string albumId)
        {
            var urls = new List<string>();
            try
            {
                var requestBody = new { albumId = albumId, pageSize = 50 };
                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("https://googleapis.com", content);
                if (!response.IsSuccessStatusCode) return urls;

                var responseData = await response.Content.ReadAsStringAsync();
                using (var doc = JsonDocument.Parse(responseData))
                {
                    if (doc.RootElement.TryGetProperty("mediaItems", out var mediaItems))
                    {
                        foreach (var item in mediaItems.EnumerateArray())
                        {
                            string baseUrl = item.GetProperty("baseUrl").GetString();
                            if (!string.IsNullOrEmpty(baseUrl))
                            {
                                // Запрашиваем у Google оптимальное разрешение под экран рамки (1280x800)
                                urls.Add($"{baseUrl}=w1280-h800");
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Защита от сбоя сети
            }
            return urls;
        }

        // 3. Скачивание файлов на внутренний накопитель фоторамки
        public async Task DownloadPhotosLocallyAsync(List<string> urls)
        {
            string localFolder = FileSystem.AppDataDirectory;

            // Удаляем старые фотографии, чтобы не забивать флэш-память рамки
            foreach (var file in Directory.GetFiles(localFolder, "*.jpg"))
            {
                try { File.Delete(file); } catch { }
            }

            int index = 0;
            foreach (var url in urls)
            {
                try
                {
                    var bytes = await _httpClient.GetByteArrayAsync(url);
                    string localPath = Path.Combine(localFolder, $"photo_{index++}.jpg");
                    await File.WriteAllBytesAsync(localPath, bytes);
                }
                catch (Exception)
                {
                    // Пропускаем поврежденный файл и идем дальше
                }
            }
        }
    }
}
