using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Retromind.Views;

public partial class GogAuthenticationView : Window
{
    private Uri _redirectUri = new("https://embed.gog.com/on_login_success");
    private string? _sessionDirectory;
    private string? _dataDirectory;
    private string? _cacheDirectory;
    private NativeWebView _loginWebView = null!;
    private TextBlock _statusTextBlock = null!;
    private bool _authenticationActive;
    private bool _authenticationCompleted;

    public GogAuthenticationView()
    {
        InitializeComponent();
    }

    public GogAuthenticationView(Uri authorizeUri, Uri redirectUri, string title, string message)
        : this()
    {
        _redirectUri = redirectUri;
        Title = title;
        _statusTextBlock.Text = message;

        CreateSessionDirectories();
        _loginWebView.Source = authorizeUri;
    }

    public Uri? CallbackUri { get; private set; }
    public Exception? InitializationException { get; private set; }

    public async Task<bool> AuthenticateAsync(Window owner, CancellationToken ct)
    {
        _authenticationActive = true;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        using var cancellationRegistration = ct.Register(() =>
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible)
                    Close(false);
            }));

        try
        {
            return await ShowDialog<bool>(owner);
        }
        finally
        {
            _authenticationActive = false;
            Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandledException;
            await DeleteSessionDirectoryAsync();
        }
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is not LinuxWpeWebViewEnvironmentRequestedEventArgs wpe)
            return;

        wpe.DataDirectory = _dataDirectory;
        wpe.CacheDirectory = _cacheDirectory;
        wpe.RenderingMode = WpeRenderingMode.Shm;
        wpe.PreferWebKitGtkInstead = false;
    }

    private void OnAdapterCreated(object? sender, WebViewAdapterEventArgs e)
    {
        if (_loginWebView.AdapterInfo?.Type != WebViewAdapterType.WpeWebKit)
        {
            FailInitialization(new PlatformNotSupportedException("The GOG login dialog requires WPE WebKit."));
            return;
        }

        _statusTextBlock.IsVisible = false;
    }

    private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request == null || !IsCallbackUri(e.Request, _redirectUri))
            return;

        e.Cancel = true;
        CallbackUri = e.Request;
        _authenticationCompleted = true;
        Close(true);
    }

    private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess && !_authenticationCompleted)
            FailInitialization(new InvalidOperationException("The embedded GOG login page could not be loaded."));
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (!_authenticationActive || _authenticationCompleted || !IsWebViewException(e.Exception))
            return;

        e.Handled = true;
        FailInitialization(e.Exception);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void FailInitialization(Exception exception)
    {
        InitializationException ??= exception;
        if (IsVisible)
            Close(false);
    }

    private void CreateSessionDirectories()
    {
        _sessionDirectory = Path.Combine(
            Path.GetTempPath(),
            "retromind-gog-auth",
            Guid.NewGuid().ToString("N"));
        _dataDirectory = Path.Combine(_sessionDirectory, "data");
        _cacheDirectory = Path.Combine(_sessionDirectory, "cache");

        try
        {
            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(_cacheDirectory);

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    _sessionDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch
        {
            TryDeleteSessionDirectory(_sessionDirectory);
            throw;
        }
    }

    private async Task DeleteSessionDirectoryAsync()
    {
        var sessionDirectory = _sessionDirectory;
        _sessionDirectory = null;
        _dataDirectory = null;
        _cacheDirectory = null;

        if (string.IsNullOrWhiteSpace(sessionDirectory))
            return;

        await Task.Run(() => TryDeleteSessionDirectory(sessionDirectory));
    }

    private static void TryDeleteSessionDirectory(string? sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory) || !Directory.Exists(sessionDirectory))
            return;

        try
        {
            Directory.Delete(sessionDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Could not remove temporary WPE login data: {ex.Message}");
        }
    }

    internal static bool IsCallbackUri(Uri candidate, Uri callback)
    {
        if (!string.Equals(candidate.Scheme, callback.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Host, callback.Host, StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != callback.Port ||
            !string.Equals(candidate.AbsolutePath, callback.AbsolutePath, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedQuery = HttpUtility.ParseQueryString(callback.Query);
        var candidateQuery = HttpUtility.ParseQueryString(candidate.Query);
        foreach (var key in expectedQuery.AllKeys)
        {
            if (key == null || !string.Equals(expectedQuery[key], candidateQuery[key], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool IsWebViewException(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current.StackTrace?.Contains("Avalonia.Controls.WebView", StringComparison.Ordinal) == true ||
                current.Message.Contains("WPE", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("WebKit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _loginWebView = FindRequiredControl<NativeWebView>("LoginWebView");
        _statusTextBlock = FindRequiredControl<TextBlock>("StatusTextBlock");
    }

    private TControl FindRequiredControl<TControl>(string name)
        where TControl : Control
    {
        return this.FindControl<TControl>(name)
               ?? throw new InvalidOperationException($"Required control '{name}' was not found.");
    }
}
