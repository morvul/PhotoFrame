using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.UI;
using Microsoft.Maui.Handlers;

namespace PhotoFrame
{
    /// <summary>
    /// Платформенная часть <see cref="VideoPlayerView"/> на основе ExoPlayer.
    /// </summary>
    /// <remarks>
    /// Раньше здесь был системный VideoView, и его хватало для H.264 из папок на
    /// устройстве. Но кадры общего альбома бывают в HEVC, и системный проигрыватель
    /// рамки (вендорный FFPlayer) от них отказывался — «error (1, 246)», нулевая
    /// длительность. При этом сама рамка HEVC умеет: опрос декодеров показал
    /// OMX.rk.video_decoder.hevc, аппаратный, до 1920x1088 — ровно под клипы альбома.
    /// ExoPlayer обращается к декодерам через MediaCodec, минуя вендорный проигрыватель,
    /// и правильно выбирает дорожку — а в клипах живых фото их две, 720p и 1080p.
    ///
    /// VP9 так и остаётся недостижимым: единственный декодер на него программный и
    /// ограничен 720x480, а клипы альбома — 1080p. Такие кадры страница отмечает
    /// и показывает снимком.
    /// </remarks>
    public class VideoPlayerViewHandler : ViewHandler<VideoPlayerView, PlayerView>
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
        /// Значения из androidx.media3.common.Player. В привязке они лежат так, что
        /// добраться до них из C# не удаётся, а числа эти в Media3 фиксированы.
        /// </summary>
        private const int RepeatModeOff = 0;
        private const int RepeatModeOne = 1;
        private const int PlaybackStateEnded = 4;

        private IExoPlayer? _player;
        private PlaybackListener? _listener;

        public VideoPlayerViewHandler() : base(Mapper, Commands)
        {
        }

        protected override PlayerView CreatePlatformView()
        {
            _player = new ExoPlayerBuilder(Context).Build();

            _listener = new PlaybackListener(this);
            _player!.AddListener(_listener);

            // PlayerView, а не голая поверхность: он сам вписывает кадр в свои границы
            // с сохранением пропорций. Иначе видео растягивается на весь экран, и
            // вертикальный клип живого фото выглядит раздавленным.
            var playerView = new PlayerView(Context)
            {
                Player = _player,
                UseController = false,
            };

            // Заслонка PlayerView по умолчанию чёрная и накрывает всё, пока клип
            // готовится: на смене кадра между живыми фото это выглядело чёрной вспышкой,
            // особенно когда следующий клип другой ориентации. Прозрачная заслонка
            // оставляет на виду сам снимок, поверх которого клип и появится.
            playerView.SetShutterBackgroundColor(0);
            playerView.SetBackgroundColor(Android.Graphics.Color.Transparent);

            // Кадр вписывается целиком либо обрезается по краям — так же, как снимок:
            // клип живого фото должен совпадать с кадром, из которого он вырастает.
            playerView.ResizeMode = FrameSettings.FillScreen
                ? AspectRatioFrameLayout.ResizeModeZoom
                : AspectRatioFrameLayout.ResizeModeFit;

            return playerView;
        }

        protected override void DisconnectHandler(PlayerView platformView)
        {
            platformView.Player = null;

            if (_player is not null)
            {
                if (_listener is not null)
                {
                    _player.RemoveListener(_listener);
                }

                _player.Release();
                _player = null;
            }

            _listener?.Dispose();
            _listener = null;

            base.DisconnectHandler(platformView);
        }

        /// <summary>
        /// Текущая позиция и длительность в миллисекундах.
        /// </summary>
        /// <remarks>
        /// У проигрывателя нет события о продвижении, поэтому значения опрашиваются.
        /// Пока файл не подготовлен, длительность приходит как C.TimeUnset — отдаём нули.
        /// </remarks>
        internal (int PositionMilliseconds, int DurationMilliseconds) QueryProgress()
        {
            if (_player is null)
            {
                return (0, 0);
            }

            try
            {
                long duration = _player.Duration;
                long position = _player.CurrentPosition;

                return duration <= 0
                    ? (0, 0)
                    : ((int)position, (int)duration);
            }
            catch (Java.Lang.Throwable)
            {
                return (0, 0);
            }
        }

        private void RaiseFinished() => VirtualView?.RaisePlaybackFinished();

        private void RaiseFirstFrame() => VirtualView?.RaiseFirstFrameRendered();

        private void RaiseFailed(string reason)
        {
            FrameLog.Warn($"Видео не воспроизведено: {reason}");
            VirtualView?.RaisePlaybackFailed();
        }

        private static void MapSourcePath(VideoPlayerViewHandler handler, VideoPlayerView view)
        {
            IExoPlayer? player = handler._player;
            if (player is null)
            {
                return;
            }

            if (string.IsNullOrEmpty(view.SourcePath))
            {
                player.Stop();
                player.ClearMediaItems();
                return;
            }

            // file://-URI, а не путь: ExoPlayer определяет источник по схеме.
            using var videoFile = new Java.IO.File(view.SourcePath!);
            player.SetMediaItem(MediaItem.FromUri(Android.Net.Uri.FromFile(videoFile)!));
            player.Prepare();
        }

        private static void MapIsLooping(VideoPlayerViewHandler handler, VideoPlayerView view)
        {
            if (handler._player is not null)
            {
                handler._player.RepeatMode = view.IsLooping
                    ? RepeatModeOne
                    : RepeatModeOff;
            }
        }

        private static void MapIsMuted(VideoPlayerViewHandler handler, VideoPlayerView view)
        {
            if (handler._player is not null)
            {
                handler._player.Volume = view.IsMuted ? 0f : 1f;
            }
        }

        private static void MapPlay(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args) =>
            handler._player?.Play();

        private static void MapPause(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args) =>
            handler._player?.Pause();

        private static void MapStop(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args)
        {
            handler._player?.Stop();
            handler._player?.ClearMediaItems();
        }

        /// <summary>
        /// Слушатель проигрывателя: сообщает странице об окончании и об отказе.
        /// </summary>
        /// <remarks>
        /// Отдельный класс, а не события на самом проигрывателе: так видно, что именно
        /// слушается, и подписку легко снять при освобождении обработчика.
        /// </remarks>
        private sealed class PlaybackListener : Java.Lang.Object, IPlayerListener
        {
            private readonly VideoPlayerViewHandler _handler;

            public PlaybackListener(VideoPlayerViewHandler handler) => _handler = handler;

            public void OnPlaybackStateChanged(int playbackState)
            {
                if (playbackState == PlaybackStateEnded)
                {
                    _handler.RaiseFinished();
                }
            }

            public void OnRenderedFirstFrame() => _handler.RaiseFirstFrame();

            public void OnPlayerError(PlaybackException? error) =>
                _handler.RaiseFailed($"{error?.ErrorCodeName}: {error?.Message}");
        }
    }
}
