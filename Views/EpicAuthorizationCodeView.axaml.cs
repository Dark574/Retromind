using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Retromind.Helpers;
using Retromind.Resources;

namespace Retromind.Views;

public partial class EpicAuthorizationCodeView : Window
{
    private readonly Uri _authorizationUri;
    private TextBox _authorizationCodeBox = null!;
    private TextBlock _messageTextBlock = null!;
    private TextBlock _authorizationCodeLabel = null!;
    private Button _reopenBrowserButton = null!;
    private TextBlock _validationTextBlock = null!;

    public EpicAuthorizationCodeView()
    {
        _authorizationUri = new Uri("https://legendary.gl/epiclogin");
        InitializeComponent();
        ApplyLocalizedText();
    }

    public EpicAuthorizationCodeView(Uri authorizationUri)
        : this()
    {
        _authorizationUri = authorizationUri;
    }

    public string? AuthorizationCode { get; private set; }

    public async Task<string?> CaptureAsync(Window owner, CancellationToken ct)
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

        var code = accepted ? AuthorizationCode : null;
        _authorizationCodeBox.Text = string.Empty;
        AuthorizationCode = null;
        return code;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _authorizationCodeBox.Focus();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (!TryExtractAuthorizationCode(_authorizationCodeBox.Text, out var authorizationCode))
        {
            ShowValidationMessage(T(
                "Epic.Auth.InvalidCode",
                "Paste the authorizationCode value or the complete JSON response from Epic."));
            return;
        }

        AuthorizationCode = authorizationCode;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnReopenBrowserClick(object? sender, RoutedEventArgs e)
    {
        if (SystemBrowserLauncher.TryOpen(_authorizationUri, out _))
        {
            HideValidationMessage();
            return;
        }

        ShowValidationMessage(T("Epic.Auth.BrowserOpenFailed", "The system browser could not be opened."));
    }

    private void ApplyLocalizedText()
    {
        Title = T("Epic.Auth.Title", "Epic Games sign-in");
        _messageTextBlock.Text = T(
            "Epic.Auth.Instructions",
            "Sign in to Epic Games in your browser. The page then displays a JSON response. Copy either its authorizationCode value or the complete response and paste it here.");
        _authorizationCodeLabel.Text = T("Epic.Auth.CodeLabel", "Authorization code or JSON response");
        _reopenBrowserButton.Content = T("Epic.Auth.ReopenBrowser", "Reopen browser");
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
        _authorizationCodeBox = FindRequiredControl<TextBox>("AuthorizationCodeBox");
        _messageTextBlock = FindRequiredControl<TextBlock>("MessageTextBlock");
        _authorizationCodeLabel = FindRequiredControl<TextBlock>("AuthorizationCodeLabel");
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

    internal static bool TryExtractAuthorizationCode(string? input, out string authorizationCode)
    {
        authorizationCode = string.Empty;
        var trimmed = input?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 8192)
            return false;

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return false;

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "authorizationCode", StringComparison.OrdinalIgnoreCase) ||
                        property.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var jsonCode = property.Value.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(jsonCode))
                        return false;

                    authorizationCode = jsonCode;
                    return true;
                }

                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1].Trim();

        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Any(char.IsWhiteSpace))
            return false;

        authorizationCode = trimmed;
        return true;
    }
}
