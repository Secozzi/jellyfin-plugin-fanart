using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Extensions;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Fanart.Providers
{
    public class ArtistProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly CultureInfo _usCulture = new CultureInfo("en-US");
        private readonly IServerConfigurationManager _config;
        private readonly IHttpClient _httpClient;
        private readonly IFileSystem _fileSystem;
        private readonly IJsonSerializer _jsonSerializer;

        internal static ArtistProvider Current;

        public ArtistProvider(IServerConfigurationManager config, IHttpClient httpClient, IFileSystem fileSystem, IJsonSerializer jsonSerializer)
        {
            _config = config;
            _httpClient = httpClient;
            _fileSystem = fileSystem;
            _jsonSerializer = jsonSerializer;

            Current = this;
        }

        public string Name => ProviderName;

        public static string ProviderName => "Fanart";

        public bool Supports(BaseItem item)
        {
            return item is MusicArtist;
        }

        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new List<ImageType>
            {
                ImageType.Primary,
                ImageType.Logo,
                ImageType.Art,
                ImageType.Banner,
                ImageType.Backdrop
            };
        }

        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var artist = (MusicArtist)item;

            var list = new List<RemoteImageInfo>();

            var artistMusicBrainzId = artist.GetProviderId(MetadataProviders.MusicBrainzArtist);

            if (!string.IsNullOrEmpty(artistMusicBrainzId))
            {
                await EnsureArtistJson(artistMusicBrainzId, cancellationToken).ConfigureAwait(false);

                var artistJsonPath = GetArtistJsonPath(_config.CommonApplicationPaths, artistMusicBrainzId);

                try
                {
                    AddImages(list, artistJsonPath, cancellationToken);
                }
                catch (FileNotFoundException)
                {

                }
                catch (IOException)
                {

                }
            }

            var language = item.GetPreferredMetadataLanguage();

            var isLanguageEn = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);

            // Sort first by width to prioritize HD versions
            return list.OrderByDescending(i => i.Width ?? 0)
                .ThenByDescending(i =>
                {
                    if (string.Equals(language, i.Language, StringComparison.OrdinalIgnoreCase))
                    {
                        return 3;
                    }
                    if (!isLanguageEn)
                    {
                        if (string.Equals("en", i.Language, StringComparison.OrdinalIgnoreCase))
                        {
                            return 2;
                        }
                    }
                    if (string.IsNullOrEmpty(i.Language))
                    {
                        return isLanguageEn ? 3 : 2;
                    }
                    return 0;
                })
                .ThenByDescending(i => i.CommunityRating ?? 0)
                .ThenByDescending(i => i.VoteCount ?? 0);
        }

        /// <summary>
        /// Adds the images.
        /// </summary>
        /// <param name="list">The list.</param>
        /// <param name="path">The path.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private void AddImages(List<RemoteImageInfo> list, string path, CancellationToken cancellationToken)
        {
            var obj = _jsonSerializer.DeserializeFromFile<ArtistResponse>(path);

            PopulateImages(list, obj.artistbackground, ImageType.Backdrop, 1920, 1080);
            PopulateImages(list, obj.artistthumb, ImageType.Primary, 500, 281);
            PopulateImages(list, obj.hdmusiclogo, ImageType.Logo, 800, 310);
            PopulateImages(list, obj.musicbanner, ImageType.Banner, 1000, 185);
            PopulateImages(list, obj.musiclogo, ImageType.Logo, 400, 155);
            PopulateImages(list, obj.hdmusicarts, ImageType.Art, 1000, 562);
            PopulateImages(list, obj.musicarts, ImageType.Art, 500, 281);
        }

        private void PopulateImages(List<RemoteImageInfo> list,
            List<ArtistImage> images,
            ImageType type,
            int width,
            int height)
        {
            if (images == null)
            {
                return;
            }

            list.AddRange(images.Select(i =>
            {
                var url = i.url;

                if (!string.IsNullOrEmpty(url))
                {
                    var likesString = i.likes;

                    var info = new RemoteImageInfo
                    {
                        RatingType = RatingType.Likes,
                        Type = type,
                        Width = width,
                        Height = height,
                        ProviderName = Name,
                        Url = url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase),
                        Language = i.lang
                    };

                    if (!string.IsNullOrEmpty(likesString) && int.TryParse(likesString, NumberStyles.Integer, _usCulture, out var likes))
                    {
                        info.CommunityRating = likes;
                    }

                    return info;
                }

                return null;
            }).Where(i => i != null));
        }

        public int Order => 0;

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClient.GetResponse(new HttpRequestOptions
            {
                CancellationToken = cancellationToken,
                Url = url
            });
        }

        internal Task EnsureArtistJson(string musicBrainzId, CancellationToken cancellationToken)
        {
            var jsonPath = GetArtistJsonPath(_config.ApplicationPaths, musicBrainzId);

            var fileInfo = _fileSystem.GetFileSystemInfo(jsonPath);

            if (fileInfo.Exists)
            {
                if ((DateTime.UtcNow - _fileSystem.GetLastWriteTimeUtc(fileInfo)).TotalDays <= 2)
                {
                    return Task.CompletedTask;
                }
            }

            return DownloadArtistJson(musicBrainzId, cancellationToken);
        }

        /// <summary>
        /// Downloads the artist data.
        /// </summary>
        /// <param name="musicBrainzId">The music brainz id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{System.Boolean}.</returns>
        internal async Task DownloadArtistJson(string musicBrainzId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = string.Format(Plugin.BaseUrl, Plugin.ApiKey, musicBrainzId, "music");

            var clientKey = Plugin.Instance.Configuration.PersonalApiKey;
            if (!string.IsNullOrWhiteSpace(clientKey))
            {
                url += "&client_key=" + clientKey;
            }

            var jsonPath = GetArtistJsonPath(_config.ApplicationPaths, musicBrainzId);

            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath));

            try
            {
                using (var httpResponse = await _httpClient.SendAsync(new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = cancellationToken,
                    BufferContent = true

                }, "GET").ConfigureAwait(false))
                {
                    using (var response = httpResponse.Content)
                    {
                        using (var saveFileStream = _fileSystem.GetFileStream(jsonPath, FileOpenMode.Create, FileAccessMode.Write, FileShareMode.Read, true))
                        {
                            await response.CopyToAsync(saveFileStream).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (HttpException ex)
            {
                if (ex.StatusCode.HasValue && ex.StatusCode.Value == HttpStatusCode.NotFound)
                {
                    _jsonSerializer.SerializeToFile(new ArtistResponse(), jsonPath);
                }
                else
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Gets the artist data path.
        /// </summary>
        /// <param name="appPaths">The application paths.</param>
        /// <param name="musicBrainzArtistId">The music brainz artist identifier.</param>
        /// <returns>System.String.</returns>
        private static string GetArtistDataPath(IApplicationPaths appPaths, string musicBrainzArtistId)
        {
            var dataPath = Path.Combine(GetArtistDataPath(appPaths), musicBrainzArtistId);

            return dataPath;
        }

        /// <summary>
        /// Gets the artist data path.
        /// </summary>
        /// <param name="appPaths">The application paths.</param>
        /// <returns>System.String.</returns>
        internal static string GetArtistDataPath(IApplicationPaths appPaths)
        {
            var dataPath = Path.Combine(appPaths.CachePath, "fanart-music");

            return dataPath;
        }

        internal static string GetArtistJsonPath(IApplicationPaths appPaths, string musicBrainzArtistId)
        {
            var dataPath = GetArtistDataPath(appPaths, musicBrainzArtistId);

            return Path.Combine(dataPath, "fanart.json");
        }


        public class ArtistImage
        {
            public string id { get; set; }
            public string url { get; set; }
            public string likes { get; set; }
            public string disc { get; set; }
            public string size { get; set; }
            public string lang { get; set; }
        }

        public class Album
        {
            public string release_group_id { get; set; }
            public List<ArtistImage> cdart { get; set; }
            public List<ArtistImage> albumcover { get; set; }
        }

        public class ArtistResponse
        {
            public string name { get; set; }
            public string mbid_id { get; set; }
            public List<ArtistImage> artistthumb { get; set; }
            public List<ArtistImage> artistbackground { get; set; }
            public List<ArtistImage> hdmusiclogo { get; set; }
            public List<ArtistImage> musicbanner { get; set; }
            public List<ArtistImage> musiclogo { get; set; }
            public List<ArtistImage> musicarts { get; set; }
            public List<ArtistImage> hdmusicarts { get; set; }
            public List<Album> albums { get; set; }
        }
    }
}
