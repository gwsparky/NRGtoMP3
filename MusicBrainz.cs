using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NrgToMp3Win
{
    /// <summary>The disc's table of contents in CD frames (first song at 150), MusicBrainz style.</summary>
    record Toc(bool Exact, int First, int Last, int Leadout, int[] Offsets);

    record Release(string Id, string Title, string Artist, string Date, string Country, int TrackCount);

    /// <summary>Looks up album and song names on musicbrainz.org. Only the disc layout or the search words are sent.</summary>
    static class MusicBrainz
    {
        static readonly HttpClient http = new() { BaseAddress = new Uri("https://musicbrainz.org/ws/2/"), Timeout = TimeSpan.FromSeconds(20) };
        static DateTime last = DateTime.MinValue;

        static MusicBrainz() => http.DefaultRequestHeaders.UserAgent.ParseAdd("NRGtoMP3/1.0 (personal CD archiving tool)");

        public static string DiscId(Toc toc)
        {
            var sb = new StringBuilder($"{toc.First:X2}{toc.Last:X2}{toc.Leadout:X8}");
            for (int i = 0; i < 99; i++) sb.Append((i < toc.Offsets.Length ? toc.Offsets[i] : 0).ToString("X8"));
            var hash = SHA1.HashData(Encoding.ASCII.GetBytes(sb.ToString()));
            return Convert.ToBase64String(hash).Replace('+', '.').Replace('/', '_').Replace('=', '-');
        }

        static async Task<JsonElement?> Get(string path)
        {
            var wait = TimeSpan.FromSeconds(1.1) - (DateTime.Now - last);   // MusicBrainz asks for at most one request a second
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            last = DateTime.Now;
            using var resp = await http.GetAsync(path + (path.Contains('?') ? "&" : "?") + "fmt=json");
            if ((int)resp.StatusCode == 404) return null;
            if (!resp.IsSuccessStatusCode) throw new Exception($"MusicBrainz answered with error {(int)resp.StatusCode}. Try again in a moment.");
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        }

        static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        static string Artist(JsonElement e) =>
            e.TryGetProperty("artist-credit", out var ac) && ac.ValueKind == JsonValueKind.Array
                ? string.Concat(ac.EnumerateArray().Select(a => Str(a, "name") + Str(a, "joinphrase")))
                : "";

        static Release ToRelease(JsonElement r)
        {
            int count = r.TryGetProperty("track-count", out var tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetInt32()
                : r.TryGetProperty("media", out var m) ? m.EnumerateArray().Sum(x => x.TryGetProperty("track-count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0) : 0;
            return new Release(Str(r, "id"), Str(r, "title"), Artist(r), Str(r, "date"), Str(r, "country"), count);
        }

        static List<Release> Releases(JsonElement? d) =>
            d is JsonElement e && e.TryGetProperty("releases", out var rs) ? rs.EnumerateArray().Select(ToRelease).ToList() : new();

        /// <summary>Real disc layouts: exact match only. Layouts guessed from silences: near matches.</summary>
        public static async Task<List<Release>> Lookup(Toc toc)
        {
            string q = "?cdstubs=no&inc=artist-credits";
            if (!toc.Exact)
                q += "&toc=" + Uri.EscapeDataString(string.Join(" ", new[] { toc.First, toc.Last, toc.Leadout }.Concat(toc.Offsets)));
            return Releases(await Get($"discid/{(toc.Exact ? DiscId(toc) : "-")}{q}"));
        }

        public static async Task<List<Release>> Search(string album, string artist)
        {
            var parts = new List<string>();
            if (album != "") parts.Add($"release:\"{album.Replace("\"", " ")}\"");
            if (artist != "") parts.Add($"artist:\"{artist.Replace("\"", " ")}\"");
            if (parts.Count == 0) return new();
            return Releases(await Get("release?limit=25&query=" + Uri.EscapeDataString(string.Join(" AND ", parts))));
        }

        /// <summary>Song names of a release (for multi-disc sets, the disc with the same number of songs).</summary>
        public static async Task<(string album, string artist, List<string> titles)> Tracks(string releaseId, int wanted)
        {
            var d = await Get($"release/{releaseId}?inc=recordings+artist-credits") ?? throw new Exception("Release not found.");
            var media = d.TryGetProperty("media", out var m) ? m.EnumerateArray().ToList() : new();
            var medium = media.FirstOrDefault(x => x.TryGetProperty("tracks", out var t) && t.GetArrayLength() == wanted);
            if (medium.ValueKind == JsonValueKind.Undefined && media.Count > 0) medium = media[0];
            var titles = medium.ValueKind != JsonValueKind.Undefined && medium.TryGetProperty("tracks", out var tr)
                ? tr.EnumerateArray().Select(x => Str(x, "title")).ToList() : new List<string>();
            return (Str(d, "title"), Artist(d), titles);
        }
    }
}
