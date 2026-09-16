/*
 * CHD-backed rc_hash CD reader for Retromind.
 *
 * The track selection and sector-layout handling follows the libchdr adapter
 * used by RetroArch. RetroArch's corresponding chd_stream/cdfs adapter is
 * licensed under the MIT license; see Licenses/RetroArch.MIT.txt.
 */

#include "retromind_chd_reader.h"

#include "rhash/rc_hash_internal.h"
#include "libchdr/chd.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>

#define RETROMIND_CHD_RAW_SECTOR_SIZE 2352U
#define RETROMIND_CHD_DATA_SECTOR_SIZE 2048U
#define RETROMIND_CHD_TRACK_PADDING 4U

typedef struct retromind_chd_metadata
{
    uint32_t physical_frame_offset;
    uint32_t first_sector;
    uint32_t frames;
    uint32_t extra;
    uint32_t pregap;
    uint32_t track;
    char type[64];
    char subtype[32];
    char pregap_type[32];
    char pregap_subtype[32];
} retromind_chd_metadata_t;

typedef struct retromind_chd_track
{
    chd_file* chd;
    const rc_hash_iterator_t* iterator;
    uint8_t* hunk;
    uint32_t loaded_hunk;
    uint32_t frames_per_hunk;
    uint32_t total_hunks;
    uint32_t unit_bytes;
    uint32_t physical_frame_offset;
    uint32_t first_sector;
    uint32_t frames;
    uint32_t stored_pregap;
    uint32_t sector_header_size;
    uint32_t data_size;
    int swap_audio_bytes;
} retromind_chd_track_t;

static uint32_t retromind_chd_padding_frames(uint32_t frames)
{
    return ((frames + RETROMIND_CHD_TRACK_PADDING - 1U) &
        ~(RETROMIND_CHD_TRACK_PADDING - 1U)) - frames;
}

static int retromind_chd_get_metadata(
    chd_file* chd,
    uint32_t index,
    retromind_chd_metadata_t* metadata)
{
    char value[256];
    chd_error error;
    unsigned postgap;
    unsigned pad;

    memset(metadata, 0, sizeof(*metadata));
    value[0] = '\0';

    error = chd_get_metadata(
        chd, CDROM_TRACK_METADATA2_TAG, index, value, sizeof(value),
        NULL, NULL, NULL);
    if (error == CHDERR_NONE)
    {
        postgap = 0;
        if (sscanf(
                value,
                "TRACK:%u TYPE:%63s SUBTYPE:%31s FRAMES:%u PREGAP:%u PGTYPE:%31s PGSUB:%31s POSTGAP:%u",
                &metadata->track,
                metadata->type,
                metadata->subtype,
                &metadata->frames,
                &metadata->pregap,
                metadata->pregap_type,
                metadata->pregap_subtype,
                &postgap) != 8)
        {
            return 0;
        }

        metadata->extra = retromind_chd_padding_frames(metadata->frames);
        return 1;
    }

    value[0] = '\0';
    error = chd_get_metadata(
        chd, CDROM_TRACK_METADATA_TAG, index, value, sizeof(value),
        NULL, NULL, NULL);
    if (error == CHDERR_NONE)
    {
        if (sscanf(
                value,
                "TRACK:%u TYPE:%63s SUBTYPE:%31s FRAMES:%u",
                &metadata->track,
                metadata->type,
                metadata->subtype,
                &metadata->frames) != 4)
        {
            return 0;
        }

        metadata->extra = retromind_chd_padding_frames(metadata->frames);
        return 1;
    }

    value[0] = '\0';
    error = chd_get_metadata(
        chd, GDROM_TRACK_METADATA_TAG, index, value, sizeof(value),
        NULL, NULL, NULL);
    if (error == CHDERR_NONE)
    {
        postgap = 0;
        pad = 0;
        if (sscanf(
                value,
                "TRACK:%u TYPE:%63s SUBTYPE:%31s FRAMES:%u PAD:%u PREGAP:%u PGTYPE:%31s PGSUB:%31s POSTGAP:%u",
                &metadata->track,
                metadata->type,
                metadata->subtype,
                &metadata->frames,
                &pad,
                &metadata->pregap,
                metadata->pregap_type,
                metadata->pregap_subtype,
                &postgap) != 9)
        {
            return 0;
        }

        metadata->extra = retromind_chd_padding_frames(metadata->frames);
        return 1;
    }

    if (index == 0 &&
        chd_get_metadata(
            chd, DVD_METADATA_TAG, 0, value, sizeof(value),
            NULL, NULL, NULL) == CHDERR_NONE)
    {
        const chd_header* header = chd_get_header(chd);
        metadata->track = 1;
        metadata->frames = header && header->unitbytes
            ? (uint32_t)(header->logicalbytes / header->unitbytes)
            : 0;
        memcpy(metadata->type, "DVD", 4);
        return metadata->frames != 0;
    }

    return 0;
}

static int retromind_chd_select_track(
    chd_file* chd,
    uint32_t requested_track,
    retromind_chd_metadata_t* selected)
{
    retromind_chd_metadata_t current;
    retromind_chd_metadata_t largest;
    retromind_chd_metadata_t last;
    uint64_t physical_frame_offset = 0;
    uint64_t first_sector = 0;
    uint32_t index;
    int has_largest = 0;
    int has_last = 0;

    memset(&largest, 0, sizeof(largest));
    memset(&last, 0, sizeof(last));

    /* Historically track zero meant the largest data track. */
    if (requested_track == 0)
        requested_track = RC_HASH_CDTRACK_LARGEST;

    for (index = 0; retromind_chd_get_metadata(chd, index, &current); ++index)
    {
        if (physical_frame_offset > UINT32_MAX || first_sector > UINT32_MAX)
            return 0;

        current.physical_frame_offset = (uint32_t)physical_frame_offset;
        current.first_sector = (uint32_t)first_sector;
        last = current;
        has_last = 1;

        if (requested_track == current.track)
        {
            *selected = current;
            return 1;
        }

        if (strcasecmp(current.type, "AUDIO") != 0 &&
            (!has_largest || current.frames > largest.frames))
        {
            largest = current;
            has_largest = 1;
        }

        physical_frame_offset += current.frames + current.extra;
        first_sector += current.frames;
    }

    if ((requested_track == RC_HASH_CDTRACK_FIRST_DATA ||
         requested_track == RC_HASH_CDTRACK_LARGEST) && has_largest)
    {
        *selected = largest;
        return 1;
    }

    if (requested_track == RC_HASH_CDTRACK_LAST && has_last)
    {
        *selected = last;
        return 1;
    }

    return 0;
}

static int retromind_chd_configure_sector_layout(
    retromind_chd_track_t* track,
    const retromind_chd_metadata_t* metadata)
{
    track->data_size = RETROMIND_CHD_DATA_SECTOR_SIZE;

    if (strcasecmp(metadata->type, "MODE1_RAW") == 0)
    {
        track->sector_header_size = 16;
    }
    else if (strcasecmp(metadata->type, "MODE2_RAW") == 0)
    {
        track->sector_header_size = 24;
    }
    else if (strcasecmp(metadata->type, "MODE2") == 0 ||
             strcasecmp(metadata->type, "MODE2_FORM_MIX") == 0)
    {
        track->sector_header_size = 8;
    }
    else if (strcasecmp(metadata->type, "MODE1") == 0 ||
             strcasecmp(metadata->type, "MODE2_FORM1") == 0 ||
             strcasecmp(metadata->type, "DVD") == 0)
    {
        track->sector_header_size = 0;
    }
    else if (strcasecmp(metadata->type, "AUDIO") == 0)
    {
        track->sector_header_size = 0;
        track->data_size = RETROMIND_CHD_RAW_SECTOR_SIZE;
        track->swap_audio_bytes = 1;
    }
    else
    {
        return 0;
    }

    return track->sector_header_size + track->data_size <= track->unit_bytes;
}

static int retromind_chd_has_extension(const char* path, const char* extension)
{
    const char* dot = strrchr(path, '.');
    return dot && strcasecmp(dot + 1, extension) == 0;
}

static void* retromind_chd_open_track(
    const char* path,
    uint32_t requested_track,
    const rc_hash_iterator_t* iterator)
{
    retromind_chd_metadata_t metadata;
    retromind_chd_track_t* track;
    const chd_header* header;
    chd_file* chd = NULL;
    chd_error error;

    error = chd_open(path, CHD_OPEN_READ, NULL, &chd);
    if (error != CHDERR_NONE)
    {
        rc_hash_iterator_error_formatted(
            iterator, "Could not open CHD file: %s", chd_error_string(error));
        return NULL;
    }

    if (!retromind_chd_select_track(chd, requested_track, &metadata))
    {
        rc_hash_iterator_error(iterator, "Could not find the requested track in the CHD file");
        chd_close(chd);
        return NULL;
    }

    header = chd_get_header(chd);
    if (!header || !header->hunkbytes || !header->unitbytes ||
        header->hunkbytes % header->unitbytes != 0)
    {
        rc_hash_iterator_error(iterator, "The CHD file has an unsupported hunk layout");
        chd_close(chd);
        return NULL;
    }

    track = (retromind_chd_track_t*)calloc(1, sizeof(*track));
    if (!track)
    {
        rc_hash_iterator_error(iterator, "Could not allocate the CHD track reader");
        chd_close(chd);
        return NULL;
    }

    track->hunk = (uint8_t*)malloc(header->hunkbytes);
    if (!track->hunk)
    {
        rc_hash_iterator_error(iterator, "Could not allocate the CHD hunk buffer");
        free(track);
        chd_close(chd);
        return NULL;
    }

    track->chd = chd;
    track->iterator = iterator;
    track->loaded_hunk = UINT32_MAX;
    track->frames_per_hunk = header->hunkbytes / header->unitbytes;
    track->total_hunks = header->totalhunks;
    track->unit_bytes = header->unitbytes;
    track->physical_frame_offset = metadata.physical_frame_offset;
    track->first_sector = metadata.first_sector;
    track->frames = metadata.frames;
    track->stored_pregap = metadata.pregap_type[0] != 'V' ? metadata.pregap : 0;

    if ((uint64_t)track->physical_frame_offset + track->stored_pregap +
        track->frames > (uint64_t)track->frames_per_hunk * track->total_hunks)
    {
        rc_hash_iterator_error(iterator, "The CHD track extends beyond the available data");
        free(track->hunk);
        free(track);
        chd_close(chd);
        return NULL;
    }

    if (!retromind_chd_configure_sector_layout(track, &metadata))
    {
        rc_hash_iterator_error_formatted(
            iterator, "The CHD track type '%s' is not supported", metadata.type);
        free(track->hunk);
        free(track);
        chd_close(chd);
        return NULL;
    }

    return track;
}

static int retromind_chd_load_hunk(
    retromind_chd_track_t* track,
    uint32_t hunk_number)
{
    chd_error error;

    if (track->loaded_hunk == hunk_number)
        return 1;

    error = chd_read(track->chd, hunk_number, track->hunk);
    if (error != CHDERR_NONE)
    {
        rc_hash_iterator_error_formatted(
            track->iterator, "Could not read CHD data: %s", chd_error_string(error));
        return 0;
    }

    track->loaded_hunk = hunk_number;
    return 1;
}

static size_t retromind_chd_read_sector(
    void* track_handle,
    uint32_t sector,
    void* buffer,
    size_t requested_bytes)
{
    retromind_chd_track_t* track = (retromind_chd_track_t*)track_handle;
    uint8_t* output = (uint8_t*)buffer;
    size_t total_read = 0;

    if (!track || !buffer || sector < track->first_sector)
        return 0;

    sector -= track->first_sector;
    while (requested_bytes && sector < track->frames)
    {
        uint64_t physical_frame = (uint64_t)track->physical_frame_offset +
            track->stored_pregap + sector;
        uint32_t hunk_number = (uint32_t)(physical_frame / track->frames_per_hunk);
        uint32_t frame_in_hunk = (uint32_t)(physical_frame % track->frames_per_hunk);
        uint64_t source_offset = (uint64_t)frame_in_hunk * track->unit_bytes +
            track->sector_header_size;
        size_t amount = requested_bytes < track->data_size
            ? requested_bytes
            : track->data_size;
        size_t index;

        if (hunk_number >= track->total_hunks ||
            source_offset + amount >
                (uint64_t)track->frames_per_hunk * track->unit_bytes)
        {
            rc_hash_iterator_error(track->iterator, "The CHD sector is outside the available data");
            break;
        }

        if (!retromind_chd_load_hunk(track, hunk_number))
            break;

        memcpy(output + total_read, track->hunk + (size_t)source_offset, amount);
        if (track->swap_audio_bytes)
        {
            for (index = 0; index + 1 < amount; index += 2)
            {
                uint8_t value = output[total_read + index];
                output[total_read + index] = output[total_read + index + 1];
                output[total_read + index + 1] = value;
            }
        }

        total_read += amount;
        requested_bytes -= amount;
        ++sector;
    }

    return total_read;
}

static void retromind_chd_close_track(void* track_handle)
{
    retromind_chd_track_t* track = (retromind_chd_track_t*)track_handle;
    if (!track)
        return;

    free(track->hunk);
    chd_close(track->chd);
    free(track);
}

static uint32_t retromind_chd_first_track_sector(void* track_handle)
{
    const retromind_chd_track_t* track =
        (const retromind_chd_track_t*)track_handle;
    return track ? track->first_sector : 0;
}

static void* retromind_cd_open_track(
    const char* path,
    uint32_t track,
    const rc_hash_iterator_t* iterator)
{
    rc_hash_callbacks_t* callbacks = (rc_hash_callbacks_t*)&iterator->callbacks;

    if (retromind_chd_has_extension(path, "chd"))
    {
        callbacks->cdreader.read_sector = retromind_chd_read_sector;
        callbacks->cdreader.close_track = retromind_chd_close_track;
        callbacks->cdreader.first_track_sector = retromind_chd_first_track_sector;
        callbacks->cdreader.open_track_iterator = retromind_cd_open_track;
        return retromind_chd_open_track(path, track, iterator);
    }

    rc_hash_get_default_cdreader(&callbacks->cdreader);
    return callbacks->cdreader.open_track_iterator(path, track, iterator);
}

void retromind_chd_reader_install(rc_hash_iterator_t* iterator)
{
    iterator->callbacks.cdreader.open_track_iterator = retromind_cd_open_track;
}
