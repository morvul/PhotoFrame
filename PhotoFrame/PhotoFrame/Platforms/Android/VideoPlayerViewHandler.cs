using Android.Views;
using Android.Widget;
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
    /// ExoPlayer идёт к декодерам через MediaCodec, минуя вендорный проигрыватель,
    /// и правильно выбирает дорожку — в клипах живых фото их две, 720p и 1080p.
    ///
    /// Проигрывателей два, и они меняются местами: клип готовится на невидимом слое,
    /// а когда у него появился первый кадр, слои перетекают друг в друга. С одним
    /// проигрывателем переход между вертикальным и горизонтальным клипом был виден —
    /// поверхность переставлялась под новый размер кадра.
    ///
    /// VP9 так и остаётся недостижимым: единственный декодер на него программный и
    /// ограничен 720x480, а клипы альбома — 1080p. Такие кадры страница отмечает
    /// и показывает снимком.
    ///
    /// Отыгравший слой не просто останавливается, а пересоздаётся целиком. Причина
    /// измерена на рамке: после Stop и ClearMediaItems проигрыватель держит декодер
    /// за собой, и следующий клип отдаётся тому же экземпляру. С библиотекой, где
    /// живых фото почти две тысячи, клип запускается едва ли не на каждом кадре, и
    /// вендорный декодер перестаёт разбирать очередь — журнал заполняется
    /// «Rkvpu_SendInputData: stream list full wait» на пятнадцать секунд подряд
    /// (одно ядро занято целиком), после чего приложение падает с SIGSEGV. Свежий
    /// экземпляр на каждый клип этот путь исключает: декодер честно отдаётся системе.
    /// </remarks>
    public class VideoPlayerViewHandler : ViewHandler<VideoPlayerView, FrameLayout>
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

        /// <summary>
        /// Сколько длится перетекание. Четверть секунды: заметно как смена кадра,
        /// а не как рывок, и не растягивает начало короткого клипа живого фото.
        /// </summary>
        private const long CrossfadeMilliseconds = 250;

        private readonly Layer[] _layers = new Layer[2];

        /// <summary>Контейнер нужен, чтобы пересоздавать слой на том же виде.</summary>
        private FrameLayout? _container;

        /// <summary>Слой, который сейчас на виду.</summary>
        private int _frontIndex;

        public VideoPlayerViewHandler() : base(Mapper, Commands)
        {
        }

        private Layer Front => _layers[_frontIndex];

        private Layer Back => _layers[1 - _frontIndex];

        protected override FrameLayout CreatePlatformView()
        {
            var container = (FrameLayout)LayoutInflater.From(Context)!
                .Inflate(Resource.Layout.video_player, null)!;

            _container = container;

            _layers[0] = CreateLayer(container.FindViewById<PlayerView>(Resource.Id.player_back)!, 0);
            _layers[1] = CreateLayer(container.FindViewById<PlayerView>(Resource.Id.player_front)!, 1);

            return container;
        }

        /// <summary>
        /// Отдаёт декодер и заводит на том же виде новый проигрыватель.
        /// </summary>
        /// <remarks>
        /// Вызывается, когда слой отыграл и скрылся: к следующему клипу он должен быть
        /// чистым. Release освобождает MediaCodec немедленно, а не когда до объекта
        /// доберётся сборщик, — на этом и держится весь смысл.
        /// </remarks>
        private void RecycleLayer(int layerIndex)
        {
            Layer? layer = _layers[layerIndex];
            if (layer is null || _container is null)
            {
                return;
            }

            try
            {
                layer.View.Player = null;
                layer.Player.RemoveListener(layer.Listener);
                layer.Player.Release();
                layer.Listener.Dispose();
            }
            catch (Java.Lang.Throwable releaseFailure)
            {
                // Даже если освободить не удалось, новый экземпляр всё равно нужен:
                // играть на сломанном смысла нет.
                FrameLog.Warn($"Проигрыватель не освобождён: {releaseFailure.Message}");
            }

            _layers[layerIndex] = CreateLayer(layer.View, layerIndex);
        }

        private Layer CreateLayer(PlayerView view, int index)
        {
            IExoPlayer player = new ExoPlayerBuilder(Context).Build()!;
            var listener = new PlaybackListener(this, index);

            player.AddListener(listener);
            view.Player = player;

            // Кадр вписывается целиком либо обрезается по краям — так же, как снимок:
            // клип живого фото должен совпадать с кадром, из которого он вырастает.
            view.ResizeMode = FrameSettings.FillScreen
                ? AspectRatioFrameLayout.ResizeModeZoom
                : AspectRatioFrameLayout.ResizeModeFit;

            return new Layer(view, player, listener);
        }

        protected override void DisconnectHandler(FrameLayout platformView)
        {
            foreach (Layer layer in _layers)
            {
                if (layer is null)
                {
                    continue;
                }

                layer.View.Player = null;
                layer.Player.RemoveListener(layer.Listener);
                layer.Player.Release();
                layer.Listener.Dispose();
            }

            base.DisconnectHandler(platformView);
        }

        /// <summary>
        /// Текущая позиция и длительность в миллисекундах.
        /// </summary>
        /// <remarks>
        /// У проигрывателя нет события о продвижении, поэтому значения опрашиваются.
        /// Пока файл не подготовлен, длительность неизвестна — отдаём нули.
        /// </remarks>
        internal (int PositionMilliseconds, int DurationMilliseconds) QueryProgress()
        {
            try
            {
                IExoPlayer? player = Front?.Player;
                if (player is null)
                {
                    return (0, 0);
                }

                long duration = player.Duration;
                return duration <= 0 ? (0, 0) : ((int)player.CurrentPosition, (int)duration);
            }
            catch (Java.Lang.Throwable)
            {
                return (0, 0);
            }
        }

        /// <summary>
        /// Меняет слои местами: новый проявляется, прежний угасает.
        /// </summary>
        /// <remarks>
        /// Прежний слой останавливается только после перетекания — иначе он погас бы
        /// раньше, чем новый проступил, и between кадрами мелькала бы чернота.
        /// </remarks>
        private void SwapLayers(int newFrontIndex)
        {
            if (newFrontIndex == _frontIndex)
            {
                return;
            }

            Layer incoming = _layers[newFrontIndex];
            Layer outgoing = _layers[_frontIndex];

            _frontIndex = newFrontIndex;

            incoming.View.Animate()!.Alpha(1f)!.SetDuration(CrossfadeMilliseconds)!.Start();
            outgoing.View.Animate()!
                .Alpha(0f)!
                .SetDuration(CrossfadeMilliseconds)!
                .WithEndAction(new Java.Lang.Runnable(() =>
                {
                    outgoing.Player.Stop();
                    outgoing.Player.ClearMediaItems();

                    // Слой ушёл с виду — отдаём его декодер. Индекс, а не ссылка:
                    // за четверть секунды перетекания слои могли поменяться снова.
                    RecycleLayer(1 - _frontIndex);
                }))!
                .Start();
        }

        /// <summary>Плавно убирает видео, открывая снимок под ним.</summary>
        private void FadeOutEverything()
        {
            for (int layerIndex = 0; layerIndex < _layers.Length; layerIndex++)
            {
                Layer captured = _layers[layerIndex];
                int capturedIndex = layerIndex;

                captured.View.Animate()!
                    .Alpha(0f)!
                    .SetDuration(CrossfadeMilliseconds)!
                    .WithEndAction(new Java.Lang.Runnable(() =>
                    {
                        captured.Player.Stop();
                        captured.Player.ClearMediaItems();
                        RecycleLayer(capturedIndex);
                    }))!
                    .Start();
            }
        }

        private void OnFirstFrame(int layerIndex) => SwapLayers(layerIndex);

        private void OnEnded(int layerIndex)
        {
            // Доиграл именно тот слой, что на виду, — иначе это отголосок прошлого клипа.
            if (layerIndex == _frontIndex)
            {
                VirtualView?.RaisePlaybackFinished();
            }
        }

        private void OnFailed(int layerIndex, string reason)
        {
            FrameLog.Warn($"Видео не воспроизведено: {reason}");
            VirtualView?.RaisePlaybackFailed();
        }

        private static void MapSourcePath(VideoPlayerViewHandler handler, VideoPlayerView view)
        {
            if (handler._layers[0] is null)
            {
                return;
            }

            if (string.IsNullOrEmpty(view.SourcePath))
            {
                handler.FadeOutEverything();
                return;
            }

            // Готовим на невидимом слое: на виду пока прежний кадр или сам снимок.
            Layer target = handler.Back;

            using var videoFile = new Java.IO.File(view.SourcePath!);
            target.Player.SetMediaItem(MediaItem.FromUri(Android.Net.Uri.FromFile(videoFile)!));
            target.Player.RepeatMode = view.IsLooping ? RepeatModeOne : RepeatModeOff;
            target.Player.Volume = view.IsMuted ? 0f : 1f;
            target.Player.Prepare();
        }

        private static void MapIsLooping(VideoPlayerViewHandler handler, VideoPlayerView view)
        {
            foreach (Layer layer in handler._layers)
            {
                if (layer is not null)
                {
                    layer.Player.RepeatMode = view.IsLooping ? RepeatModeOne : RepeatModeOff;
                }
            }
        }

        private static void MapIsMuted(VideoPlayerViewHandler handler, VideoPlayerView view)
        {
            foreach (Layer layer in handler._layers)
            {
                if (layer is not null)
                {
                    layer.Player.Volume = view.IsMuted ? 0f : 1f;
                }
            }
        }

        /// <summary>
        /// Играть просят оба слоя: тот, что готовится, должен начать рисовать, иначе
        /// первого кадра не дождаться, а тот, что на виду, мог стоять на паузе.
        /// </summary>
        private static void MapPlay(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args)
        {
            handler.Back?.Player.Play();
            handler.Front?.Player.Play();
        }

        private static void MapPause(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args) =>
            handler.Front?.Player.Pause();

        private static void MapStop(
            VideoPlayerViewHandler handler, VideoPlayerView view, object? args) =>
            handler.FadeOutEverything();

        /// <summary>Проигрыватель со своим видом и подпиской.</summary>
        private sealed record Layer(PlayerView View, IExoPlayer Player, PlaybackListener Listener);

        /// <summary>
        /// Слушатель одного слоя: сообщает о первом кадре, окончании и отказе.
        /// </summary>
        private sealed class PlaybackListener : Java.Lang.Object, IPlayerListener
        {
            private readonly VideoPlayerViewHandler _handler;
            private readonly int _layerIndex;

            public PlaybackListener(VideoPlayerViewHandler handler, int layerIndex)
            {
                _handler = handler;
                _layerIndex = layerIndex;
            }

            public void OnRenderedFirstFrame() => _handler.OnFirstFrame(_layerIndex);

            public void OnPlaybackStateChanged(int playbackState)
            {
                if (playbackState == PlaybackStateEnded)
                {
                    _handler.OnEnded(_layerIndex);
                }
            }

            public void OnPlayerError(PlaybackException? error) =>
                _handler.OnFailed(_layerIndex, $"{error?.ErrorCodeName}: {error?.Message}");
        }
    }
}
