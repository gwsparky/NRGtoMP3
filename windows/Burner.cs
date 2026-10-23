using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace NrgToMp3Win
{
    /// <summary>Burns audio CDs with Windows' own burning service (IMAPI2, part of Windows 7 and later).
    /// Uses "raw" disc-at-once writing, so the songs sit exactly as far apart as chosen (no gap = seamless);
    /// Windows' simpler track-at-once mode always puts 2 seconds between songs.</summary>
    static class Burner
    {
        public record Drive(string Id, string Name);

        // IMAPI2 values
        const int SectorAudio = 0;                                  // IMAPI_CD_SECTOR_AUDIO
        static readonly int[] SectorTypes = { 3, 1 };               // IMAPI_FORMAT2_RAW_CD_SUBCODE_IS_RAW, then _PQ_ONLY

        static dynamic Create(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, true));

        /// <summary>Runs IMAPI2 work on its own single-threaded COM thread, as IMAPI2 expects.</summary>
        static T OnComThread<T>(Func<T> work)
        {
            T result = default;
            Exception error = null;
            var t = new Thread(() => { try { result = work(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) throw error;
            return result;
        }

        /// <summary>The drives that can write audio CDs this way.</summary>
        public static List<Drive> Drives() => OnComThread(() =>
        {
            var list = new List<Drive>();
            dynamic master = Create("IMAPI2.MsftDiscMaster2");
            if (!master.IsSupportedEnvironment) return list;
            for (int i = 0; i < (int)master.Count; i++)
            {
                try
                {
                    string id = master[i];
                    dynamic rec = Create("IMAPI2.MsftDiscRecorder2");
                    rec.InitializeDiscRecorder(id);
                    dynamic fmt = Create("IMAPI2.MsftDiscFormat2RawCD");
                    if (!fmt.IsRecorderSupported(rec)) continue;
                    string letters = "";
                    try { foreach (string v in (object[])rec.VolumePathNames) letters += v.TrimEnd('\\') + " "; } catch { }
                    list.Add(new Drive(id, $"{letters}{((string)rec.VendorId).Trim()} {((string)rec.ProductId).Trim()}".Trim()));
                }
                catch { }
            }
            return list;
        });

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
        static extern void SHCreateStreamOnFileEx(string file, uint mode, uint attributes, bool create, IntPtr template, out IntPtr stream);

        static object Stream(string path)
        {
            SHCreateStreamOnFileEx(path, 0x20 /* STGM_READ | STGM_SHARE_DENY_WRITE */, 0, false, IntPtr.Zero, out var p);
            try { return Marshal.GetObjectForIUnknown(p); } finally { Marshal.Release(p); }
        }

        static string Message(Exception ex)
        {
            int code = ex is COMException c ? c.HResult : ex.InnerException is COMException ic ? ic.HResult : 0;
            return unchecked((uint)code) switch
            {
                0xC0AA0202 => "There's no disc in the burner. Insert a blank CD-R and try again.",
                0xC0AA0205 or 0xC0AA0206 => "The disc in the burner isn't blank, or can't be written. Insert a blank CD-R.",
                0xC0AA0207 or 0xC0AA0208 => "The disc in the burner can't be used for an audio CD. Insert a blank CD-R.",
                0xC0AA0210 => "Another program is using the burner. Close it and try again.",
                0xC0AA0403 or 0xC0AA0404 => "The songs don't fit on this disc.",
                _ => ex.Message + (code != 0 ? $" (code {unchecked((uint)code):X8})" : ""),
            };
        }

        /// <summary>Thrown when the drive won't burn with exact gaps; the standard (2-second gap) way may still work.</summary>
        public class ExactGapsFailed : Exception { public ExactGapsFailed(string m) : base(m) { } }

        /// <summary>Burns one raw CD-audio file per track (each already followed by the gap before the next song).
        /// The first song gets the standard 2-second lead-in. Tries each raw layout the drive offers; throws
        /// <see cref="ExactGapsFailed"/> if none works (nothing has been written then), other errors as readable messages.</summary>
        public static void Burn(string driveId, IList<string> trackFiles, bool eject, Action<string> status)
        {
            OnComThread<object>(() =>
            {
                dynamic rec = Create("IMAPI2.MsftDiscRecorder2");
                rec.InitializeDiscRecorder(driveId);
                dynamic probe = Create("IMAPI2.MsftDiscFormat2RawCD");
                probe.Recorder = rec;
                try
                {
                    if (!probe.IsCurrentMediaSupported(rec)) throw new InvalidOperationException("The disc in the burner can't be used for an audio CD. Insert a blank CD-R.");
                }
                catch (COMException ex) { throw new InvalidOperationException(Message(ex)); }

                // the raw layouts this drive says it can write, best first; then the others, as some drives misreport
                var supported = new List<int>();
                try { foreach (object v in (object[])probe.SupportedSectorTypes) supported.Add(Convert.ToInt32(v)); } catch { }
                var order = SectorTypes.Where(supported.Contains).Concat(SectorTypes.Where(t => !supported.Contains(t))).ToList();

                string lastError = null;
                foreach (int type in order)
                {
                    // a fresh burner object for each try, so nothing is left over from a failed one
                    dynamic fmt = Create("IMAPI2.MsftDiscFormat2RawCD");
                    fmt.Recorder = rec;
                    fmt.ClientName = "NRG to MP3";
                    bool writing = false;
                    try
                    {
                        status("Getting the disc ready…");
                        fmt.RequestedSectorType = type;
                        fmt.PrepareMedia();
                        try
                        {
                            status("Building the disc layout…");
                            dynamic image = Create("IMAPI2.MsftRawCDImageCreator");
                            image.ResultingImageType = type;
                            image.DisableGaplessAudio = false;    // songs follow each other exactly; the gaps are in the audio itself
                            foreach (var f in trackFiles) image.AddTrack(SectorAudio, Stream(f));
                            object result = image.CreateResultImage();
                            status("Burning… this takes a few minutes. Don't open the drive.");
                            writing = true;
                            fmt.WriteMedia(result);
                        }
                        finally { try { fmt.ReleaseMedia(); } catch { } }
                        if (eject) try { rec.EjectMedia(); } catch { }
                        return null;
                    }
                    catch (COMException ex)
                    {
                        lastError = Message(ex);
                        uint code = unchecked((uint)ex.HResult);
                        // once writing has really started, the disc is used: don't try again on it
                        if (writing && code != 0xC0AA0602) throw new InvalidOperationException(lastError + " (while burning)");
                    }
                }
                throw new ExactGapsFailed(lastError ?? "This burner can't burn with exact gaps.");
            });
        }

        /// <summary>Windows' standard way to burn audio CDs (track-at-once): works on nearly every burner, but always puts
        /// 2 seconds of silence between songs. <paramref name="songFiles"/> are the songs alone, without gaps.</summary>
        public static void BurnStandard(string driveId, IList<string> songFiles, bool eject, Action<string> status)
        {
            OnComThread<object>(() =>
            {
                dynamic rec = Create("IMAPI2.MsftDiscRecorder2");
                rec.InitializeDiscRecorder(driveId);
                dynamic fmt = Create("IMAPI2.MsftDiscFormat2TrackAtOnce");
                fmt.Recorder = rec;
                fmt.ClientName = "NRG to MP3";
                int song = 0;
                try
                {
                    status("Getting the disc ready…");
                    fmt.PrepareMedia();
                    try
                    {
                        foreach (var f in songFiles)
                        {
                            song++;
                            status($"Burning song {song} of {songFiles.Count}… Don't open the drive.");
                            fmt.AddAudioTrack(Stream(f));
                        }
                    }
                    finally { status("Finishing the disc…"); try { fmt.ReleaseMedia(); } catch { } }
                }
                catch (COMException ex) { throw new InvalidOperationException(Message(ex) + (song > 0 ? $" (while burning song {song})" : " (while getting the disc ready)")); }
                if (eject) try { rec.EjectMedia(); } catch { }
                return null;
            });
        }
    }
}
