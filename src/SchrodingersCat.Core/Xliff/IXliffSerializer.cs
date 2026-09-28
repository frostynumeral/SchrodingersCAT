using SchrodingersCat.Core.Models;

namespace SchrodingersCat.Core.Xliff;

/// <summary>
/// Reads and writes <see cref="TranslationDocument"/>s in an XLIFF dialect.
/// Stream-based so it works equally with files, memory and (later) project archives.
/// </summary>
public interface IXliffSerializer
{
    /// <exception cref="XliffFormatException">The input is not valid or not supported XLIFF.</exception>
    Task<TranslationDocument> ReadAsync(Stream input, CancellationToken cancellationToken = default);

    Task WriteAsync(TranslationDocument document, Stream output, CancellationToken cancellationToken = default);
}

public static class XliffSerializerExtensions
{
    public static async Task<TranslationDocument> ReadFileAsync(
        this IXliffSerializer serializer, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await serializer.ReadAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Saves via a temporary file and then swaps it in, so a crash or error mid-save
    /// never leaves the translator with a half-written file.
    /// </summary>
    public static async Task WriteFileAsync(
        this IXliffSerializer serializer, TranslationDocument document, string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var tempPath = fullPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await serializer.WriteAsync(document, stream, cancellationToken);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }
}
