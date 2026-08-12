using Android.Graphics;

// Paint и Color есть и в Android.Graphics, и в Microsoft.Maui.Graphics, который
// подключён через ImplicitUsings, поэтому нужны однозначные псевдонимы.
using AndroidPaint = Android.Graphics.Paint;
using AndroidColor = Android.Graphics.Color;
using AndroidRect = Android.Graphics.Rect;

namespace PhotoFrame
{
    /// <summary>
    /// Рисует ночные часы на весь экран, зажигая только каждый второй пиксель.
    /// </summary>
    /// <remarks>
    /// Шахматная маска решает сразу две задачи: суммарная яркость падает примерно вдвое
    /// без изменения подсветки, и набор светящихся пикселей меняется при каждом
    /// обновлении времени, поэтому статичная картинка не «выжигается» в одном месте.
    ///
    /// Пиксельная маска недостижима средствами Microsoft.Maui.Graphics, поэтому здесь
    /// напрямую используется Android.Graphics: у проекта единственный целевой
    /// фреймворк — net10.0-android.
    /// </remarks>
    internal static class NightClockRenderer
    {
        /// <summary>Доля ширины экрана, которую занимает строка времени.</summary>
        private const float TimeWidthFraction = 0.92f;

        /// <summary>Размер даты относительно размера времени.</summary>
        private const float DateSizeFraction = 0.13f;

        /// <summary>
        /// Готовит PNG с часами. Вызывать в фоновом потоке: кодирование кадра
        /// 1280x800 на такой рамке занимает заметное время.
        /// </summary>
        /// <param name="phaseShifted">
        /// Сдвигает шахматную маску на один пиксель — при каждом обновлении времени
        /// светятся уже другие пиксели.
        /// </param>
        /// <param name="colorHex">Цвет часов из <see cref="NightClockPalette"/>.</param>
        /// <param name="brightnessPercent">
        /// Яркость рисунка в процентах. Шахматная маска гасит половину пикселей поверх
        /// этого значения, поэтому итоговая яркость примерно вдвое ниже.
        /// </param>
        public static byte[] RenderPng(
            int widthPixels,
            int heightPixels,
            string timeText,
            string? dateText,
            bool phaseShifted,
            string colorHex,
            int brightnessPercent)
        {
            using Bitmap frame = Bitmap.CreateBitmap(widthPixels, heightPixels, Bitmap.Config.Argb8888!)!;
            using var canvas = new Canvas(frame);
            canvas.DrawColor(AndroidColor.Black);

            using Bitmap checkerPattern = CreateCheckerPattern(colorHex);
            using var checkerShader = new BitmapShader(
                checkerPattern, Shader.TileMode.Repeat!, Shader.TileMode.Repeat!);

            if (phaseShifted)
            {
                // Сдвиг на один пиксель по горизонтали инвертирует шахматный порядок.
                using var shaderMatrix = new Matrix();
                shaderMatrix.SetTranslate(1, 0);
                checkerShader.SetLocalMatrix(shaderMatrix);
            }

            using var textPaint = new AndroidPaint(PaintFlags.AntiAlias)
            {
                TextAlign = AndroidPaint.Align.Center,
                Alpha = ToAlpha(brightnessPercent),
            };

            textPaint.SetShader(checkerShader);
            textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));

            float timeTextSize = FitTextSize(textPaint, timeText, widthPixels * TimeWidthFraction);
            textPaint.TextSize = timeTextSize;

            // Вертикальное центрирование по фактической высоте глифов, а не по строке:
            // у крупных цифр разница заметна.
            var timeBounds = new AndroidRect();
            textPaint.GetTextBounds(timeText, 0, timeText.Length, timeBounds);

            bool hasDate = !string.IsNullOrEmpty(dateText);
            float dateTextSize = timeTextSize * DateSizeFraction;
            float dateBlockHeight = hasDate ? dateTextSize * 1.6f : 0f;

            float centreX = widthPixels / 2f;
            float timeBaselineY =
                (heightPixels - dateBlockHeight) / 2f + timeBounds.Height() / 2f;

            canvas.DrawText(timeText, centreX, timeBaselineY, textPaint);

            if (hasDate)
            {
                textPaint.TextSize = dateTextSize;
                textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Normal));
                canvas.DrawText(dateText!, centreX, timeBaselineY + dateBlockHeight, textPaint);
            }

            using var pngStream = new System.IO.MemoryStream();
            frame.Compress(Bitmap.CompressFormat.Png!, 100, pngStream);
            return pngStream.ToArray();
        }

        /// <summary>Подбирает размер шрифта так, чтобы строка заняла нужную ширину.</summary>
        private static float FitTextSize(AndroidPaint paint, string text, float targetWidth)
        {
            const float probeSize = 100f;
            paint.TextSize = probeSize;

            float measuredWidth = paint.MeasureText(text);
            return measuredWidth <= 0
                ? probeSize
                : probeSize * (targetWidth / measuredWidth);
        }

        /// <summary>Проценты яркости в прозрачность краски.</summary>
        private static int ToAlpha(int brightnessPercent) =>
            System.Math.Clamp(brightnessPercent, 1, 100) * 255 / 100;

        /// <summary>
        /// Плитка 2x2, в которой светятся только пиксели по диагонали.
        /// </summary>
        private static Bitmap CreateCheckerPattern(string colorHex)
        {
            Bitmap pattern = Bitmap.CreateBitmap(2, 2, Bitmap.Config.Argb8888!)!;
            AndroidColor litColor = ParseColor(colorHex);

            pattern.SetPixel(0, 0, litColor);
            pattern.SetPixel(1, 1, litColor);
            pattern.SetPixel(1, 0, AndroidColor.Transparent);
            pattern.SetPixel(0, 1, AndroidColor.Transparent);

            return pattern;
        }

        /// <summary>
        /// Разбирает код цвета. Значение приходит из настроек, поэтому непонятное
        /// заменяется цветом по умолчанию, а не роняет отрисовку.
        /// </summary>
        private static AndroidColor ParseColor(string colorHex)
        {
            try
            {
                return AndroidColor.ParseColor(NightClockPalette.ResolveHex(colorHex));
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                return AndroidColor.ParseColor(NightClockPalette.DefaultHex);
            }
        }
    }
}
