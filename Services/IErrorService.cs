using System;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services;

public interface IErrorService
{
    /// <summary>
    /// Best-effort structured logging followed by an optional UI-thread notification.
    /// No retry, recovery, or severity-dependent automatic dialogs. Output failures never escape.
    /// The caller owns notification suppression and all business decisions.
    /// </summary>
    void Report(ErrorReport error, Func<Task>? notifyUser = null);
}
