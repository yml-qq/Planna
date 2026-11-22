using Microsoft.Extensions.Configuration;

namespace Planna.Infrastructure.Notes;

internal static class NoteAttachmentStorage
{
    public static string Root(IConfiguration configuration) =>
        configuration["NOTE_ATTACHMENT_DIR"] ??
        Path.Combine(Path.GetTempPath(), "planna-note-attachments");

    public static string PathFor(string root, string storageKey) =>
        Path.Combine(root, storageKey.Replace('/', Path.DirectorySeparatorChar));

    // Returns null if the resolved path escapes the configured root (defence in depth for a
    // corrupted/tampered storage_key).
    public static string? PathWithinRoot(string root, string storageKey)
    {
        var rootFull = Path.GetFullPath(root);
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(PathFor(root, storageKey));
        return candidate.StartsWith(prefix, StringComparison.Ordinal) ? candidate : null;
    }
}
