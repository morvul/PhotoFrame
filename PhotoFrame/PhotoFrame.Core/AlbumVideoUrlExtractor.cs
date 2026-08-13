using System.Text.RegularExpressions;

namespace PhotoFrame
{
    /// <summary>
    /// Достаёт ссылку на сам видеофайл со страницы отдельного кадра альбома.
    /// </summary>
    /// <remarks>
    /// В списке альбома такой ссылки нет — только кадр-заставка, из-за чего видео
    /// раньше показывались стоп-кадром. Ссылка есть на странице отдельного кадра
    /// (`/share/&lt;альбом&gt;/photo/&lt;кадр&gt;?key=…`), на отдельном хосте
    /// video-downloads.googleusercontent.com, и скачивается без авторизации.
    ///
    /// Единственная копия такой ссылки в списке альбома принадлежит обложке и отвечает
    /// ошибкой 500 — проверено; годится только ссылка со страницы кадра.
    /// </remarks>
    public static partial class AlbumVideoUrlExtractor
    {
        [GeneratedRegex(@"https://video-downloads\.googleusercontent\.com/[A-Za-z0-9_\-=]+")]
        private static partial Regex VideoUrlRegex();

        /// <summary>Ссылка на видео либо null, если её на странице нет.</summary>
        public static string? Extract(string itemPageHtml)
        {
            if (string.IsNullOrEmpty(itemPageHtml))
            {
                return null;
            }

            Match match = VideoUrlRegex().Match(itemPageHtml);
            return match.Success ? match.Value : null;
        }
    }
}
