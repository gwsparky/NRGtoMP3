// Raw audio-CD reading for Swift (the DKIOCCDREAD ioctl's macros can't be used from Swift directly).
#include <stdint.h>

/// Opens a CD's raw device (e.g. "/dev/rdisk4"). Returns a file descriptor, or -1.
int cd_open(const char *device);

/// Reads `sectors` audio sectors (2352 bytes each, little-endian 16-bit stereo) starting at `lba`.
/// Returns 0 on success, or an errno value.
int cd_read_audio(int fd, uint64_t lba, uint32_t sectors, void *buffer);

/// The drive's current read speed in KB/s (176 = 1x), or 0 if unknown.
int cd_speed(int fd);
