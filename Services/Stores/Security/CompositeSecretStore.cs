using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Stores.Security;

/// <summary>Uses the primary store when possible, with authoritative session-only overrides on failed writes.</summary>
public sealed class CompositeSecretStore : ISecretStore
{
    private readonly ISecretStore _primary;
    private readonly ISecretStore _fallback;
    private readonly IErrorService _errors;
    private readonly bool _sessionOnly;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<SecretKey> _sessionKeys = new();
    private readonly HashSet<SecretKey> _deletedKeys = new();
    private readonly ConcurrentDictionary<SecretKey, SecretStorageStatus> _status = new();

    public CompositeSecretStore(ISecretStore primary, ISecretStore fallback, IErrorService errors, bool sessionOnly = false)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
        _sessionOnly = sessionOnly;
    }

    public SecretStorageStatus GetStorageStatus(SecretKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _status.GetValueOrDefault(key, SecretStorageStatus.Unknown);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_sessionOnly && await IsPrimaryAvailableAsync("Availability", ct).ConfigureAwait(false))
            return true;
        try { return await _fallback.IsAvailableAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report("SecretStore.FallbackAvailabilityFailed", "Availability", "OperationFailed", ex);
            return false;
        }
    }

    public async Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
    {
        var status = await SetWithStatusAsync(key, secret, ct).ConfigureAwait(false);
        if (status == SecretStorageStatus.Failed)
            throw new InvalidOperationException("The secret could not be stored in either secret store.");
    }

    public async Task<SecretStorageStatus> SetWithStatusAsync(SecretKey key, string secret, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(secret);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_sessionOnly && await IsPrimaryAvailableAsync("Write", ct).ConfigureAwait(false))
            {
                try
                {
                    await _primary.SetAsync(key, secret, ct).ConfigureAwait(false);
                    _sessionKeys.Remove(key);
                    _deletedKeys.Remove(key);
                    var status = _primary.GetStorageStatus(key);
                    _status[key] = status;
                    // A stale fallback must not reappear even if cleanup or a later primary read fails.
                    try { await _fallback.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { Report("SecretStore.FallbackCleanupFailed", "Write", "CleanupFailed", ex); }
                    return status;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Report("SecretStore.PrimaryWriteFailed", "Write", "OperationFailed", ex); }
            }

            try
            {
                await _fallback.SetAsync(key, secret, ct).ConfigureAwait(false);
                _sessionKeys.Add(key);
                _deletedKeys.Remove(key);
                _status[key] = SecretStorageStatus.SessionOnly;
                if (_sessionOnly) Report("SecretStore.SessionOnly", "Write", "ExplicitSessionOnly");
                return SecretStorageStatus.SessionOnly;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _status[key] = SecretStorageStatus.Failed;
                Report("SecretStore.FallbackWriteFailed", "Write", "OperationFailed", ex);
                return SecretStorageStatus.Failed;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_deletedKeys.Contains(key)) return null;
            // Never let an older persisted token override a newer session value when the keyring returns.
            if (_sessionOnly || _sessionKeys.Contains(key))
            {
                try
                {
                    var value = await _fallback.GetAsync(key, ct).ConfigureAwait(false);
                    _status[key] = value == null ? SecretStorageStatus.Unknown : SecretStorageStatus.SessionOnly;
                    return value;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _status[key] = SecretStorageStatus.Failed;
                    Report("SecretStore.FallbackReadFailed", "Read", "OperationFailed", ex);
                    throw new InvalidOperationException("The session secret could not be read.");
                }
            }

            if (await IsPrimaryAvailableAsync("Read", ct).ConfigureAwait(false))
            {
                try
                {
                    var value = await _primary.GetAsync(key, ct).ConfigureAwait(false);
                    _status[key] = value == null ? SecretStorageStatus.Unknown : _primary.GetStorageStatus(key);
                    return value;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Report("SecretStore.PrimaryReadFailed", "Read", "OperationFailed", ex); }
            }
            _status[key] = SecretStorageStatus.Failed;
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var primaryDeleted = _sessionOnly;
            if (!_sessionOnly && await IsPrimaryAvailableAsync("Delete", ct).ConfigureAwait(false))
            {
                try
                {
                    await _primary.DeleteAsync(key, ct).ConfigureAwait(false);
                    primaryDeleted = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Report("SecretStore.PrimaryDeleteFailed", "Delete", "OperationFailed", ex); }
            }

            // Failed persistent logout must not expose an old token again in THIS instance.
            _deletedKeys.Add(key);
            _sessionKeys.Remove(key);
            _status[key] = primaryDeleted ? SecretStorageStatus.Unknown : SecretStorageStatus.Failed;
            try { await _fallback.DeleteAsync(key, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _status[key] = SecretStorageStatus.Failed;
                Report("SecretStore.FallbackDeleteFailed", "Delete", "OperationFailed", ex);
                throw new InvalidOperationException("The secret could not be removed from the fallback secret store.");
            }
            if (!primaryDeleted)
                throw new InvalidOperationException("The secret could not be removed from the primary secret store.");
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> IsPrimaryAvailableAsync(string operation, CancellationToken ct)
    {
        try
        {
            var available = await _primary.IsAvailableAsync(ct).ConfigureAwait(false);
            if (!available && operation != "Availability")
                Report($"SecretStore.Primary{operation}Failed", operation, "Unavailable");
            return available;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Report($"SecretStore.Primary{operation}Failed", operation, "AvailabilityCheckFailed", ex);
            return false;
        }
    }

    private void Report(string code, string operation, string reason, Exception? exception = null)
    {
        try
        {
            var context = new Dictionary<string, string> { ["Operation"] = operation, ["Reason"] = reason };
            if (exception is SecretServiceException backendException && backendException.ExitCode.HasValue)
                context["ExitCode"] = backendException.ExitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // No SecretKey, value, exception payload/type, arguments or command output enter diagnostics.
            var severity = code == "SecretStore.SessionOnly" || code == "SecretStore.FallbackCleanupFailed"
                ? ErrorSeverity.Diagnostic : code == "SecretStore.PrimaryDeleteFailed" ||
                                            code.StartsWith("SecretStore.Fallback", StringComparison.Ordinal)
                    ? ErrorSeverity.Error : ErrorSeverity.Warning;
            _errors.Report(new ErrorReport(code, severity, nameof(CompositeSecretStore),
                code == "SecretStore.SessionOnly" ? "The secret was deliberately stored for this session only." :
                "A secret-store operation failed. Persistent storage was not confirmed; session fallback may still be available.",
                Context: context));
        }
        catch { /* Diagnostic output never changes credential state. */ }
    }
}
