using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using LookingGlass.Server.Admin;

namespace LookingGlass.Tests;

/// <summary>
/// A test server with the operator web page on, its page host on a test server of its own (as <c>tailscale serve</c> would reach
/// its socket), and requests to it with the headers <c>tailscale serve</c> would set: a Tailscale login, the address asked for,
/// the device's tailnet address. Each header can be left out or changed, to play everything else.
/// </summary>
public sealed partial class PageServer : IAsyncDisposable {
    public const string Login = "alice@github";
    public const string Host = "lg-test.tailnet-0000.ts.net:8444";
    public const string Url = "https://" + Host + "/";
    public const string Origin = "https://" + Host;
    public const string TailnetAddress = "100.101.102.103";

    /// <summary>The settings that turn the page on, for <see cref="Login"/> at <see cref="Url"/>.</summary>
    public static readonly (string Key, string Value)[] On = [
        ("LookingGlass:Admin:Listen", "http://127.0.0.1:5999"), ("LookingGlass:Admin:Url", Url), ("LookingGlass:Admin:TailscaleLogins:0", Login),
    ];

    /// <param name="settings">More settings (after <see cref="On"/>, so they can change its).</param>
    /// <param name="services">More changes to the server's services (as <c>ConfigureTestServices</c>).</param>
    public PageServer(TimeProvider? time = null, FakeDiscord? discord = null, CapturingLoggerProvider? logs = null, string? directory = null,
        Action<IServiceCollection>? services = null, params (string Key, string Value)[] settings) {
        this.Server = new Harness(directory, serverTime: time, logs: logs, discord: discord,
            testServices: collection => {
                collection.AddSingleton(new OperatorPageListener(web => web.UseTestServer()));
                services?.Invoke(collection);
            },
            settings: [.. On, .. settings]);
    }

    public Harness Server { get; }

    /// <summary>The page's own host, as a test server.</summary>
    public TestServer Admin => (TestServer) this.Server.Factory.Services.GetRequiredService<OperatorPageHost>().Server!;

    public ValueTask DisposeAsync() => this.Server.DisposeAsync();

    /// <summary>A request as <c>tailscale serve</c> passes it on; null leaves a header out.</summary>
    public static HttpRequestMessage Request(HttpMethod method, string path, string? login = Login, string? host = Host, bool funnel = false,
        string? forwardedFor = TailnetAddress) {
        var request = new HttpRequestMessage(method, path);
        if (login != null) {
            request.Headers.TryAddWithoutValidation("Tailscale-User-Login", login);
            request.Headers.TryAddWithoutValidation("Tailscale-User-Name", "Alice");
        }

        if (host != null) {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
        }

        if (forwardedFor != null) {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        if (funnel) {
            request.Headers.TryAddWithoutValidation("Tailscale-Funnel-Request", "?1");
        }

        return request;
    }

    /// <summary>Sends a request to the page's host, and reads the answer.</summary>
    public async Task<Answer> SendAsync(HttpRequestMessage request) {
        using var client = this.Admin.CreateClient();
        using (request) {
            var response = await client.SendAsync(request, Harness.Ct);
            return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(Harness.Ct), response);
        }
    }

    public Task<Answer> GetAsync(string path, string? login = Login, string? host = Host, bool funnel = false, string? forwardedFor = TailnetAddress) =>
        this.SendAsync(Request(HttpMethod.Get, path, login, host, funnel, forwardedFor));

    /// <summary>Sends a form as a browser on the page would: with the page's Origin (or another, or none, and <c>Sec-Fetch-Site</c> if given).</summary>
    public Task<Answer> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> form, string? login = Login, string? origin = Origin, string? fetchSite = null,
        string? host = Host) {
        var request = Request(HttpMethod.Post, path, login, host);
        request.Content = new FormUrlEncodedContent(form);
        if (origin != null) {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (fetchSite != null) {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        }

        return this.SendAsync(request);
    }

    /// <summary>Sends the form at <paramref name="action"/> on a page as it is (its hidden fields), with <paramref name="changes"/> made.</summary>
    public Task<Answer> SubmitAsync(Answer page, string action, params (string Name, string Value)[] changes) {
        var fields = Fields(page.Html, action);
        foreach (var (name, value) in changes) {
            fields[name] = value;
        }

        return this.PostAsync(action, fields);
    }

    /// <summary>The fields of the form posting to <paramref name="action"/> on a page (hidden ones, and text boxes' values), decoded.</summary>
    public static Dictionary<string, string> Fields(string html, string action) {
        var form = Regex.Match(html, $"<form[^>]*action=\"{Regex.Escape(action)}\"[^>]*>(.*?)</form>", RegexOptions.Singleline);
        Assert.True(form.Success, $"No form posting to {action} on the page:\n{html}");
        var fields = new Dictionary<string, string>();
        foreach (Match input in InputPattern().Matches(form.Groups[1].Value)) {
            var name = Attribute(input.Value, "name");
            var type = Attribute(input.Value, "type");
            if (name != null && type is "hidden" or "text" or "number") {
                fields[name] = WebUtility.HtmlDecode(Attribute(input.Value, "value") ?? "");
            }
        }

        return fields;
    }

    /// <summary>The page's text without its tags, decoded, for checking what it says.</summary>
    public static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " "));

    private static string? Attribute(string tag, string name) {
        var match = Regex.Match(tag, $"\\s{name}=\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("<input[^>]*>", RegexOptions.Singleline)]
    private static partial Regex InputPattern();
}

/// <summary>An answer from the page's host.</summary>
public sealed record Answer(HttpStatusCode Status, string Html, HttpResponseMessage Response) {
    /// <summary>The page's text, without tags.</summary>
    public string Text => PageServer.Text(this.Html);

    public string? Header(string name) =>
        this.Response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values)
        : this.Response.Content.Headers.TryGetValues(name, out var content) ? string.Join(", ", content) : null;
}
