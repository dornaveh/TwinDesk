#include "TwinDeskAudioShared.h"

#include <errno.h>
#include <fcntl.h>
#include <mach/mach_time.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

static pthread_mutex_t gProducerMutex = PTHREAD_MUTEX_INITIALIZER;
static int gProducerFD = -1;
static TwinDeskAudioRing *gProducerRing = NULL;

int twindesk_mic_open(void) {
    pthread_mutex_lock(&gProducerMutex);
    if (gProducerRing != NULL) {
        gProducerRing->producing = 1;
        pthread_mutex_unlock(&gProducerMutex);
        return 0;
    }

    // The driver runs as _coreaudiod in a separate sandbox. Match the proven
    // AudioServerPlugIn pattern: make this audio-only ring accessible to that
    // process after creation, regardless of the app's umask.
    // Darwin does not support fchmod on POSIX shared-memory descriptors. Set
    // permissions at creation, then immediately restore the process umask.
    mode_t previousMask = umask(0);
    int fd = shm_open(TWINDESK_AUDIO_SHM_NAME, O_CREAT | O_EXCL | O_RDWR, 0666);
    int openError = errno;
    umask(previousMask);
    errno = openError;
    if (fd >= 0) {
        // macOS permits sizing a POSIX shared-memory object only on creation.
        if (ftruncate(fd, (off_t)sizeof(TwinDeskAudioRing)) != 0) {
            int result = errno;
            close(fd);
            shm_unlink(TWINDESK_AUDIO_SHM_NAME);
            pthread_mutex_unlock(&gProducerMutex);
            return result;
        }
    } else {
        if (errno != EEXIST) {
            int result = errno;
            pthread_mutex_unlock(&gProducerMutex);
            return result;
        }
        fd = shm_open(TWINDESK_AUDIO_SHM_NAME, O_RDWR, 0666);
        if (fd < 0) {
            int result = errno;
            pthread_mutex_unlock(&gProducerMutex);
            return result;
        }
    }
    struct stat info;
    // macOS rounds POSIX shared-memory sizes up to a VM page boundary.
    if (fstat(fd, &info) != 0 || info.st_size < (off_t)sizeof(TwinDeskAudioRing)) {
        close(fd);
        pthread_mutex_unlock(&gProducerMutex);
        return EINVAL;
    }
    // Do not silently accept an older restrictive ring: Core Audio runs as
    // _coreaudiod and must be able to advance the consumer cursor.
    if ((info.st_mode & 0666) != 0666) {
        close(fd);
        pthread_mutex_unlock(&gProducerMutex);
        return EACCES;
    }
    void *mapping = mmap(NULL, sizeof(TwinDeskAudioRing), PROT_READ | PROT_WRITE,
                         MAP_SHARED, fd, 0);
    if (mapping == MAP_FAILED) {
        int result = errno;
        close(fd);
        pthread_mutex_unlock(&gProducerMutex);
        return result;
    }

    gProducerFD = fd;
    gProducerRing = (TwinDeskAudioRing *)mapping;
    if (!twindesk_ring_ready(gProducerRing)) {
        twindesk_ring_init(gProducerRing);
    }
    gProducerRing->producing = 1;
    pthread_mutex_unlock(&gProducerMutex);
    return 0;
}

uint32_t twindesk_mic_write_s16le(const void *bytes, uint32_t byteCount,
                                  float *outPeak, uint32_t *outBufferedFrames) {
    if (outPeak != NULL) { *outPeak = 0; }
    if (outBufferedFrames != NULL) { *outBufferedFrames = 0; }
    if (bytes == NULL || byteCount == 0 || byteCount > 19200 || byteCount % 4 != 0) {
        return 0;
    }

    pthread_mutex_lock(&gProducerMutex);
    if (gProducerRing == NULL || !twindesk_ring_ready(gProducerRing)) {
        pthread_mutex_unlock(&gProducerMutex);
        return 0;
    }

    const uint8_t *source = (const uint8_t *)bytes;
    uint32_t sampleCount = byteCount / 2;
    uint32_t frameCount = sampleCount / TWINDESK_AUDIO_CHANNELS;
    float converted[9600];
    float peak = 0;
    for (uint32_t index = 0; index < sampleCount; ++index) {
        uint16_t raw = (uint16_t)source[index * 2]
                     | ((uint16_t)source[index * 2 + 1] << 8);
        int16_t signedSample = (int16_t)raw;
        float sample = (float)signedSample / 32768.0f;
        converted[index] = sample;
        float magnitude = sample < 0 ? -sample : sample;
        if (magnitude > peak) { peak = magnitude; }
    }

    uint32_t written = twindesk_ring_write(gProducerRing, converted, frameCount,
                                            mach_absolute_time());
    if (outPeak != NULL) { *outPeak = peak; }
    if (outBufferedFrames != NULL) {
        *outBufferedFrames = twindesk_ring_filled(gProducerRing);
    }
    pthread_mutex_unlock(&gProducerMutex);
    return written;
}

void twindesk_mic_close(void) {
    pthread_mutex_lock(&gProducerMutex);
    if (gProducerRing != NULL) {
        gProducerRing->producing = 0;
        munmap(gProducerRing, sizeof(TwinDeskAudioRing));
        gProducerRing = NULL;
    }
    if (gProducerFD >= 0) {
        close(gProducerFD);
        gProducerFD = -1;
    }
    pthread_mutex_unlock(&gProducerMutex);
}
