namespace PhotoFrame
{
    /// <summary>
    /// Неизменяемые параметры сборки. Всё, что пользователь может поменять на самой
    /// рамке, живёт в <see cref="FrameSettings"/>.
    /// </summary>
    public static class AppSettings
    {
        /// <summary>Разрешение экрана рамки — подтверждено на устройстве через "wm size".</summary>
        public const int FrameWidthPixels = 1280;

        /// <summary>Высота экрана рамки в пикселях.</summary>
        public const int FrameHeightPixels = 800;

        /// <summary>Верхняя граница на число скачиваемых снимков — флэш-память рамки невелика.</summary>
        public const int MaxPhotosToDownload = 500;
    }
}
