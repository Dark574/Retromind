using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Retromind.Services.Stores.Gog.Auth;

public sealed class GogOAuthLoopbackListener : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _expectedPath;
    private bool _disposed;

    public Uri RedirectUri { get; }

    public GogOAuthLoopbackListener(Uri redirectUri)
    {
        RedirectUri = redirectUri ?? throw new ArgumentNullException(nameof(redirectUri));
        if (!RedirectUri.IsAbsoluteUri)
            throw new ArgumentException("OAuth redirect URI must be absolute.", nameof(redirectUri));

        if (!string.Equals(RedirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("OAuth loopback redirect URI must use HTTP.", nameof(redirectUri));

        _expectedPath = string.IsNullOrWhiteSpace(RedirectUri.AbsolutePath)
            ? "/"
            : RedirectUri.AbsolutePath;

        var normalizedPath = _expectedPath.EndsWith("/", StringComparison.Ordinal)
            ? _expectedPath
            : _expectedPath + "/";

        var prefix = $"{RedirectUri.Scheme}://{RedirectUri.Host}:{RedirectUri.Port}{normalizedPath}";
        _listener.Prefixes.Add(prefix);
    }

    public async Task<OAuthCallbackResult> WaitForCallbackAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_listener.IsListening)
            throw new InvalidOperationException("OAuth loopback listener is already running.");

        _listener.Start();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var context = await WaitForContextAsync(ct).ConfigureAwait(false);
                var requestUrl = context.Request.Url;

                if (requestUrl == null)
                {
                    await WriteHtmlResponseAsync(
                            context.Response,
                            HttpStatusCode.BadRequest,
                            "Invalid callback",
                            "The OAuth callback URL was invalid.")
                        .ConfigureAwait(false);
                    continue;
                }

                if (!string.Equals(requestUrl.AbsolutePath, _expectedPath, StringComparison.Ordinal))
                {
                    await WriteHtmlResponseAsync(
                            context.Response,
                            HttpStatusCode.NotFound,
                            "Not Found",
                            "Unexpected callback path.")
                        .ConfigureAwait(false);
                    continue;
                }

                var query = HttpUtility.ParseQueryString(requestUrl.Query);
                var error = query["error"];
                if (!string.IsNullOrWhiteSpace(error))
                {
                    await WriteHtmlResponseAsync(
                            context.Response,
                            HttpStatusCode.BadRequest,
                            "Authentication failed",
                            "The login process returned an error.")
                        .ConfigureAwait(false);

                    throw new InvalidOperationException($"GOG OAuth callback error: {error}");
                }

                var code = query["code"];
                if (string.IsNullOrWhiteSpace(code))
                {
                    await WriteHtmlResponseAsync(
                            context.Response,
                            HttpStatusCode.BadRequest,
                            "Authentication failed",
                            "The callback did not include an authorization code.")
                        .ConfigureAwait(false);

                    throw new InvalidOperationException("GOG OAuth callback did not include an authorization code.");
                }

                var state = query["state"];
                await WriteHtmlResponseAsync(
                        context.Response,
                        HttpStatusCode.OK,
                        "Login complete",
                        "You can close this window and return to Retromind.")
                    .ConfigureAwait(false);

                return new OAuthCallbackResult(code, state);
            }
        }
        finally
        {
            if (_listener.IsListening)
                _listener.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _listener.Close();
    }

    private async Task<HttpListenerContext> WaitForContextAsync(CancellationToken ct)
    {
        var contextTask = _listener.GetContextAsync();
        var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
        var completedTask = await Task.WhenAny(contextTask, cancelTask).ConfigureAwait(false);

        if (completedTask != contextTask)
            throw new OperationCanceledException(ct);

        return await contextTask.ConfigureAwait(false);
    }

    private static async Task WriteHtmlResponseAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        string title,
        string message)
    {
        var titleEncoded = WebUtility.HtmlEncode(title);
        var messageEncoded = WebUtility.HtmlEncode(message);
        var html = $"""
                    <!doctype html>
                    <html lang="en">
                    <head>
                      <meta charset="utf-8">
                      <title>{titleEncoded}</title>
                    </head>
                    <body>
                      <h2>{titleEncoded}</h2>
                      <p>{messageEncoded}</p>
                    </body>
                    </html>
                    """;

        var payload = Encoding.UTF8.GetBytes(html);
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = payload.Length;

        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        response.Close();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GogOAuthLoopbackListener));
    }
}
