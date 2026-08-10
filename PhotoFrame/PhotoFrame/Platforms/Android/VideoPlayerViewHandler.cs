using Android.Media;
using Android.Widget;
using Microsoft.Maui.Handlers;

namespace PhotoFrame
{
    /// <summary>
    /// Платформенная часть <see cref="VideoPlayerView"/> на основе системного VideoView.
    /// </summary>
    public class VideoPlayerViewHandler : ViewHandler<VideoPlayerView, VideoView>
    {
        public static readonly IPropertyMapper<VideoPlayerView, VideoPlayerViewHandler> Mapper =
            new PropertyMapper<VideoPlayerView, VideoPlayerViewHandler>(ViewMapper)
            {
                [nameof(VideoPlayerView.SourcePath)] = MapSourcePath,
                [nameof(VideoPlayerView.IsLooping)] = MapIsLooping,
                [nameof(VideoPlayerView.IsMuted)] = MapIsMuted,
            };

        public static readonly CommandMapper<VideoPlayerView, VideoPlayerViewHandler> Commands =
            new(ViewCommandMapper)
            {
                [nameof(VideoPlayerView.Play)] = MapPlay,
                [nameof(VideoPlayerView.Pause)] = MapPause,
                [nameof(VideoPlayerView.Stop)] = MapStop,
            };

        /// <summary>
        /// Держим сам MediaPlayer: громкость и зацикливание задаются на нём, а VideoView
        /// отдаёт его только в OnPrepared.
        /// </summary>
        private MediaPlayer? _preparedPlayer;

        public VideoPlayerViewHandler() : base(Mapper, Commands)
        {
        }

        protected override VideoView CreatePlatformView()
        {
            var videoView = new VideoView(Context);

            videoView.Prepared += OnPrepared;
            videoView.Completion += OnCompletion;
            videoView.Error += OnError;

            return videoView;
        }

        protected override void DisconnectHandler(VideoView platformView)
        {
            platformView.Prepared -= OnPrepared;
            platformView.Completion -= OnCompletion;
            platformView.Error -= OnError;

            platformView.StopPlayback();
            _preparedPlayer = null;

            base.DisconnectHandler(platformView);
        }

        private void OnPrepared(object? sender, EventArgs e)
        {
            // MediaPlayer доступен только здесь, поэтому громкость и цикл применяем повторно.
            _preparedPlayer = sender as MediaPlayer;
            ApplyLooping();
            ApplyMuted();
        }

        private void OnCompletion(object? sender, EventArgs e)
        {
            // При зацикливании MediaPlayer сам начинает заново и Completion не приходит,
            // но проверка страхует от расхождения поведения между прошивками.
            if (VirtualView?.IsLooping == true)
            {
                PlatformView?.Start();
                return;
            }

            VirtualView?.RaisePlaybackFinished();
        }

        private void OnError(object? sender, MediaPlayer.ErrorEventArgs e)
        {
            // Нечитаемый файл не должен останавливать слайд-шоу: сообщаем о завершении,
            // и страница переходит к следующему кадру.
            System.Diagnostics.Debug.WriteLine(
                $"Видео не воспроизведено: what={e.What}, extra={e.Extra}");

            e.Handled = true;
            VirtualView?.RaisePlaybackFinished();
        }

        /// <summary>
        /// Текущая позиция и длительность в миллисекундах.
        /// </summary>
        /// <remarks>
        /// У VideoView нет события о продвижении воспроизведения, поэтому значения
        /// приходится опрашивать. Duration возвращает -1, пока файл не подготовлен.
        /// </remarks>
        internal (int PositionMilliseconds, int DurationMilliseconds) QueryProgress()
        {
            VideoView? platformView = PlatformView;
            if (platformView is null)
            {
                return (0, 0);
            }

            try
            {
                return (platformView.CurrentPosition, platformView.Duration);
            }
            catch (Java.Lang.Throwable)
            {
                // Опрос до подготовки файла может бросить: прогресс тогда просто нулевой.
                return (0, 0);
            }
        }

        private void ApplyLooping()
        {
            if (_preparedPlayer is not null && VirtualView is not null)
            {
                _preparedPlayer.Looping = VirtualView.IsLooping;
            }
        }

        private void ApplyMuted()
        {
            if (_preparedPlayer is null)
            {
                return;
            }

            float volume = VirtualView?.IsMuted == true ? 0f : 1f;
            _preparedPlayer.SetVolume(volume, volume);
        }

        private static void MapSourcePath(VideoPlayerViewHandler handler, VideoPlayerView view)
        {
            if (string.IsNullOrEmpty(view.SourcePath))
            {
                handler.PlatformView?.StopPlayback();
                return;
            }

            handler._preparedPlayer = null;

            // SetVideoPath сначала пробует трактовать путь как content://-URI и пишет в лог
            // "No content provider", прежде чем свалиться на файл. Отдаём file://-URI сразу:
            // и лог чище, и не зависим от того, что реализация решит попробовать первым.
            using var videoFile = new Java.IO.File(view.SourcePath!);
            handler.PlatformView?.SetVideoURI(Android.Net.Uri.FromFile(videoFile));
        }

        private static void MapIsLooping(VideoPlayerViewHandler handler, VideoPlayerView view) =>
            handler.ApplyLooping();

        private static void MapIsMuted(VideoPlayerViewHandler handler, VideoPlayerView view) =>
            handler.ApplyMuted();

        private static void MapPlay(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args) =>
            handler.PlatformView?.Start();

        private static void MapPause(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args) =>
            handler.PlatformView?.Pause();

        private static void MapStop(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args)
        {
            handler.PlatformView?.StopPlayback();
            handler._preparedPlayer = null;
        }
    }
}
