using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

var input = Environment.GetEnvironmentVariable("INPUT_INPUT") ?? "source.json";
var output = Environment.GetEnvironmentVariable("INPUT_OUTPUT") ?? "index.json";
var repoToken = Environment.GetEnvironmentVariable("INPUT_REPO-TOKEN");
var formatOutput = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INPUT_FORMAT_OUTPUT"));
Stopwatch sw = new();

Source source;
using (new Scope($"Read {input}", sw))
{
    using var fs = File.OpenRead(input);
    source = await JsonSerializer.DeserializeAsync(fs, SerializeContexts.Default.Source) ?? throw new InvalidDataException();
}

using var client = new HttpClient();
client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("gomorroth.vpm-build-repository", null));
if (repoToken is not null)
{
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", repoToken);
}

ConcurrentBag<(PackageInfo packageInfo, string zipUrl)> packageList = new();

using (new Scope($"Fetch packages", sw))
{
    await Parallel.ForEachAsync(source.Repositories ?? [], async (repo, token) =>
    {
        var releases = await client.GetFromJsonAsync($"https://api.github.com/repos/{repo}/releases", SerializeContexts.Default.ReleaseArray);
        await Parallel.ForEachAsync(releases ?? [], async (release, _) =>
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("gomorroth.vpm-build-repository", null));
            if (repoToken is not null)
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", repoToken);
            }

            PackageInfo? packageInfo = null;
            string? zipUrl = null;
            foreach (var asset in release?.Assets ?? [])
            {
                if (asset.Name is "package.json")
                {
                    packageInfo = await client.GetFromJsonAsync($"{asset.DownloadUrl}", SerializeContexts.Default.PackageInfo, token);
                }
                else if (asset.ContentType is "application/zip")
                {
                    zipUrl = asset.DownloadUrl;
                }

                if (packageInfo is not null && zipUrl is not null)
                {
                    break;
                }
            }
            if (packageInfo is not null && zipUrl is not null)
            {
                packageList.Add((packageInfo, zipUrl));
            }
        });
    });
}

var packages = packageList.ToArray();

using (new Scope($"Export package list > {output}", sw))
{
    var bufferWriter = new ArrayBufferWriter<byte>(ushort.MaxValue);
    using Utf8JsonWriter writer = new(bufferWriter, new() { Indented = formatOutput });
    writer.WriteStartObject();
    {
        foreach (var package in packages.GroupBy(x => x.packageInfo.Name))
        {
            writer.WritePropertyName(package.Key!);
            writer.WriteStartObject();
            writer.WritePropertyName("versions"u8);
            writer.WriteStartObject();
            foreach (var (packageInfo, zipUrl) in package.OrderByDescending(x => x.packageInfo.Version!, SemVerComparer.Instance))
            {
                writer.WriteStartObject(packageInfo.Version!);
                writer.WriteString("name"u8, packageInfo.Name!);
                writer.WriteString("displayName"u8, packageInfo.DisplayName!);
                writer.WriteString("version"u8, packageInfo.Version!);
                writer.WritePropertyName("author"u8);
                AuthorConverter.Instance.Write(writer, source.Author!, null!);
                writer.WriteString("url"u8, zipUrl);
                writer.WriteStartObject("dependencies"u8);
                foreach (var dependency in packageInfo.Dependencies ?? [])
                {
                    writer.WriteString(dependency.Key, dependency.Value);
                }
                writer.WriteEndObject();
                writer.WriteStartObject("vpmDependencies"u8);
                foreach (var dependency in packageInfo.VpmDependencies ?? [])
                {
                    writer.WriteString(dependency.Key, dependency.Value);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }
    writer.WriteEndObject();
    await writer.FlushAsync();

    await RandomAccess.WriteAsync(File.OpenHandle(output, FileMode.Create, FileAccess.Write, FileShare.None), bufferWriter.WrittenMemory, 0);
}

internal readonly struct Scope : IDisposable
{
    private readonly Stopwatch _stopWatch;
    public Scope(string label, Stopwatch stopwatch)
    {
        Console.Write($"{label} ... ");
        stopwatch.Restart();
        _stopWatch = stopwatch;
    }

    public void Dispose()
    {
        _stopWatch.Stop();
        Console.WriteLine($"Done : {_stopWatch.ElapsedMilliseconds}ms");
    }
}

internal sealed class SemVerComparer : IComparer<string>
{
    public static SemVerComparer Instance { get; } = new SemVerComparer();
    public int Compare(string? x, string? y)
    {
        return SemVer.Parse(x).CompareTo(SemVer.Parse(y));
    }
}
internal readonly ref struct SemVer
{
    public readonly int Major;
    public readonly int Minor;
    public readonly int Patch;
    public readonly ReadOnlySpan<char> Label;

    public SemVer(int major, int minor, int patch, ReadOnlySpan<char> label)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Label = label;
    }

    [SkipLocalsInit]
    public static SemVer Parse(ReadOnlySpan<char> value)
    {
        var ranges = (stackalloc Range[6]);
        int count = value.SplitAny(ranges, ".-");
        if (count < 3)
            return default;

        var major = value[ranges[0]];
        var minor = value[ranges[1]];
        var patch = value[ranges[2]];
        var label = count < 4 ? [] : value[ranges[3].Start..];
        if (!label.IsEmpty)
        {
            var buildMeta = label.IndexOf('+');
            if (buildMeta > 0)
            {
                label = label[..buildMeta];
            }
        }

        return new SemVer(int.Parse(major), int.Parse(minor), int.Parse(patch), label);
    }

    public int CompareTo(SemVer other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
            return minor;

        int patch = Patch.CompareTo(other.Patch);
        if (patch != 0)
            return patch;

        if (Label.Length == 0 && other.Label.Length != 0)
            return 1;
        else if (Label.Length != 0 && other.Label.Length == 0)
            return -1;
        else
            return Label.CompareTo(other.Label, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Label.IsEmpty ? "" : "-")}{Label}";

}

internal sealed record class Author
{
    public string? Name { get; set; }
    public string? Url { get; set; }
}

internal sealed record class Source
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("author")]
    [JsonConverter(typeof(AuthorConverter))]
    public Author? Author { get; set; }

    [JsonPropertyName("githubRepos")]
    public string[]? Repositories { get; set; }
}

internal sealed class Release
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("assets")]
    public Asset[]? Assets { get; set; }

    public sealed class Asset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
        [JsonPropertyName("content_type")]
        public string? ContentType { get; set; }
        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; set; }
    }
}

internal record class PackageInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("version")]
    public string? Version { get; set; }
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string>? Dependencies { get; set; }
    [JsonPropertyName("vpmDependencies")]
    public Dictionary<string, string>? VpmDependencies { get; set; }
}

internal sealed record RepositoryPackageInfo : PackageInfo
{
    [JsonPropertyName("repo")]
    public string? Repository { get; set; }
}

internal sealed class AuthorConverter : JsonConverter<Author>
{
    public static AuthorConverter Instance { get; } = new AuthorConverter();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override Author? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new Author() { Name = reader.GetString() };

        return ReadObject(ref reader, typeToConvert, options);
    }

    private static Author? ReadObject(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException();
        }

        var author = new Author();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return author;
            }

            // Get the key.
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException();
            }

            string? propertyName = reader.GetString();
            if (propertyName is null)
                throw new JsonException();

            reader.Read();
            if (propertyName.Equals("name", StringComparison.OrdinalIgnoreCase))
                author.Name = reader.GetString();
            else if (propertyName.Equals("url", StringComparison.OrdinalIgnoreCase))
                author.Url = reader.GetString();

        }

        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, Author value, JsonSerializerOptions options)
    {
        if (value.Url is null)
        {
            writer.WriteStringValue(value.Name);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("name"u8, value.Name);
            writer.WriteString("url"u8, value.Url);
            writer.WriteEndObject();
        }
    }
}

internal class PackageInfoZipUrl
{
    public required PackageInfo PackageInfo { get; set; }
    public required string ZipUrl { get; set; }

    public void Deconstruct(out PackageInfo p, out string z) => (p, z) = (PackageInfo, ZipUrl);
}

[JsonSerializable(typeof(Source))]
[JsonSerializable(typeof(Release))]
[JsonSerializable(typeof(Release[]))]
[JsonSerializable(typeof(Release.Asset))]
[JsonSerializable(typeof(PackageInfo))]
[JsonSerializable(typeof(Dictionary<string, PackageInfoZipUrl[]>))]
[JsonSerializable(typeof(RepositoryPackageInfo))]
internal sealed partial class SerializeContexts : JsonSerializerContext;
internal static partial class Sha256
{
    private static ReadOnlySpan<ulong> K => 
    [
        0x428a2f98UL, 0x71374491UL, 0xb5c0fbcfUL, 0xe9b5dba5UL,
        0x3956c25bUL, 0x59f111f1UL, 0x923f82a4UL, 0xab1c5ed5UL,
        0xd807aa98UL, 0x12835b01UL, 0x243185beUL, 0x550c7dc3UL,
        0x72be5d74UL, 0x80deb1feUL, 0x9bdc06a7UL, 0xc19bf174UL,
        0xe49b69c1UL, 0xefbe4786UL, 0x0fc19dc6UL, 0x240ca1ccUL,
        0x2de92c6fUL, 0x4a7484aaUL, 0x5cb0a9dcUL, 0x76f988daUL,
        0x983e5152UL, 0xa831c66dUL, 0xb00327c8UL, 0xbf597fc7UL,
        0xc6e00bf3UL, 0xd5a79147UL, 0x06ca6351UL, 0x14292967UL,
        0x27b70a85UL, 0x2e1b2138UL, 0x4d2c6dfcUL, 0x53380d13UL,
        0x650a7354UL, 0x766a0abbUL, 0x81c2c92eUL, 0x92722c85UL,
        0xa2bfe8a1UL, 0xa81a664bUL, 0xc24b8b70UL, 0xc76c51a3UL,
        0xd192e819UL, 0xd6990624UL, 0xf40e3585UL, 0x106aa070UL,
        0x19a4c116UL, 0x1e376c08UL, 0x2748774cUL, 0x34b0bcb5UL,
        0x391c0cb3UL, 0x4ed8aa4aUL, 0x5b9cca4fUL, 0x682e6ff3UL,
        0x748f82eeUL, 0x78a5636fUL, 0x84c87814UL, 0x8cc70208UL,
        0x90befffaUL, 0xa4506cebUL, 0xbef9a3f7UL, 0xc67178f2UL
    ];

    private static ReadOnlySpan<uint> H =>
    [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
    ];

}

// Utilities
static partial class Sha256
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Rot(this uint v, int n) => BitOperations.RotateLeft(v, n);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Shr(this uint v, int n) => v >> n;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Sigma0(this uint x) => x.Rot(30) ^ x.Rot(19) ^ x.Rot(10);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Sigma1(this uint x) => x.Rot(26) ^ x.Rot(21) ^ x.Rot(7);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint sigma0(this uint x) => x.Rot(25) ^ x.Rot(14) ^ x.Rot(3);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint sigma1(this uint x) => x.Rot(15) ^ x.Rot(13) ^ x.Rot(10);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Ch(this uint x, uint y, uint z) => (x & y) ^ (~x & z);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Maj(this uint x, uint y, uint z) => (x & y) ^ (y & z) ^ (x & z);
}