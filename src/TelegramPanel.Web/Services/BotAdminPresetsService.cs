using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace TelegramPanel.Web.Services;

public sealed record BotAdminPreset(string Name, IReadOnlyList<long> UserIds);

/// <summary>
/// Bot 管理员 ID 预设（保存到 appsettings.local.json，避免手动改 JSON 配置）
/// </summary>
public sealed class BotAdminPresetsService
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly string _configFilePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public BotAdminPresetsService(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
        _configFilePath = LocalConfigFile.ResolvePath(configuration, environment);
    }

    public async Task<IReadOnlyList<BotAdminPreset>> GetPresetsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_configFilePath))
                return Array.Empty<BotAdminPreset>();

            var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null)
                return Array.Empty<BotAdminPreset>();

            if (root["BotAdminPresets"] is not JsonObject section)
                return Array.Empty<BotAdminPreset>();

            if (section["Presets"] is not JsonObject presetsObj)
                return Array.Empty<BotAdminPreset>();

            var list = new List<BotAdminPreset>();
            foreach (var kv in presetsObj)
            {
                var name = (kv.Key ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var ids = new List<long>();
                if (kv.Value is JsonArray arr)
                {
                    foreach (var node in arr)
                    {
                        if (node == null)
                            continue;

                        if (node is JsonValue v)
                        {
                            if (v.TryGetValue<long>(out var id) && id > 0)
                                ids.Add(id);
                            else if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var sid) && sid > 0)
                                ids.Add(sid);
                        }
                    }
                }

                ids = ids.Distinct().ToList();
                if (ids.Count == 0)
                    continue;

                list.Add(new BotAdminPreset(name, ids));
            }

            return list
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<BotAdminPreset>();
        }
    }

    public async Task SavePresetAsync(string name, IReadOnlyList<long> userIds, CancellationToken cancellationToken = default)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("预设名称不能为空", nameof(name));

        var ids = (userIds ?? Array.Empty<long>()).Where(x => x > 0).Distinct().ToList();
        if (ids.Count == 0)
            throw new ArgumentException("预设用户 ID 不能为空", nameof(userIds));

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await LocalConfigFile.EnsureExistsAsync(_configFilePath, cancellationToken);

            var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
            var root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();

            var section = root["BotAdminPresets"] as JsonObject ?? new JsonObject();
            var presetsObj = section["Presets"] as JsonObject ?? new JsonObject();

            var arr = new JsonArray();
            foreach (var id in ids)
                arr.Add(id);

            presetsObj[name] = arr;
            section["Presets"] = presetsObj;
            root["BotAdminPresets"] = section;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var updatedJson = JsonSerializer.Serialize(root, options);
            await LocalConfigFile.WriteJsonAtomicallyAsync(_configFilePath, updatedJson, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeletePresetAsync(string name, CancellationToken cancellationToken = default)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_configFilePath))
                return;

            var json = await File.ReadAllTextAsync(_configFilePath, cancellationToken);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null)
                return;

            if (root["BotAdminPresets"] is not JsonObject section)
                return;

            if (section["Presets"] is not JsonObject presetsObj)
                return;

            if (!presetsObj.Remove(name))
                return;

            section["Presets"] = presetsObj;
            root["BotAdminPresets"] = section;

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var updatedJson = JsonSerializer.Serialize(root, options);
            await LocalConfigFile.WriteJsonAtomicallyAsync(_configFilePath, updatedJson, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
