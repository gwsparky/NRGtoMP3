using System;
using System.Collections.Generic;
using System.Text;

namespace NrgToMp3Win
{
    /// <summary>Reads the song names burned on a CD ("CD-Text"), the same way the Mac app does (mac/AudioCD.swift).
    /// Cross-platform, so it can be tested on any OS.</summary>
    static class CdText
    {
        /// <summary>One kind of CD-Text (0x80 = titles, 0x81 = performers) by track number; 0 is the whole disc.
        /// <paramref name="data"/> is the drive's answer: a 4-byte header, then 18-byte packs. Each pack carries 12 bytes
        /// of text; names run on from pack to pack, ending with a zero byte, and a tab means "the same as the previous one".
        /// Only the first language block is read.</summary>
        public static Dictionary<int, string> Strings(byte[] data, int length, byte type)
        {
            var result = new Dictionary<int, string>();
            int track = -1;
            var bytes = new List<byte>();
            string last = "";
            for (int i = 4; i + 18 <= Math.Min(length, data.Length); i += 18)
            {
                if (data[i] != type || ((data[i + 3] >> 4) & 7) != 0) continue;
                if (track < 0) track = data[i + 1] & 0x7F;
                for (int k = i + 4; k < i + 16; k++)
                {
                    if (data[k] != 0) { bytes.Add(data[k]); continue; }
                    var s = Encoding.Latin1.GetString(bytes.ToArray());
                    if (s == "\t") s = last;
                    result[track] = s.Trim();      // zero padding after the last name just adds empty ones past the end
                    last = s; track++;
                    bytes.Clear();
                }
            }
            return result;
        }
    }
}
