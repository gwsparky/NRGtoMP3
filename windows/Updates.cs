using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NrgToMp3Win
{
    /// <summary>
    /// Checks GitHub Releases (gwsparky/NRGtoMP3) for a newer ChurchSound. Release tags are the version: "v26.11.08", "v26.11.08-V3".
    /// Uses the saved GitHub key when there is one, so it still works if the project is made private. Cross-platform.
    /// </summary>
    static class Updates
    {
        public const string Repo = "gwsparky/NRGtoMP3";

        /// <summary>"26.11.08 V3" → [26, 11, 8, 3]; a plain date is V1.</summary>
        public static int[] Parts(string v)
        {
            var n = Regex.Matches(v ?? "", @"\d+").Select(m => int.Parse(m.Value)).ToList();
            while (n.Count < 4) n.Add(1);
            return n.ToArray();
        }

        public static bool IsNewer(string a, string b)
        {
            int[] x = Parts(a), y = Parts(b);
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++) if (x[i] != y[i]) return x[i] > y[i];
            return x.Length > y.Length;
        }

        /// <summary>The newest release: its version ("26.11.08 V3") and web page, or an error message.</summary>
        public static async Task<(string version, string page, string error)> Latest(string token)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
                req.Headers.UserAgent.ParseAdd("ChurchSound");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new("Bearer", token.Trim());
                using var resp = await http.SendAsync(req);
                if ((int)resp.StatusCode == 404) return (null, null, "Couldn't see the releases on GitHub. If the project is private, add your GitHub key with GitHub… on the Convert tab.");
                if (!resp.IsSuccessStatusCode) return (null, null, $"GitHub answered {(int)resp.StatusCode}.");
                using var j = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var tag = j.RootElement.GetProperty("tag_name").GetString() ?? "";
                return (tag.TrimStart('v', 'V').Replace('-', ' '), j.RootElement.GetProperty("html_url").GetString(), null);
            }
            catch (Exception e) { return (null, null, e.Message); }
        }

        public static void OpenPage(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
