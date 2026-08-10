using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace PhotoFrame
{
    /// <summary>
    /// Всё, что известно о показанном кадре: файл, размеры, EXIF или метаданные видео.
    /// </summary>
    public partial class FileInfoPage : ContentPage
    {
        /// <summary>
        /// Путь к файлу, о котором показываются сведения.
        /// </summary>
        /// <remarks>
        /// Передаётся статикой, а не параметром маршрута: путь содержит слэши и пробелы,
        /// и протаскивать его через query-строку Shell означало бы возиться с
        /// экранированием без всякой пользы.
        /// </remarks>
        public static string? MediaPath { get; set; }

        public FileInfoPage()
        {
            InitializeComponent();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            DetailsLayout.Clear();

            string? mediaPath = MediaPath;
            if (string.IsNullOrEmpty(mediaPath))
            {
                DetailsLayout.Add(BuildMessage("Кадр не выбран."));
                return;
            }

            DetailsLayout.Add(BuildMessage("Чтение сведений..."));

            // EXIF и метаданные контейнера читаются с диска: на рамке это заметно,
            // поэтому не держим UI-поток.
            List<MediaDetailsReader.DetailRow> rows =
                await Task.Run(() => MediaDetailsReader.Read(mediaPath)).ConfigureAwait(true);

            DetailsLayout.Clear();

            if (rows.Count == 0)
            {
                DetailsLayout.Add(BuildMessage("Сведения недоступны."));
                return;
            }

            foreach (MediaDetailsReader.DetailRow row in rows)
            {
                DetailsLayout.Add(BuildRow(row.Label, row.Value));
            }
        }

        private static Label BuildMessage(string text) => new()
        {
            Text = text,
            TextColor = Colors.Gray,
            FontSize = 15,
        };

        private static View BuildRow(string label, string value)
        {
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(260) },
                    new ColumnDefinition { Width = GridLength.Star },
                },
                Padding = new Thickness(0, 10),
                ColumnSpacing = 16,
            };

            row.Add(new Label
            {
                Text = label,
                TextColor = Colors.Gray,
                FontSize = 15,
            });

            var valueLabel = new Label
            {
                Text = value,
                TextColor = Colors.White,
                FontSize = 15,

                // Пути и модели камер бывают длинными: переносим, а не обрезаем.
                LineBreakMode = LineBreakMode.WordWrap,
            };

            row.Add(valueLabel, column: 1);
            return row;
        }

        private async void OnBackClicked(object? sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}
