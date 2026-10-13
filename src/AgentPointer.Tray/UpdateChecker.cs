using System.Net.Http;
using System.Text.Json;

namespace AgentPointer.Tray;

/// <summary>一次更新检查的结果。</summary>
/// <param name="Kind">结果类别。</param>
/// <param name="Latest">远端最新版本号（有新版时给出）。</param>
/// <param name="ReleaseUrl">新版本的发布页（有新版时给出，供复制）。</param>
internal sealed record UpdateResult(UpdateKind Kind, string? Latest = null, string? ReleaseUrl = null);

internal enum UpdateKind
{
    NotConfigured,
    UpToDate,
    Available,
    Failed,
}

/// <summary>
/// 更新检查：对比 GitHub Releases 的最新 tag 与当前版本。
///
/// <para>只访问 <c>https://api.github.com</c> 一个固定主机，请求前显式校验 host，
/// 不接受任何来自输入的 URL；不带凭据，无副作用。</para>
/// </summary>
internal static class UpdateChecker
{
    /// <summary>公开发布仓库。</summary>
    public const string? Repository = "castorhrio/agent-pointer-desktop";
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Repository);

    private const string ApiHost = "api.github.com";

    // 复用连接：手动检查也可能被反复触发，避免 socket 堆积
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentPointer-Tray");
        return client;
    }

    public static async Task<UpdateResult> CheckAsync(string currentVersion)
    {
        if (!IsConfigured) return new UpdateResult(UpdateKind.NotConfigured);
        try
        {
            var uri = new Uri($"https://{ApiHost}/repos/{Repository}/releases/latest", UriKind.Absolute);

            // 固定主机再校验一次：防重定向后被带去别的地址
            if (uri.Host != ApiHost || uri.Scheme != "https")
            {
                return new UpdateResult(UpdateKind.Failed);
            }

            using var response = await Http.GetAsync(uri);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateResult(UpdateKind.Failed);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var tag = doc.RootElement.TryGetProperty("tag_name", out var tagEl) && tagEl.GetString() is { } t
                ? t.TrimStart('v', 'V')
                : null;

            if (tag is null || !Version.TryParse(tag, out var remote))
            {
                return new UpdateResult(UpdateKind.Failed);
            }

            if (!Version.TryParse(currentVersion, out var local))
            {
                return new UpdateResult(UpdateKind.Failed);
            }

            if (remote > local)
            {
                var url = doc.RootElement.TryGetProperty("html_url", out var urlEl)
                    ? urlEl.GetString()
                    : $"https://github.com/{Repository}/releases";
                return new UpdateResult(UpdateKind.Available, tag, url);
            }

            return new UpdateResult(UpdateKind.UpToDate);
        }
        catch (Exception)
        {
            // 网络失败静默降级：UI 显示"暂时无法检查"
            return new UpdateResult(UpdateKind.Failed);
        }
    }
}
