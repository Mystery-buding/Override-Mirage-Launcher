using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace OverrideMirageLauncher.Core;

public sealed record DeviceCodeInfo(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int IntervalSeconds,
    int ExpiresInSeconds);

public sealed record AuthSession(
    string PlayerName,
    string Uuid,
    string AccessToken,
    string UserType,
    string Xuid,
    string ClientId)
{
    public static AuthSession Offline(string name) =>
        new(name, GameLauncher.OfflineUuid(name), "0", "legacy", "", "");

    public static AuthSession Microsoft(string name, string uuid, string accessToken, string xuid, string clientId) =>
        new(name, uuid, accessToken, "msa", xuid, clientId);
}

public sealed record MicrosoftAccount(
    string Name,
    string Uuid,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken);

public static class MicrosoftAuth
{

    public const string NoClientIdHint = "还没有配置 Azure 应用 ID";

    public static string ResolveClientId(string? configured) => (configured ?? string.Empty).Trim();

    public static bool HasClientId(string? configured) => !string.IsNullOrWhiteSpace(configured);

    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XboxUserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string MinecraftLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string MinecraftProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private const string Scope = "XboxLive.signin offline_access";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<DeviceCodeInfo> StartDeviceCodeAsync(string clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("尚未配置 Azure 应用 ID。");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["scope"] = Scope
        });

        using var response = await Http.PostAsync(DeviceCodeUrl, form, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("申请设备码失败（" + (int)response.StatusCode + "）：" + Describe(body));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return new DeviceCodeInfo(
            root.GetProperty("device_code").GetString() ?? "",
            root.GetProperty("user_code").GetString() ?? "",
            root.TryGetProperty("verification_uri", out var uri) ? uri.GetString() ?? "" : "https://microsoft.com/link",
            root.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5,
            root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900);
    }

    public static async Task<MicrosoftAccount> WaitForLoginAsync(
        string clientId, DeviceCodeInfo device, IProgress<string>? progress, CancellationToken ct,
        Action<string>? trace = null)
    {
        var interval = Math.Max(2, device.IntervalSeconds);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(device.ExpiresInSeconds);
        progress?.Report("等待你在浏览器里完成授权…");

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false);

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId.Trim(),
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = device.DeviceCode
            });

            using var response = await Http.PostAsync(TokenUrl, form, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var access = root.GetProperty("access_token").GetString() ?? "";
                var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";
                var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

                trace?.Invoke("微软授权成功，已拿到 access_token / refresh_token");
                progress?.Report("授权成功，正在换取 Minecraft 令牌…");
                return await ExchangeAsync(clientId, access, refresh, expiresIn, trace, ct).ConfigureAwait(false);
            }

            var code = ReadAuthError(body);
            switch (code)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "expired_token":
                    throw new TimeoutException("授权码已过期，请重新发起登录。");
                case "authorization_declined":
                case "access_denied":
                    throw new OperationCanceledException("你已取消授权。");
                default:
                    throw new InvalidOperationException("登录失败：" + Describe(body));
            }
        }

        throw new TimeoutException("等待授权超时，请重新发起登录。");
    }

    public static async Task<MicrosoftAccount> RefreshAsync(string clientId, string refreshToken, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId.Trim(),
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope
        });

        using var response = await Http.PostAsync(TokenUrl, form, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("登录已过期，请重新登录：" + Describe(body));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? "";
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? refreshToken : refreshToken;
        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        return await ExchangeAsync(clientId, access, refresh, expiresIn, null, ct).ConfigureAwait(false);
    }

    private static async Task<MicrosoftAccount> ExchangeAsync(
        string clientId, string microsoftToken, string refreshToken, int expiresIn,
        Action<string>? trace, CancellationToken ct)
    {

        var xblBody = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + microsoftToken
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        });

        var (xblOk, xblText) = await PostJsonAsync(XboxUserAuthUrl, xblBody, ct).ConfigureAwait(false);
        if (!xblOk)
        {
            var reason = DescribeXboxError(xblText);
            trace?.Invoke("第 1 步 Xbox Live 认证：失败 —— " + reason);
            trace?.Invoke("  原始响应：" + Trim(xblText));
            throw new InvalidOperationException("Xbox Live 认证失败：" + reason);
        }

        var xbl = ParseXboxToken(xblText);
        if (xbl.Token.Length == 0)
        {
            trace?.Invoke("第 1 步 Xbox Live 认证：响应里没有 token");
            throw new InvalidOperationException("Xbox Live 认证返回内容异常。");
        }

        trace?.Invoke("第 1 步 Xbox Live 认证：成功（拿到 user token）");

        var xstsBody = JsonSerializer.Serialize(new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xbl.Token }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        });

        var (xstsOk, xstsText) = await PostJsonAsync(XstsUrl, xstsBody, ct).ConfigureAwait(false);
        if (!xstsOk)
        {
            var reason = DescribeXboxError(xstsText);
            trace?.Invoke("第 2 步 XSTS 授权：失败 —— " + reason);
            trace?.Invoke("  原始响应：" + Trim(xstsText));
            throw new InvalidOperationException(reason);
        }

        var xsts = ParseXboxToken(xstsText);
        if (xsts.Token.Length == 0)
        {
            trace?.Invoke("第 2 步 XSTS 授权：响应里没有 token");
            throw new InvalidOperationException("XSTS 授权返回内容异常。");
        }

        trace?.Invoke("第 2 步 XSTS 授权：成功（拿到 XSTS token）");

        var mcBody = JsonSerializer.Serialize(new
        {
            identityToken = $"XBL3.0 x={xsts.UserHash};{xsts.Token}"
        });

        var (mcOk, mcText) = await PostJsonAsync(MinecraftLoginUrl, mcBody, ct).ConfigureAwait(false);
        if (!mcOk)
        {
            var reason = DescribeMinecraftError(clientId, mcText);
            trace?.Invoke("第 3 步 换取 Minecraft 令牌：失败 —— " + reason.Replace("\n", " "));
            trace?.Invoke("  原始响应：" + Trim(mcText));
            throw new InvalidOperationException(reason);
        }

        trace?.Invoke("第 3 步 换取 Minecraft 令牌：成功");

        string mcToken;
        int mcExpiresIn = expiresIn;
        using (var doc = JsonDocument.Parse(mcText))
        {
            mcToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
            if (doc.RootElement.TryGetProperty("expires_in", out var ei)) mcExpiresIn = ei.GetInt32();
        }

        if (mcToken.Length == 0)
            throw new InvalidOperationException("Minecraft 服务没有返回访问令牌。");

        using var request = new HttpRequestMessage(HttpMethod.Get, MinecraftProfileUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);

        using var profileResponse = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var profileText = await profileResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (profileResponse.StatusCode == HttpStatusCode.NotFound)
        {
            trace?.Invoke("第 4 步 读取玩家档案：404 —— 该账号没有 Java 版（没买 / 只买了基岩版 / 换过没迁移的 Mojang 账号）");
            throw new InvalidOperationException(
                "该 Microsoft 账号没有 Minecraft: Java Edition 档案。\n\n" +
                "常见原因：只买了基岩版、Java 版在另一个微软账号上、或旧的 Mojang 账号还没迁移到微软账号。");
        }

        if (!profileResponse.IsSuccessStatusCode)
        {
            trace?.Invoke($"第 4 步 读取玩家档案：失败（{(int)profileResponse.StatusCode}）—— " + Describe(profileText));
            throw new InvalidOperationException("读取玩家档案失败：" + Describe(profileText));
        }

        trace?.Invoke("第 4 步 读取玩家档案：成功");

        using (var profile = JsonDocument.Parse(profileText))
        {
            var root = profile.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var rawId = root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";

            if (name.Length == 0 || rawId.Length == 0)
                throw new InvalidOperationException("玩家档案缺少名字或 UUID。");

            return new MicrosoftAccount(
                name,
                FormatUuid(rawId),
                mcToken,
                DateTimeOffset.UtcNow.AddSeconds(mcExpiresIn),
                refreshToken);
        }
    }

    private static (string Token, string UserHash) ParseXboxToken(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var token = root.TryGetProperty("Token", out var t) ? t.GetString() ?? "" : "";
        var uhs = "";
        if (root.TryGetProperty("DisplayClaims", out var claims) &&
            claims.TryGetProperty("xui", out var xui) &&
            xui.ValueKind == JsonValueKind.Array &&
            xui.GetArrayLength() > 0 &&
            xui[0].TryGetProperty("uhs", out var uhsElement))
        {
            uhs = uhsElement.GetString() ?? "";
        }

        return (token, uhs);
    }

    private static string DescribeMinecraftError(string clientId, string json)
    {
        var type = "";
        var message = "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("errorType", out var t)) type = t.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("errorMessage", out var m)) message = m.GetString() ?? "";
            }
        }
        catch
        {

        }

        if (type.Contains("InvalidAppRegistration", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
        {
            return "Minecraft 服务不认这个应用 ID（Invalid app registration）。\n\n" +
                   "这不是你哪里配错了：Mojang 对第三方启动器有一份 client ID 白名单，" +
                   "新注册的 Azure 应用默认不在里面，所以微软授权、Xbox、XSTS 三步都成功，" +
                   "最后一步换 Minecraft 令牌被服务端拒绝。\n\n" +
                   "解决办法：到 Minecraft 官方提交「第三方启动器 API 访问申请」，" +
                   "把应用 ID " + clientId + " 报给他们审核（https://help.minecraft.net，" +
                   "搜索 Minecraft Services API / 第三方启动器接入）。审核通常需要几天到几周。\n\n" +
                   "审核通过之前可以先切到「离线登录」继续玩单人与离线服务器。";
        }

        return "换取 Minecraft 令牌失败：" + Describe(json);
    }

    private static string DescribeXboxError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("XErr", out _))
                return DescribeXstsError(json);
        }
        catch
        {

        }

        return "Xbox 授权失败：" + Describe(json);
    }

    private static string Trim(string body)
    {
        var text = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 400 ? text : text[..400] + "…";
    }

    private static string DescribeXstsError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("XErr", out var xerr))
            {
                return xerr.GetInt64() switch
                {
                    2148916233L => "该 Microsoft 账号还没有 Xbox 账户，请先到 xbox.com 创建。",
                    2148916235L => "该账号所在的国家或地区不支持 Xbox Live。",
                    2148916236L => "该账号需要通过成人验证后才能登录。",
                    2148916237L => "该账号需要通过成人验证后才能登录。",
                    2148916238L => "未成年账号需要先加入 Microsoft 家庭组。",
                    _ => $"Xbox 授权失败（XErr {xerr.GetInt64()}）。"
                };
            }
        }
        catch
        {

        }

        return "Xbox 授权失败：" + Describe(json);
    }

    private static async Task<(bool Ok, string Body)> PostJsonAsync(string url, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (response.IsSuccessStatusCode, body);
    }

    private static string ReadAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    private static string Describe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_description", out var desc))
                return desc.GetString() ?? body;
            if (root.TryGetProperty("Message", out var message))
                return message.GetString() ?? body;
            if (root.TryGetProperty("error", out var error))
                return error.GetString() ?? body;
        }
        catch
        {

        }

        return body.Length > 300 ? body[..300] : body;
    }

    private static string FormatUuid(string raw)
    {
        var hex = raw.Replace("-", "").ToLowerInvariant();
        if (hex.Length != 32) return raw;
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
