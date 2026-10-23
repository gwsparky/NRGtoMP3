using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NrgToMp3Win
{
    /// <summary>Reads a music CD straight from the drive: its table of contents, and the raw audio sectors
    /// (2352 bytes each, 1/75 s, already in the little-endian order CD images use).</summary>
    class CdReader : IDisposable
    {
        public record Song(int Number, long Lba, long Sectors);

        const uint IOCTL_CDROM_READ_TOC = 0x00024000, IOCTL_CDROM_RAW_READ = 0x0002403E, IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
        const int Sector = 2352, Chunk = 20;          // sectors per read; drives accept at least this many

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

        readonly SafeFileHandle handle;
        public readonly string Letter;                 // "D:"
        public List<Song> Songs = new();
        public long Leadout;                           // first sector after the music

        /// <summary>The drive letters of CD/DVD drives with a disc in them.</summary>
        public static List<string> DrivesWithDiscs() =>
            DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).Where(d => { try { return d.IsReady; } catch { return false; } })
                     .Select(d => d.Name.TrimEnd('\\')).ToList();

        public CdReader(string letter)
        {
            Letter = letter;
            handle = CreateFile(@"\\.\" + letter, 0x80000000 /* GENERIC_READ */, 3 /* share read + write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Couldn't open drive {letter}");
            ReadToc();
        }

        void ReadToc()
        {
            var toc = new byte[804];
            if (!DeviceIoControl(handle, IOCTL_CDROM_READ_TOC, null, 0, toc, toc.Length, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't read the disc's table of contents");
            int first = toc[2], last = toc[3];
            var entries = new List<(int number, bool data, long lba)>();
            for (int i = 0; i <= last - first + 1; i++)       // the tracks, then the lead-out (0xAA)
            {
                int at = 4 + i * 8;
                int control = toc[at + 1] & 0x0F;                           // TRACK_DATA: Control is the low 4 bits, Adr the high 4
                long lba = (toc[at + 5] * 60 + toc[at + 6]) * 75 + toc[at + 7] - 150;   // address is minutes, seconds, frames
                entries.Add((toc[at + 2], (control & 4) != 0, lba));
            }
            Leadout = entries[^1].lba;
            for (int i = 0; i + 1 < entries.Count; i++)
            {
                if (entries[i].data) continue;
                // a music track runs to the next track; before a data track (enhanced CD) there's an 11400-sector session gap
                long end = entries[i + 1].lba - (entries[i + 1].data && entries[i + 1].number != 0xAA ? 11400 : 0);
                if (end > entries[i].lba) Songs.Add(new Song(entries[i].number, entries[i].lba, end - entries[i].lba));
            }
        }

        /// <summary>The music's layout in MusicBrainz terms (first song at 150; a data track at the end is left out, as MusicBrainz does).</summary>
        public Toc Toc => Songs.Count == 0 ? null
            : new(true, Songs[0].Number, Songs[^1].Number, (int)(Songs[^1].Lba + Songs[^1].Sectors) + 150, Songs.Select(s => (int)s.Lba + 150).ToArray());

        /// <summary>Reads <paramref name="count"/> sectors starting at <paramref name="lba"/> into <paramref name="buffer"/>.
        /// Tries a few times (worn discs often read on a second try), then sector by sector.</summary>
        public void Read(long lba, int count, byte[] buffer, int offset = 0)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (TryRead(lba, count, buffer, offset)) return;
                System.Threading.Thread.Sleep(200 * (attempt + 1));
            }
            for (int s = 0; s < count; s++)
            {
                bool ok = false;
                for (int attempt = 0; attempt < 5 && !ok; attempt++) ok = TryRead(lba + s, 1, buffer, offset + s * Sector);
                if (!ok) throw new IOException($"The drive couldn't read the disc at {SongCore.Short((lba + s) / 75.0)}. It may be scratched; try cleaning it and copying again.");
            }
        }

        bool TryRead(long lba, int count, byte[] buffer, int offset)
        {
            var info = new byte[16];
            BitConverter.GetBytes(lba * 2048).CopyTo(info, 0);       // the drive wants a "cooked" byte offset
            BitConverter.GetBytes(count).CopyTo(info, 8);
            BitConverter.GetBytes(2).CopyTo(info, 12);                // CDDA
            var output = offset == 0 && buffer.Length >= count * Sector ? buffer : new byte[count * Sector];
            if (!DeviceIoControl(handle, IOCTL_CDROM_RAW_READ, info, info.Length, output, count * Sector, out int got, IntPtr.Zero) || got < count * Sector)
                return false;
            if (output != buffer) Buffer.BlockCopy(output, 0, buffer, offset, count * Sector);
            return true;
        }

        /// <summary>A whole song as CD audio.</summary>
        public byte[] ReadSong(Song s, Func<bool> cancelled = null)
        {
            var pcm = new byte[s.Sectors * Sector];
            for (long done = 0; done < s.Sectors; done += Chunk)
            {
                if (cancelled?.Invoke() == true) throw new OperationCanceledException();
                int n = (int)Math.Min(Chunk, s.Sectors - done);
                Read(s.Lba + done, n, pcm, (int)(done * Sector));
            }
            return pcm;
        }

        public static void Eject(string letter)
        {
            using var h = CreateFile(@"\\.\" + letter, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!h.IsInvalid) DeviceIoControl(h, IOCTL_STORAGE_EJECT_MEDIA, null, 0, null, 0, out _, IntPtr.Zero);
        }

        public const int ChunkSectors = Chunk;

        public void Dispose() => handle.Dispose();
    }
}
