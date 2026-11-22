using System.Security.Cryptography;

namespace Aetherform;

internal static class BodySetup
{
    private const string Url = "https://github.com/kaldigo/FFXIVAetherform/releases/download/body-0.3.0/Aetherform.pmp";
    private const string Hash = "2A765DDC5F9FF4FA9F66B6FD2C8C5CEC7345A5302E5C6910181570455D939B4C";
    private const long Size = 128132976;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(20) };

    public static async Task<string> GetPackage(string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Aetherform.pmp");
        if (File.Exists(path)) return path;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = File.Create(temporary))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    if (output.Length + count > Size) throw new InvalidDataException("Unexpected body package size.");
                    sha.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (output.Length != Size || Convert.ToHexString(sha.GetHashAndReset()) != Hash)
                    throw new InvalidDataException("Body package verification failed. Retry the download.");
            }
            File.Move(temporary, path, true);
            return path;
        }
        finally { File.Delete(temporary); }
    }
}
