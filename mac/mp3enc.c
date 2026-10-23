// Tiny MP3 encoder bundled with the app so MP3 export works without Homebrew/ffmpeg.
// Usage: mp3enc <input.pcm> <output.mp3> <kbps>
// Input: raw 44.1 kHz, 16-bit, stereo, little-endian PCM (CD audio).
#include <stdio.h>
#include <stdlib.h>
#include <lame/lame.h>

int main(int argc, char **argv) {
    if (argc != 4) { fprintf(stderr, "usage: mp3enc in.pcm out.mp3 kbps\n"); return 2; }
    FILE *in = fopen(argv[1], "rb"), *out = fopen(argv[2], "w+b");
    if (!in || !out) { perror("mp3enc"); return 1; }
    lame_t lame = lame_init();
    lame_set_in_samplerate(lame, 44100);
    lame_set_num_channels(lame, 2);
    lame_set_brate(lame, atoi(argv[3]));
    lame_set_quality(lame, 2);                  // high quality
    lame_set_bWriteVbrTag(lame, 1);             // LAME info frame: lets players skip MP3 padding (gapless playback)
    if (lame_init_params(lame) < 0) { fprintf(stderr, "mp3enc: bad settings\n"); return 1; }

    enum { FRAMES = 8192 };
    static short pcm[FRAMES * 2];
    static unsigned char mp3[FRAMES * 5 / 4 + 7200];
    size_t n;
    while ((n = fread(pcm, 4, FRAMES, in)) > 0) {
        int bytes = lame_encode_buffer_interleaved(lame, pcm, (int)n, mp3, sizeof mp3);
        if (bytes < 0) { fprintf(stderr, "mp3enc: encoding failed (%d)\n", bytes); return 1; }
        fwrite(mp3, 1, bytes, out);
    }
    int bytes = lame_encode_flush(lame, mp3, sizeof mp3);
    if (bytes > 0) fwrite(mp3, 1, bytes, out);
    lame_mp3_tags_fid(lame, out);               // fill in the info frame now the length is known
    lame_close(lame);
    fclose(in);
    return fclose(out) == 0 ? 0 : 1;
}
