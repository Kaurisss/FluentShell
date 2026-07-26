using System.Text.Json;

namespace FluentShell.Services;

/// <summary>
/// 以原子方式读写单个 JSON 文档：写入先落 .tmp 再覆盖目标，读取失败回退到默认值。
/// 目录由构造参数注入，不读取环境路径。
/// </summary>
public sealed class JsonDocumentStore<T>
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly string _filePath;
    private readonly Func<T> _createDefault;

    public JsonDocumentStore(string folder, string fileName, Func<T> createDefault)
    {
        _folder = folder;
        _filePath = Path.Combine(folder, fileName);
        _createDefault = createDefault;
    }

    public async Task<T> LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath)) return _createDefault();
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options) ?? _createDefault();
        }
        catch
        {
            return _createDefault();
        }
    }

    public async Task SaveAsync(T value)
    {
        Directory.CreateDirectory(_folder);
        var tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options);
        }
        File.Move(tempPath, _filePath, true);
    }
}
