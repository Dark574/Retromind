using System;
using System.Collections.Generic;

namespace Retromind.Models;

/// <summary>Classification chosen by the caller, independently of notification policy.</summary>
public enum ErrorSeverity
{
    Diagnostic,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Output-only diagnostic data. Codes are stable identifiers, messages may be localized.
/// Context must contain diagnostic metadata rather than credentials or full request payloads.
/// Supply known sensitive values when they could occur without an identifying key in free text.
/// </summary>
public sealed record ErrorReport(
    string Code,
    ErrorSeverity Severity,
    string Component,
    string Message,
    Exception? Exception = null,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, string>? Context = null,
    IReadOnlyCollection<string>? SensitiveValues = null);
