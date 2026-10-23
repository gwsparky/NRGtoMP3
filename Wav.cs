using System.IO;
using System.Text;

namespace NrgToMp3
{
    static class Wav
    {
        /// <summary>Wraps raw 44.1 kHz / 16-bit / stereo little-endian PCM in a WAV file, with a LIST/INFO chunk for the song details.</summary>
        public static void Write(string pcmPath, string wavPath, string title, string artist, string album, string track)
        {
            var info = new MemoryStream();
            info.Write(Encoding.ASCII.GetBytes("INFO"));
            foreach (var (id, text) in new[] { ("INAM", title), ("IART", artist), ("IPRD", album), ("ITRK", track) })
            {
                if (string.IsNullOrEmpty(text)) continue;
                var body = Encoding.UTF8.GetBytes(text + "\0");
                info.Write(Encoding.ASCII.GetBytes(id));
                info.Write(System.BitConverter.GetBytes(body.Length));
                info.Write(body);
                if (body.Length % 2 == 1) info.WriteByte(0);   // chunks are word-aligned
            }

            long dataLen = new FileInfo(pcmPath).Length;
            using (var w = new BinaryWriter(File.Create(wavPath)))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write((uint)(4 + 8 + 16 + 8 + info.Length + 8 + dataLen));
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt "));
                w.Write(16);
                w.Write((short)1);          // PCM
                w.Write((short)2);          // stereo
                w.Write(44100);
                w.Write(44100 * 4);         // bytes per second
                w.Write((short)4);          // bytes per sample frame
                w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("LIST"));
                w.Write((uint)info.Length);
                w.Write(info.ToArray());
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write((uint)dataLen);
                w.Flush();
                using (var pcm = File.OpenRead(pcmPath)) pcm.CopyTo(w.BaseStream);
            }
        }
    }
}
