using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace GangioKeet.Client.Platform;

/// <summary>
/// HTTP download with byte-range resume. The partial file is kept between
/// attempts, so a crash or a dropped connection continues from the last byte
/// instead of restarting a multi-gigabyte download.
/// </summary>
internal static class ResumableDownload
{
    internal sealed record Result(long Bytes, long? TotalBytes);

    internal static async Task<Result> DownloadAsync(HttpClient http, string url,
        string destinationPath, string expectedSha256,
        Action<long, long?>? progress, CancellationToken ct)
    {
        string partial = destinationPath + ".downloading";
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);

            using HttpResponseMessage response = await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            {
                if (await MatchesAsync(partial, expectedSha256, ct).ConfigureAwait(false))
                {
                    File.Move(partial, destinationPath, overwrite: true);
                    return new(offset, offset);
                }
                Delete(partial);
                continue;
            }

            response.EnsureSuccessStatusCode();

            bool append;
            long start;
            long? total;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                if (range?.From is not long from || from != offset)
                {
                    Delete(partial);
                    if (offset > 0) continue;
                    throw new InvalidDataException("The server returned an invalid Content-Range.");
                }
                append = true;
                start = offset;
                total = range.Length;
            }
            else
            {
                // The server ignored Range: restart from zero.
                append = false;
                start = 0;
                total = response.Content.Headers.ContentLength;
                Delete(partial);
            }

            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (append)
                await AppendExistingAsync(hasher, partial, ct).ConfigureAwait(false);

            long downloaded = start;
            await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (FileStream target = new(partial,
                append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, useAsync: true))
            {
                byte[] buffer = new byte[256 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hasher.AppendData(buffer.AsSpan(0, read));
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;
                    progress?.Invoke(downloaded, total);
                }
                await target.FlushAsync(ct).ConfigureAwait(false);
            }

            string actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Delete(partial);
                throw new InvalidDataException(
                    $"Downloaded file hash mismatch. Expected {expectedSha256}, got {actual}.");
            }

            File.Move(partial, destinationPath, overwrite: true);
            progress?.Invoke(downloaded, total);
            return new(downloaded, total);
        }

        throw new InvalidDataException("Could not restart the interrupted download.");
    }

    private static async Task AppendExistingAsync(IncrementalHash hasher, string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, useAsync: true);
        byte[] buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            hasher.AppendData(buffer.AsSpan(0, read));
    }

    private static async Task<bool> MatchesAsync(string path, string expectedSha256, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The next attempt will report the real IO error.
        }
    }
}
