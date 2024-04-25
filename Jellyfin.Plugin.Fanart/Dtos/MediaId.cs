using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Fanart.Dtos
{
    public class MediaId
    {
        [JsonPropertyName("anilist_id")]
        public int? AnilistId { get; set; }

        [JsonPropertyName("thetvdb_id")]
        public int? TheTvdbId { get; set; }
    }
}
