#include "rc_hash.h"
#include "retromind_chd_reader.h"

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

#if defined(__GNUC__) && __GNUC__ >= 4
#define RETROMIND_EXPORT __attribute__((visibility("default")))
#else
#define RETROMIND_EXPORT
#endif

typedef struct retromind_hash_context
{
    char* error;
    size_t error_size;
} retromind_hash_context_t;

static void retromind_rhash_error(
    const char* message,
    const rc_hash_iterator_t* iterator)
{
    retromind_hash_context_t* context =
        (retromind_hash_context_t*)iterator->userdata;

    if (!context || !context->error || context->error_size == 0 ||
        context->error[0] != '\0')
    {
        return;
    }

    snprintf(context->error, context->error_size, "%s", message);
}

RETROMIND_EXPORT int retromind_rhash_generate(
    uint32_t console_id,
    const char* path,
    char hash[33],
    char* error,
    size_t error_size)
{
    rc_hash_iterator_t iterator;
    retromind_hash_context_t context;
    int result;

    if (!path || !hash)
        return 0;

    hash[0] = '\0';
    if (error && error_size)
        error[0] = '\0';

    context.error = error;
    context.error_size = error_size;
    rc_hash_initialize_iterator(&iterator, path, NULL, 0);
    iterator.userdata = &context;
    iterator.callbacks.error_message = retromind_rhash_error;
    retromind_chd_reader_install(&iterator);
    result = rc_hash_generate(hash, console_id, &iterator);
    rc_hash_destroy_iterator(&iterator);
    return result;
}

RETROMIND_EXPORT const char* retromind_rhash_version(void)
{
    return "12.4.0";
}
