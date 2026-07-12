using System.Text.Json;

namespace Surveil.Core;

public sealed class JsonStore
{
    public const string ConfigFileName = "buildings.json";
    public const string InventoryFileName = "cameras.json";

    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string DataDirectory { get; }

    public JsonStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Surveil");
    }

    public async Task<SurveilConfig> LoadConfigAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(DataDirectory, ConfigFileName);
        if (!File.Exists(path)) return new SurveilConfig();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SurveilConfig>(stream, Options, cancellationToken)
               ?? new SurveilConfig();
    }

    public async Task SaveConfigAsync(SurveilConfig config, CancellationToken cancellationToken = default)
    {
        ConfigValidator.Validate(config);
        await AtomicWriteAsync(Path.Combine(DataDirectory, ConfigFileName), config, cancellationToken);
    }

    public async Task<Inventory> LoadInventoryAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(DataDirectory, InventoryFileName);
        if (!File.Exists(path)) return new Inventory();
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Inventory>(stream, Options, cancellationToken)
               ?? new Inventory();
    }

    public Task SaveInventoryAsync(Inventory inventory, CancellationToken cancellationToken = default) =>
        AtomicWriteAsync(Path.Combine(DataDirectory, InventoryFileName), inventory, cancellationToken);

    private static async Task AtomicWriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
