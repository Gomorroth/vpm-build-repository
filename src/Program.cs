﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var input = Environment.GetEnvironmentVariable("INPUT_INPUT") ?? "source.json";
var output = Environment.GetEnvironmentVariable("INPUT_OUTPUT") ?? "index.json";
var repoToken = Environment.GetEnvironmentVariable("INPUT_REPO-TOKEN");
var formatOutput = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INPUT_FORMAT_OUTPUT")) || true;
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

ConcurrentBag<PackageInfo> packageList = new();

using (new Scope($"Fetch packages", sw))
{
    await Parallel.ForEachAsync(source.Repositories?.Where(x => x.Contains("Cloth")) ?? [], async (repo, token) =>
    {
        var releases = await client.GetFromJsonAsync($"https://api.github.com/repos/{repo}/releases", SerializeContexts.Default.ReleaseArray);
        await Parallel.ForEachAsync(releases ?? [], async (release, token) =>
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
                packageInfo.Url = zipUrl;
                packageList.Add(packageInfo);
            }


            async static Task<string> CalcHash(HttpClient client, string url)
            {
                using var stream = await client.GetStreamAsync(url);
                int size = (int)stream.Length;
                var data = ArrayPool<byte>.Shared.Rent(size);
                stream.Read(data, 0, data.Length);

                string HashAndToString(ReadOnlySpan<byte> data)
                {
                    var buffer = (stackalloc byte[64]);
                    SHA256.TryHashData(data, buffer, out _);
                    return Convert.ToHexString(buffer);
                }

                return HashAndToString(data.AsSpan(0, size));
            }
        });
    });
}

var packages = packageList.ToArray();

using (new Scope($"Export package list > {output}", sw))
{
    var bufferWriter = new ArrayBufferWriter<byte>(ushort.MaxValue);
    using Utf8JsonWriter writer = new(bufferWriter, new() { Indented = formatOutput,  });
    var serializeOptions = new JsonSerializerOptions() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    writer.WriteStartObject();
    {
        foreach (var package in packages.GroupBy(x => x.Name))
        {
            writer.WritePropertyName(package.Key!);
            writer.WriteStartObject();
            writer.WritePropertyName("versions"u8);
            writer.WriteStartObject();
            foreach (var packageInfo in package.OrderByDescending(x => x.Version!, SemVerComparer.Instance))
            {
                writer.WritePropertyName(packageInfo.Version!);
                packageInfo.Author = source.Author;
                JsonSerializer.Serialize(writer, packageInfo, SerializeContexts.Default.PackageInfo);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }
    writer.WriteEndObject();
    await writer.FlushAsync();

    Console.WriteLine(Encoding.UTF8.GetString(bufferWriter.WrittenSpan));

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

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("author")]
    public Author? Author { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("unity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unity { get; set; }

    [JsonPropertyName("dependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Dependencies { get; set; }

    [JsonPropertyName("vpmDependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? VpmDependencies { get; set; }

    [JsonPropertyName("legacyFolders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? LegacyFolders { get; set; }

    [JsonPropertyName("legacyFiles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? LegacyFiles { get; set; }

    [JsonPropertyName("legacyPackages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? LegacyPackages { get; set; }

    [JsonPropertyName("changelogUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChangelogUrl { get; set; }

    [JsonPropertyName("zipSHA256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ZipSHA256 { get; set; }
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

[JsonSerializable(typeof(Source))]
[JsonSerializable(typeof(Release))]
[JsonSerializable(typeof(Release[]))]
[JsonSerializable(typeof(Release.Asset))]
[JsonSerializable(typeof(PackageInfo))]
[JsonSerializable(typeof(RepositoryPackageInfo))]
internal sealed partial class SerializeContexts : JsonSerializerContext;
