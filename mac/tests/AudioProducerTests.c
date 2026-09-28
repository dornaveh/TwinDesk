// Compile with TWINDESK_AUDIO_TEST and a unique TWINDESK_AUDIO_SHM_NAME so this
// test never touches the live microphone ring.
#ifndef TWINDESK_AUDIO_TEST
#error "This test requires an isolated shared-memory name."
#endif
#include "TwinDeskAudioShared.h"
#include <assert.h>
#include <fcntl.h>
#include <stdio.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

int main(void) {
    assert(strcmp(TWINDESK_AUDIO_SHM_NAME, "/twindesk.micring") != 0);
    shm_unlink(TWINDESK_AUDIO_SHM_NAME);
    mode_t savedMask = umask(0022);
    assert(twindesk_mic_open() == 0);
    mode_t restoredMask = umask(0022);
    assert(restoredMask == 0022);
    int fd = shm_open(TWINDESK_AUDIO_SHM_NAME, O_RDWR, 0);
    assert(fd >= 0);
    struct stat info;
    assert(fstat(fd, &info) == 0);
    assert(info.st_size >= sizeof(TwinDeskAudioRing));
    assert((info.st_mode & 0666) == 0666);
    TwinDeskAudioRing *ring = mmap(NULL, sizeof(*ring), PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
    assert(ring != MAP_FAILED && twindesk_ring_ready(ring));
    const unsigned char bytes[] = {0x00, 0x80, 0xff, 0x7f};
    float peak = 0; uint32_t buffered = 0;
    assert(twindesk_mic_write_s16le(bytes, sizeof(bytes), &peak, &buffered) == 1);
    assert(peak == 1 && buffered == 1);
    float output[2];
    assert(twindesk_ring_read(ring, output, 1) == 1);
    assert(output[0] == -1 && output[1] > 0.99);
    twindesk_mic_close();
    assert(ring->producing == 0);
    assert(twindesk_mic_open() == 0); // Reuse a page-rounded, existing object.
    twindesk_mic_close();
    munmap(ring, sizeof(*ring)); close(fd);
    shm_unlink(TWINDESK_AUDIO_SHM_NAME);
    umask(savedMask);
    puts("Audio producer creation, permissions, conversion and reopen tests passed.");
}
