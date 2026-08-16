using System.Collections.Generic;
using PhotoFrame;
using Xunit;

namespace PhotoFrame.Core.Tests
{
    /// <summary>
    /// Разбор ответов Immich и сборка адресов.
    /// </summary>
    /// <remarks>
    /// Образцы JSON здесь урезаны до используемых полей: у настоящего ответа их
    /// несколько десятков, и полная копия только скрывала бы, что именно проверяется.
    /// Лишние поля добавлены там, где важно, что разбор их игнорирует.
    /// </remarks>
    public class ImmichCatalogTests
    {
        [Theory]
        [InlineData("192.168.1.10:2283", "http://192.168.1.10:2283")]
        [InlineData("http://immich.home/", "http://immich.home")]
        [InlineData("https://immich.example.com/api", "https://immich.example.com")]
        [InlineData("  immich.home  ", "http://immich.home")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void NormalizeServerUrl_ForgivesCommonTypos(string? enteredUrl, string expectedUrl) =>
            Assert.Equal(expectedUrl, ImmichCatalog.NormalizeServerUrl(enteredUrl));

        [Fact]
        public void BuildPreviewUrl_AsksServerForScreenSizedJpeg() =>
            Assert.Equal(
                "http://immich.home/api/assets/abc/thumbnail?size=preview",
                ImmichCatalog.BuildPreviewUrl("immich.home/", "abc"));

        [Fact]
        public void BuildAlbumUrl_EscapesIdentifier() =>
            Assert.Equal(
                "http://immich.home/api/albums/a%2Fb",
                ImmichCatalog.BuildAlbumUrl("http://immich.home", "a/b"));

        [Fact]
        public void ParseAlbums_ReadsNameAndCount()
        {
            const string albumsJson = """
                [
                  {"id":"1a","albumName":"Отпуск","assetCount":42,"shared":false},
                  {"id":"2b","albumName":"Дом","assetCount":7}
                ]
                """;

            List<ImmichAlbum> albums = ImmichCatalog.ParseAlbums(albumsJson);

            Assert.Equal(2, albums.Count);
            Assert.Equal(new ImmichAlbum("1a", "Отпуск", 42), albums[0]);
            Assert.Equal(new ImmichAlbum("2b", "Дом", 7), albums[1]);
        }

        [Fact]
        public void ParseAlbumAssets_KeepsServerOrderAndMarksVideos()
        {
            const string albumJson = """
                {
                  "id":"1a",
                  "albumName":"Отпуск",
                  "assets":[
                    {"id":"one","type":"IMAGE","originalFileName":"IMG_0001.HEIC"},
                    {"id":"two","type":"VIDEO","originalFileName":"IMG_0002.MOV"}
                  ]
                }
                """;

            List<ImmichAsset> assets = ImmichCatalog.ParseAlbumAssets(albumJson);

            Assert.Equal(2, assets.Count);
            Assert.Equal(new ImmichAsset("one", "IMG_0001.HEIC", IsVideo: false), assets[0]);
            Assert.True(assets[1].IsVideo);
        }

        [Fact]
        public void ParseAlbumAssets_SkipsWhatServerAlreadyTrashed()
        {
            const string albumJson = """
                {"assets":[
                  {"id":"kept","type":"IMAGE","isTrashed":false},
                  {"id":"gone","type":"IMAGE","isTrashed":true}
                ]}
                """;

            ImmichAsset onlyAsset = Assert.Single(ImmichCatalog.ParseAlbumAssets(albumJson));
            Assert.Equal("kept", onlyAsset.Id);
        }

        [Fact]
        public void ParseAlbumAssets_SkipsEntriesWithoutIdentifier()
        {
            const string albumJson = """{"assets":[{"type":"IMAGE"},{"id":"real","type":"IMAGE"}]}""";

            Assert.Equal("real", Assert.Single(ImmichCatalog.ParseAlbumAssets(albumJson)).Id);
        }

        [Fact]
        public void ParseSearchAssets_ReportsNextPageGivenAsString()
        {
            const string searchJson = """
                {"assets":{"total":2,"count":1,"items":[{"id":"one","type":"IMAGE"}],"nextPage":"2"}}
                """;

            List<ImmichAsset> assets = ImmichCatalog.ParseSearchAssets(searchJson, out int nextPage);

            Assert.Equal("one", Assert.Single(assets).Id);
            Assert.Equal(2, nextPage);
        }

        [Fact]
        public void ParseSearchAssets_TreatsNullNextPageAsLastPage()
        {
            const string searchJson = """{"assets":{"items":[],"nextPage":null}}""";

            Assert.Empty(ImmichCatalog.ParseSearchAssets(searchJson, out int nextPage));
            Assert.Equal(0, nextPage);
        }

        [Theory]
        [InlineData("")]
        [InlineData("<html>Sign in</html>")]
        [InlineData("{\"error\":\"unauthorized\"}")]
        public void ParseSearchAssets_ReturnsNothingForAnswerThatIsNotAListing(string body)
        {
            Assert.Empty(ImmichCatalog.ParseSearchAssets(body, out int nextPage));
            Assert.Equal(0, nextPage);
            Assert.Empty(ImmichCatalog.ParseAlbums(body));
            Assert.Empty(ImmichCatalog.ParseAlbumAssets(body));
        }

        [Fact]
        public void BuildSearchRequestBody_AsksOnlyForImages() =>
            Assert.Equal(
                "{\"page\":3,\"size\":1000,\"type\":\"IMAGE\",\"withDeleted\":false}",
                ImmichCatalog.BuildSearchRequestBody(3, 1000));
    }
}
