using System.Text.Json;

namespace Surveil.Core;

public interface IConfigurationRepository
{
    Task<SurveilConfig> LoadConfigAsync(CancellationToken cancellationToken = default);
    Task SaveConfigAsync(SurveilConfig config, CancellationToken cancellationToken = default);
    Task<SurveilConfig> ImportConfigAsync(string sourcePath, CancellationToken cancellationToken = default);
    Task ExportConfigAsync(string destinationPath, CancellationToken cancellationToken = default);
}

public interface IInventoryRepository
{
    Task<Inventory> LoadInventoryAsync(CancellationToken cancellationToken = default);
    Task SaveInventoryAsync(Inventory inventory, CancellationToken cancellationToken = default);
}

/// <summary>Reusable atomic JSON persistence for configuration, inventory, and app settings.</summary>
public static class AtomicJsonFile
{
    public static async Task WriteAsync<T>(string path, T value, JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Write<T>(string path, T value, JsonSerializerOptions options) =>
        WriteAsync(path, value, options).GetAwaiter().GetResult();
}
