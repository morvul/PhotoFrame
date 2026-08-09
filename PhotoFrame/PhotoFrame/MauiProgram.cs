using Microsoft.Extensions.Logging;

namespace PhotoFrame
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            // Singleton: внутри SharedAlbumPhotoSource живёт настроенный HttpClient,
            // пересоздавать его на каждый показ страницы не нужно.
            builder.Services.AddSingleton<SharedAlbumPhotoSource>();
            builder.Services.AddSingleton<LocalFolderPhotoSource>();
            builder.Services.AddSingleton<CompositePhotoSource>();

#if DEBUG
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
