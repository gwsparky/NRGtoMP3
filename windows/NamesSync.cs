using System;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NrgToMp3Win
{
    /// <summary>Keeps names files in a GitHub repository's "names" folder ("names/Church Music.txt"), so they can be
    /// edited on github.com and picked up by both apps. Downloading needs no key for a public repository;
    /// uploading needs a GitHub access key ("personal access token") with permission to change its contents.</summary>
    static class NamesSync
    {
        public const string DefaultRepo = "gwsparky/NRGtoMP3";
        public const string Folder = "names";

        static readonly HttpClient http = new() { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(20) };

        static NamesSync()
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NRGtoMP3/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        /// <summary>A names file on GitHub: its text and GitHub's version id (needed to replace it).</summary>
        public record Remote(string Text, string Sha);

        public static string RemotePath(string imagePath) => $"{Folder}/{System.IO.Path.GetFileNameWithoutExtension(imagePath)}.txt";

        static string Url(string repo, string path) =>
            $"repos/{repo.Trim().Trim('/')}/contents/{string.Join("/", Array.ConvertAll(path.Split('/'), Uri.EscapeDataString))}";

        static HttpRequestMessage Request(HttpMethod method, string repo, string path, string token)
        {
            var req = new HttpRequestMessage(method, Url(repo, path));
            if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new("Bearer", token.Trim());
            return req;
        }

        static Exception Problem(HttpStatusCode code, string body) => (int)code switch
        {
            401 => new Exception("GitHub didn't accept the access key. Check it in GitHub Settings…"),
            403 when body.Contains("rate limit") => new Exception("GitHub is limiting requests for now. Try again in an hour, or add an access key."),
            403 or 404 => new Exception("The access key isn't allowed to change this repository (it needs \"Contents: Read and write\")."),
            409 or 422 => new Exception("The file changed on GitHub at the same moment. Try again."),
            _ => new Exception($"GitHub answered with error {(int)code}."),
        };

        /// <summary>The names file for this image on GitHub, or null if there isn't one.</summary>
        public static async Task<Remote> Download(string repo, string token, string imagePath)
        {
            using var resp = await http.SendAsync(Request(HttpMethod.Get, repo, RemotePath(imagePath), token));
            var body = await resp.Content.ReadAsStringAsync();
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode) throw Problem(resp.StatusCode, body);
            using var json = JsonDocument.Parse(body);
            var b64 = json.RootElement.GetProperty("content").GetString().Replace("\n", "").Replace("\r", "");
            var bytes = Convert.FromBase64String(b64);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch { text = Encoding.Latin1.GetString(bytes); }
            return new Remote(text.TrimStart('﻿'), json.RootElement.GetProperty("sha").GetString());
        }

        /// <summary>Puts the names file on GitHub, replacing the one there. Returns false if GitHub already had exactly this.</summary>
        public static async Task<bool> Upload(string repo, string token, string imagePath, string text)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new Exception("Uploading needs a GitHub access key. Add one in GitHub Settings…");
            var existing = await Download(repo, token, imagePath);
            if (existing != null && Same(existing.Text, text)) return false;
            var payload = JsonSerializer.Serialize(new
            {
                message = $"Song names for {System.IO.Path.GetFileName(imagePath)}",
                content = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n"))),
                sha = existing?.Sha,
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            using var req = Request(HttpMethod.Put, repo, RemotePath(imagePath), token);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) throw Problem(resp.StatusCode, await resp.Content.ReadAsStringAsync());
            return true;
        }

        /// <summary>The same names, ignoring line endings, the BOM and trailing spaces (Windows and github.com differ in these).</summary>
        public static bool Same(string a, string b)
        {
            static string Norm(string s) => string.Join("\n", (s ?? "").TrimStart('﻿').Replace("\r", "").Split('\n')
                .Select(l => l.TrimEnd())).Trim();
            return Norm(a) == Norm(b);
        }
    }
}
