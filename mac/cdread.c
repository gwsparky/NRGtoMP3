#include "cdread.h"
#include <errno.h>
#include <fcntl.h>
#include <string.h>
#include <sys/ioctl.h>
#include <IOKit/storage/IOCDMediaBSDClient.h>

int cd_open(const char *device) { return open(device, O_RDONLY); }

int cd_read_audio(int fd, uint64_t lba, uint32_t sectors, void *buffer) {
    dk_cd_read_t r;
    memset(&r, 0, sizeof r);
    r.offset = lba * kCDSectorSizeCDDA;
    r.sectorArea = kCDSectorAreaUser;
    r.sectorType = kCDSectorTypeCDDA;
    r.bufferLength = sectors * kCDSectorSizeCDDA;
    r.buffer = buffer;
    if (ioctl(fd, DKIOCCDREAD, &r) < 0) return errno ? errno : EIO;
    return r.bufferLength == sectors * kCDSectorSizeCDDA ? 0 : EIO;
}

int cd_speed(int fd) {
    uint16_t s = 0;
    return ioctl(fd, DKIOCCDGETSPEED, &s) == 0 ? s : 0;
}
