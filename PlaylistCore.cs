using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using NrgToMp3;

namespace NrgToMp3Win
{
    /// <summary>One song in the Player's playlist: an audio file, or a song inside a disc image.</summary>
    class PlayerSong
    {
        public Guid Id = Guid.NewGuid();
        public string Title, Note;
        public double Seconds;
        public double? Gap;
        public string File;                 // an audio file
        public string Image;                // or the audio file of a disc image (.nrg, or a .cue's .bin)
        public PlaylistTrack Track;
        public bool Swap;

        public Nrg.Track NrgTrack => new() { Number = Track.number, SectorSize = Track.sectorSize, Mode = 7, Start = Track.start, End = Track.start + Track.sectors * Track.sectorSize };
        public string Source => Note ?? (File != null ? Path.GetFileName(File) : $"{Path.GetFileName(Image)}, track {Track.number}");
    }

    // The ".nrgplaylist" file (JSON), the same format the Mac app uses, so playlists open on either.
    class PlaylistTrack
    {
        public int number { get; set; }
        public string title { get; set; }
        public string performer { get; set; }
        public double seconds { get; set; }
        public long start { get; set; }
        public long sectors { get; set; }
        public int sectorSize { get; set; }
        public double? gap { get; set; }
    }

    class PlaylistSongJson
    {
        public string title { get; set; }
        public double? gap { get; set; }
        public string note { get; set; }
        public string file { get; set; }
        public string image { get; set; }
        public PlaylistTrack track { get; set; }
        public bool? swap { get; set; }
    }

    class PlaylistJson
    {
        public int version { get; set; } = 1;
        public string title { get; set; } = "";
        public double defaultGap { get; set; } = 2;
        public List<PlaylistSongJson> songs { get; set; } = new();
    }

    static class Playlists
    {
        static readonly JsonSerializerOptions options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        /// <summary>Every song of a disc image, ready for the playlist.</summary>
        public static List<PlayerSong> FromImage(DiscItem d) => d.Songs.Select(t => new PlayerSong
        {
            Title = d.Name(t), Seconds = t.Sectors / 75.0, Image = d.Nrg.DataPath, Swap = d.Swap,
            Track = new PlaylistTrack { number = t.Number, title = d.Name(t), seconds = t.Sectors / 75.0, start = t.Start, sectors = t.Sectors, sectorSize = t.SectorSize },
        }).ToList();

        public static void Save(string path, string title, double defaultGap, IEnumerable<PlayerSong> songs)
        {
            var file = new PlaylistJson
            {
                title = title ?? "", defaultGap = defaultGap,
                songs = songs.Select(s => new PlaylistSongJson
                {
                    title = s.Title, gap = s.Gap, note = s.Note, file = s.File, image = s.Image, track = s.Track,
                    swap = s.Image != null ? s.Swap : null,
                }).ToList(),
            };
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(file, options));
        }

        /// <summary>Reads a playlist. Songs whose audio can't be found (moved, or on a drive that isn't connected)
        /// are listed in <paramref name="missing"/> and left out.</summary>
        public static (string title, double defaultGap, List<PlayerSong> songs) Load(string path, out List<string> missing, Func<string, double> secondsOfFile)
        {
            var file = JsonSerializer.Deserialize<PlaylistJson>(System.IO.File.ReadAllText(path)) ?? new PlaylistJson();
            string here = Path.GetDirectoryName(Path.GetFullPath(path));
            // the saved path; or, if the playlist and its files were moved together, the same name next to the playlist
            string Find(string p)
            {
                if (string.IsNullOrEmpty(p)) return null;
                if (System.IO.File.Exists(p)) return p;
                string name = Path.GetFileName(p.Replace('\\', '/').TrimEnd('/')),
                       parent = Path.GetFileName(Path.GetDirectoryName(p.Replace('\\', '/')) ?? "");
                foreach (var c in new[] { Path.Combine(here, name), Path.Combine(here, parent, name) })
                    if (System.IO.File.Exists(c)) return c;
                return null;
            }
            missing = new();
            var songs = new List<PlayerSong>();
            foreach (var s in file.songs)
            {
                if (s.file != null && Find(s.file) is string f)
                    songs.Add(new PlayerSong { Title = s.title, Note = s.note, Gap = s.gap, File = f, Seconds = secondsOfFile(f) });
                else if (s.image != null && s.track != null && Find(s.image) is string img)
                    songs.Add(new PlayerSong { Title = s.title, Note = s.note, Gap = s.gap, Image = img, Track = s.track, Swap = s.swap ?? false, Seconds = s.track.sectors / 75.0 });
                else
                    missing.Add(s.title);
            }
            return (file.title, file.defaultGap, songs);
        }
    }
}
