namespace WorkBoard.Api.Infrastructure.Writes;

public static class RequestBodies
{
    // Null when the body is larger than maxBytes.
    public static async Task<byte[]?> ReadAsync(HttpContext http, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
