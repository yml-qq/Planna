using Microsoft.Extensions.Configuration;

namespace Planna.Infrastructure.Notes;

internal static class NoteAttachmentStorage
{
    public static string Root(IConfiguration configuration) =>
        configuration["NOTE_ATTACHMENT_DIR"] ??
        Path.Combine(Path.GetTempPath(), "planna-note-attachments");

    public static string PathFor(string root, string storageKey) =>
        Path.Combine(root, storageKey.Replace('/', Path.DirectorySeparatorChar));
}
