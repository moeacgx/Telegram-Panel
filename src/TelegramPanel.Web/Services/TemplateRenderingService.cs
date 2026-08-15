using System.Text;
using System.Text.RegularExpressions;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Web.Services;

/// <summary>
/// 模板变量解析服务。
/// </summary>
public sealed class TemplateRenderingService
{
    private static readonly Regex TokenRegex = new("\\{(?<name>[a-zA-Z0-9_]+)\\}", RegexOptions.Compiled);
    private readonly DataDictionaryService _dataDictionaryService;

    public TemplateRenderingService(DataDictionaryService dataDictionaryService)
    {
        _dataDictionaryService = dataDictionaryService;
    }

    public IReadOnlyList<string> ExtractTokenNames(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return Array.Empty<string>();

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TokenRegex.Matches(template))
        {
            if (!match.Success)
                continue;

            var tokenName = (match.Groups["name"].Value ?? string.Empty).Trim();
            if (tokenName.Length == 0 || !seen.Add(tokenName))
                continue;

            names.Add(tokenName);
        }

        return names;
    }

    public async Task ValidateTextTemplateAsync(string? template, CancellationToken cancellationToken = default)
    {
        foreach (var tokenName in ExtractTokenNames(template))
        {
            if (string.Equals(tokenName, "time", StringComparison.OrdinalIgnoreCase))
                continue;

            await EnsureDictionaryAvailableAsync(tokenName, DataDictionaryTypes.Text, cancellationToken);
        }
    }

    public async Task ValidateTextDictionaryTokenAsync(string? tokenExpression, CancellationToken cancellationToken = default)
    {
        var tokenName = ExtractSingleTokenName(tokenExpression)
            ?? throw new InvalidOperationException("目标字典必须是单个文本字典变量，例如 {groups}");

        if (string.Equals(tokenName, "time", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标字典不能使用内置时间变量 {time}");

        await EnsureDictionaryAvailableAsync(tokenName, DataDictionaryTypes.Text, cancellationToken);
    }

    public async Task ValidateImageTemplateAsync(string? tokenExpression, CancellationToken cancellationToken = default)
    {
        var tokenName = ExtractSingleTokenName(tokenExpression)
            ?? throw new InvalidOperationException("图片变量必须是单个字典变量，例如 {avatar}");

        await EnsureDictionaryAvailableAsync(tokenName, DataDictionaryTypes.Image, cancellationToken);
    }

    public async Task<string> RenderTextTemplateAsync(string template, CancellationToken cancellationToken = default)
    {
        template = template ?? string.Empty;
        var matches = TokenRegex.Matches(template);
        if (matches.Count == 0)
            return NormalizeEscapedNewlines(template);

        var builder = new StringBuilder();
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            if (!match.Success)
                continue;

            builder.Append(template, lastIndex, match.Index - lastIndex);
            var tokenName = match.Groups["name"].Value;
            var resolved = await ResolveTextTokenAsync(tokenName, cancellationToken);
            builder.Append(resolved);
            lastIndex = match.Index + match.Length;
        }

        builder.Append(template, lastIndex, template.Length - lastIndex);
        return NormalizeEscapedNewlines(builder.ToString());
    }

    public static string NormalizeEscapedNewlines(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("/n", "\n", StringComparison.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ResolveTextDictionaryValuesAsync(string tokenExpression, CancellationToken cancellationToken = default)
    {
        var tokenName = ExtractSingleTokenName(tokenExpression)
            ?? throw new InvalidOperationException("目标字典必须是单个文本字典变量，例如 {groups}");

        if (string.Equals(tokenName, "time", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标字典不能使用内置时间变量 {time}");

        var dictionary = await _dataDictionaryService.GetByNameAsync(tokenName, cancellationToken)
            ?? throw new InvalidOperationException($"未找到变量：{{{tokenName}}}");

        if (!dictionary.IsEnabled)
            throw new InvalidOperationException($"变量已停用：{{{tokenName}}}");

        if (!string.Equals(dictionary.Type, DataDictionaryTypes.Text, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"变量类型不匹配：{{{tokenName}}} 需要使用文本字典");

        var values = dictionary.Items
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .Select(x => NormalizeEscapedNewlines((x.TextValue ?? string.Empty).Trim()))
            .Where(x => x.Length > 0)
            .ToList();

        if (values.Count == 0)
            throw new InvalidOperationException($"变量没有可用内容：{{{tokenName}}}");

        return values;
    }

    public async Task<StoredImageAssetInfo> ResolveImageTemplateAsync(string tokenExpression, CancellationToken cancellationToken = default)
    {
        var tokenName = ExtractSingleTokenName(tokenExpression)
            ?? throw new InvalidOperationException("图片变量必须是单个字典变量，例如 {avatar}");
        return await _dataDictionaryService.ResolveImageValueAsync(tokenName, cancellationToken);
    }

    public string? ExtractSingleTokenName(string? tokenExpression)
    {
        var text = (tokenExpression ?? string.Empty).Trim();
        if (text.Length == 0)
            return null;

        var match = TokenRegex.Match(text);
        if (!match.Success || match.Index != 0 || match.Length != text.Length)
            return null;

        return match.Groups["name"].Value;
    }

    private async Task<string> ResolveTextTokenAsync(string tokenName, CancellationToken cancellationToken)
    {
        if (string.Equals(tokenName, "time", StringComparison.OrdinalIgnoreCase))
            return DateTime.Now.ToString("yyyyMMddHHmmss");

        return await _dataDictionaryService.ResolveTextValueAsync(tokenName, cancellationToken);
    }

    private async Task EnsureDictionaryAvailableAsync(string tokenName, string expectedType, CancellationToken cancellationToken)
    {
        var dictionary = await _dataDictionaryService.GetByNameAsync(tokenName, cancellationToken)
            ?? throw new InvalidOperationException($"未找到变量：{{{tokenName}}}");

        if (!dictionary.IsEnabled)
            throw new InvalidOperationException($"变量已停用：{{{tokenName}}}");

        if (!string.Equals(dictionary.Type, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            var expectedName = string.Equals(expectedType, DataDictionaryTypes.Image, StringComparison.OrdinalIgnoreCase)
                ? "图片字典"
                : "文本字典";
            throw new InvalidOperationException($"变量类型不匹配：{{{tokenName}}} 需要使用{expectedName}");
        }

        var hasAvailableItem = string.Equals(expectedType, DataDictionaryTypes.Image, StringComparison.OrdinalIgnoreCase)
            ? dictionary.Items.Any(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.AssetPath))
            : dictionary.Items.Any(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.TextValue));

        if (!hasAvailableItem)
        {
            var valueName = string.Equals(expectedType, DataDictionaryTypes.Image, StringComparison.OrdinalIgnoreCase)
                ? "图片"
                : "内容";
            throw new InvalidOperationException($"变量没有可用{valueName}：{{{tokenName}}}");
        }
    }
}
