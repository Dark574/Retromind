#include "rc_hash.h"

#include <stddef.h>
#include <stdint.h>

#if defined(__GNUC__) && __GNUC__ >= 4
#define RETROMIND_EXPORT __attribute__((visibility("default")))
#else
#define RETROMIND_EXPORT
#endif

RETROMIND_EXPORT int retromind_rhash_generate(
    uint32_t console_id,
    const char* path,
    char hash[33])
{
    rc_hash_iterator_t iterator;
    int result;

    if (!path || !hash)
        return 0;

    hash[0] = '\0';
    rc_hash_initialize_iterator(&iterator, path, NULL, 0);
    result = rc_hash_generate(hash, console_id, &iterator);
    rc_hash_destroy_iterator(&iterator);
    return result;
}

RETROMIND_EXPORT const char* retromind_rhash_version(void)
{
    return "12.4.0";
}
