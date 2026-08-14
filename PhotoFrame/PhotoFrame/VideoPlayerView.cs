using System;
using Microsoft.Maui.Controls;

namespace PhotoFrame
{
    /// <summary>
    /// Проигрыватель видео поверх слайд-шоу.
    /// </summary>
    /// <remarks>
    /// Своя обёртка над Android VideoView, а не CommunityToolkit.Maui.MediaElement:
    /// тот тянет в APK AndroidX и нативные библиотеки ExoPlayer, а рамка 32-битная,
    /// на Android 8.1, и каждая лишняя зависимость здесь — это ещё один способ
    /// получить неработающую сборку. VideoView входит в саму систему.
    /// </remarks>
    public class VideoPlayerView : View
    {
        public static readonly BindableProperty SourcePathProperty = BindableProperty.Create(
            nameof(SourcePath), typeof(string), typeof(VideoPlayerView), default(string));

        public static readonly BindableProperty IsLoopingProperty = BindableProperty.Create(
            nameof(IsLooping), typeof(bool), typeof(VideoPlayerView), false);

        public static readonly BindableProperty IsMutedProperty = BindableProperty.Create(
            nameof(IsMuted), typeof(bool), typeof(VideoPlayerView), false);

        /// <summary>Путь к файлу. Установка не запускает воспроизведение.</summary>
        public string? SourcePath
        {
            get => (string?)GetValue(SourcePathProperty);
            set => SetValue(SourcePathProperty, value);
        }

        /// <summary>Повторять по кругу.</summary>
        public bool IsLooping
        {
            get => (bool)GetValue(IsLoopingProperty);
            set => SetValue(IsLoopingProperty, value);
        }

        /// <summary>Без звука.</summary>
        public bool IsMuted
        {
            get => (bool)GetValue(IsMutedProperty);
            set => SetValue(IsMutedProperty, value);
        }

        /// <summary>Видео доиграло до конца и не было зацикленным.</summary>
        public event EventHandler? PlaybackFinished;

        /// <summary>
        /// Файл не воспроизводится: проигрыватель не смог его подготовить.
        /// </summary>
        /// <remarks>
        /// Отдельно от <see cref="PlaybackFinished"/> нарочно: доигравший клип и клип,
        /// который вообще не открылся, требуют разного — второй незачем пробовать снова.
        /// </remarks>
        public event EventHandler? PlaybackFailed;

        /// <summary>
        /// Первый кадр клипа отрисован.
        /// </summary>
        /// <remarks>
        /// До этого момента проигрыватель нечего показывать: пока он готовится, на экране
        /// должен оставаться снимок, иначе на смене кадра видна чёрная вспышка.
        /// </remarks>
        public event EventHandler? FirstFrameRendered;

        /// <summary>Начать или продолжить воспроизведение.</summary>
        public void Play() => Handler?.Invoke(nameof(Play));

        /// <summary>Приостановить, сохранив позицию.</summary>
        public void Pause() => Handler?.Invoke(nameof(Pause));

        /// <summary>Остановить и освободить проигрыватель.</summary>
        public void Stop() => Handler?.Invoke(nameof(Stop));

        /// <summary>
        /// Текущая позиция и длительность в миллисекундах; (0, 0) пока нечего играть.
        /// </summary>
        /// <remarks>
        /// Обратный вызов в обработчик: свойства MAUI передают значения только в сторону
        /// платформы, а прогресс нужно читать оттуда.
        /// </remarks>
        public (int PositionMilliseconds, int DurationMilliseconds) QueryProgress() =>
            (Handler as VideoPlayerViewHandler)?.QueryProgress() ?? (0, 0);

        /// <summary>Вызывается платформенным обработчиком.</summary>
        internal void RaisePlaybackFinished() =>
            PlaybackFinished?.Invoke(this, EventArgs.Empty);

        /// <summary>Вызывается, когда файл не удалось воспроизвести.</summary>
        internal void RaisePlaybackFailed() =>
            PlaybackFailed?.Invoke(this, EventArgs.Empty);

        /// <summary>Вызывается платформенным обработчиком на первом кадре.</summary>
        internal void RaiseFirstFrameRendered() =>
            FirstFrameRendered?.Invoke(this, EventArgs.Empty);
    }
}
