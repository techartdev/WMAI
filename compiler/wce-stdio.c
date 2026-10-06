/* wce-stdio.c - WMAI addition to PocketGCC: --stdout=FILE / --stderr=FILE.

   Windows CE has no console by default, and a redirect set by a parent
   process (SetStdioPathW) is not inherited by the child, so a program that
   launches these tools cannot capture their messages. Each tool's main() calls
   wce_stdio_redirect() first: it removes these options from argv and redirects
   the process's own stdout/stderr, which Windows CE does honour.

   No #includes on purpose: only the two coredll functions below are needed. */

typedef unsigned short wce_wchar; /* built with -fshort-wchar: 16-bit */
extern void *GetModuleHandleW (const wce_wchar *name);
extern void *GetProcAddressW (void *module, const wce_wchar *name);
typedef int (*wce_set_stdio_fn) (unsigned long id, const wce_wchar *path);

static int
wce_prefix (const char *s, const char *prefix)
{
  while (*prefix)
    if (*s++ != *prefix++)
      return 0;
  return 1;
}

int
wce_stdio_redirect (int *argc, char **argv)
{
  static const wce_wchar coredll[] =
    { 'c', 'o', 'r', 'e', 'd', 'l', 'l', '.', 'd', 'l', 'l', 0 };
  static const wce_wchar fn[] =
    { 'S', 'e', 't', 'S', 't', 'd', 'i', 'o', 'P', 'a', 't', 'h', 'W', 0 };
  wce_set_stdio_fn set = 0;
  wce_wchar path[260];
  int i, j, k, id, done = 0;

  for (i = 1, j = 1; i < *argc; i++)
    {
      const char *a = argv[i];
      if (wce_prefix (a, "--stdout="))
        id = 1;
      else if (wce_prefix (a, "--stderr="))
        id = 2;
      else
        {
          argv[j++] = argv[i];
          continue;
        }
      if (set == 0)
        set = (wce_set_stdio_fn) GetProcAddressW (GetModuleHandleW (coredll), fn);
      if (set != 0)
        {
          a += 9;
          for (k = 0; a[k] && k < 259; k++)
            path[k] = (unsigned char) a[k]; /* paths are ASCII */
          path[k] = 0;
          set (id, path);
          done++;
        }
    }
  argv[j] = 0;
  *argc = j;
  return done;
}
