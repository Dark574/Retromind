using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Retromind.Helpers;
using Retromind.Resources;

namespace Retromind.Views;

public partial class GogBrowserCallbackView : Window
{
    private readonly Uri _authorizeUri;
    private readonly Uri _expectedCallbackUri;
    private TextBox _callbackUrlBox = null!;
    private TextBlock _messageTextBlock = null!;
    private TextBlock _callbackUrlLabel = null!;
    private Button _reopenBrowserButton = null!;
    private TextBlock _validationTextBlock = null!;

    public GogBrowserCallbackView()
    {
        _authorizeUri = new Uri("https://auth.gog.com/auth");
        _expectedCallbackUri = new Uri("https://embed.gog.com/on_login_success?origin=client");
        InitializeComponent();
        ApplyLocalizedText();
    }

    public GogBrowserCallbackView(Uri authorizeUri, Uri expectedCallbackUri)
        : this()
    {
        _authorizeUri = authorizeUri;
        _expectedCallbackUri = expectedCallbackUri;
    }

    public Uri? CallbackUri { get; private set; }

    public async Task<Uri?> CaptureAsync(Window owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cancellationRegistration = ct.Register(() =>
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible)
                    Close(false);
            }));

        var accepted = await ShowDialog<bool>(owner);
        ct.ThrowIfCancellationRequested();
        return accepted ? CallbackUri : null;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _callbackUrlBox.Focus();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var validation = ValidateCallbackInput(
            _callbackUrlBox.Text,
            _expectedCallbackUri,
            out var callbackUri);

        if (validation != CallbackInputValidation.Valid)
        {
            ShowValidationMessage(validation == CallbackInputValidation.InvalidUri
                ? T("Gog.CallbackInvalidUri", "The entered value is not a valid URL.")
                : T("Gog.CallbackUnexpectedEndpoint", "The URL does not match the expected GOG callback address."));
            return;
        }

        CallbackUri = callbackUri;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnReopenBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (SystemBrowserLauncher.TryOpen(_authorizeUri, out _))
        {
            HideValidationMessage();
            return;
        }

        ShowValidationMessage(T("Gog.BrowserOpenFailed", "The system browser could not be opened."));
    }

    private void ApplyLocalizedText()
    {
        Title = T("Gog.EmbeddedLoginTitle", "GOG sign-in");
        _messageTextBlock.Text = T(
            "Gog.BrowserFallbackHint",
            "Complete sign-in in your browser. Then copy the final URL from the browser address bar and paste it here.");
        _callbackUrlLabel.Text = T("Gog.CallbackUrlLabel", "Final callback URL");
        _reopenBrowserButton.Content = T("Gog.ReopenBrowser", "Reopen browser");
    }

    private void ShowValidationMessage(string message)
    {
        _validationTextBlock.Text = message;
        _validationTextBlock.IsVisible = true;
    }

    private void HideValidationMessage()
    {
        _validationTextBlock.Text = string.Empty;
        _validationTextBlock.IsVisible = false;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _callbackUrlBox = FindRequiredControl<TextBox>("CallbackUrlBox");
        _messageTextBlock = FindRequiredControl<TextBlock>("MessageTextBlock");
        _callbackUrlLabel = FindRequiredControl<TextBlock>("CallbackUrlLabel");
        _reopenBrowserButton = FindRequiredControl<Button>("ReopenBrowserButton");
        _validationTextBlock = FindRequiredControl<TextBlock>("ValidationTextBlock");
    }

    private TControl FindRequiredControl<TControl>(string name)
        where TControl : Control
    {
        return this.FindControl<TControl>(name)
               ?? throw new InvalidOperationException($"Required control '{name}' was not found.");
    }

    private static string T(string key, string fallback)
    {
        var value = Strings.ResourceManager.GetString(key, Strings.Culture);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    internal static CallbackInputValidation ValidateCallbackInput(
        string? input,
        Uri expectedCallbackUri,
        out Uri? callbackUri)
    {
        callbackUri = null;
        if (string.IsNullOrWhiteSpace(input) ||
            !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var parsedCallbackUri))
        {
            return CallbackInputValidation.InvalidUri;
        }

        if (!GogAuthenticationView.IsCallbackUri(parsedCallbackUri, expectedCallbackUri))
            return CallbackInputValidation.UnexpectedEndpoint;

        callbackUri = parsedCallbackUri;
        return CallbackInputValidation.Valid;
    }

    internal enum CallbackInputValidation
    {
        Valid,
        InvalidUri,
        UnexpectedEndpoint
    }
}
