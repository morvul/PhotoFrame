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
            handler.PlatformView?.SetVideoPath(view.SourcePath);
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
