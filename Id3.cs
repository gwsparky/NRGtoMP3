using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NrgToMp3
{
    static class Id3
    {
        /// <summary>Writes an ID3v2.3 tag (UTF-16 text frames) followed by the MP3 audio.</summary>
        public static void WriteTagged(string mp3In, string mp3Out, IList<(string id, string text)> tags)
        {
            var frames = new MemoryStream();
            foreach (var (fid, text) in tags)
            {
                var body = new byte[] { 1, 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(text)).ToArray();
                frames.Write(Encoding.ASCII.GetBytes(fid), 0, 4);
                frames.Write(new[] { (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length }, 0, 4);
                frames.Write(new byte[2], 0, 2);   // flags
                frames.Write(body, 0, body.Length);
            }
            long size = frames.Length;   // header size is "syncsafe": 7 bits per byte
            var header = new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0,
                (byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F) };

            using (var outFile = File.Create(mp3Out))
            using (var inFile = File.OpenRead(mp3In))
            {
                outFile.Write(header, 0, header.Length);
                frames.WriteTo(outFile);
                inFile.CopyTo(outFile);
            }
        }
    }
}
