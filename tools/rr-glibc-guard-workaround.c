#define _GNU_SOURCE

#include <dlfcn.h>
#include <errno.h>
#include <pthread.h>
#include <stdio.h>

typedef int (*pthread_create_fn)(
    pthread_t *thread,
    const pthread_attr_t *attr,
    void *(*start_routine)(void *),
    void *arg);

__attribute__((constructor)) static void rr_dotnet_disable_default_pthread_guards(void)
{
    pthread_attr_t attr;

    if (pthread_attr_init(&attr) != 0)
    {
        return;
    }

    if (pthread_attr_setguardsize(&attr, 0) == 0)
    {
        int result = pthread_setattr_default_np(&attr);
        if (result != 0)
        {
            fprintf(stderr, "rr-dotnet: pthread_setattr_default_np failed: %d\n", result);
        }
    }

    pthread_attr_destroy(&attr);
}

int pthread_attr_setguardsize(pthread_attr_t *attr, size_t guardsize)
{
    static int (*real_pthread_attr_setguardsize)(pthread_attr_t *, size_t);

    if (real_pthread_attr_setguardsize == NULL)
    {
        real_pthread_attr_setguardsize = dlsym(RTLD_NEXT, "pthread_attr_setguardsize");
    }

    (void)guardsize;
    return real_pthread_attr_setguardsize(attr, 0);
}

int pthread_create(
    pthread_t *thread,
    const pthread_attr_t *attr,
    void *(*start_routine)(void *),
    void *arg)
{
    static pthread_create_fn real_pthread_create;
    pthread_attr_t local_attr;
    const pthread_attr_t *effective_attr = attr;
    int initialized = 0;

    if (real_pthread_create == NULL)
    {
        real_pthread_create = (pthread_create_fn)dlsym(RTLD_NEXT, "pthread_create");
        if (real_pthread_create == NULL)
        {
            errno = ENOSYS;
            return ENOSYS;
        }
    }

    if (attr == NULL)
    {
        if (pthread_attr_init(&local_attr) == 0)
        {
            pthread_attr_setguardsize(&local_attr, 0);
            effective_attr = &local_attr;
            initialized = 1;
        }
    }
    else if (pthread_attr_init(&local_attr) == 0)
    {
        local_attr = *attr;
        pthread_attr_setguardsize(&local_attr, 0);
        effective_attr = &local_attr;
        initialized = 1;
    }

    int result = real_pthread_create(thread, effective_attr, start_routine, arg);

    if (initialized)
    {
        pthread_attr_destroy(&local_attr);
    }

    return result;
}
