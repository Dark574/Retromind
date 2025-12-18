using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Retromind.Helpers;

/// <summary>
/// Zentraler Helper für UI-Thread-Marshalling (Avalonia).
/// Hinweis: Nicht in Models verwenden – nur in ViewModels / UI-nahen Services.
/// </summary>
public static class UiThreadHelper
{
    public static bool CheckAccess()
        => Dispatcher.UIThread.CheckAccess();

    public static void Post(Action action)
        => Post(action, DispatcherPriority.Normal);

    public static void Post(Action action, DispatcherPriority priority)
    {
        if (action is null) return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, priority);
    }

    public static Task InvokeAsync(Action action)
        => InvokeAsync(action, DispatcherPriority.Normal);

    public static async Task InvokeAsync(Action action, DispatcherPriority priority)
    {
        if (action is null) return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        // DispatcherOperation ist awaitbar (GetAwaiter), aber nicht zwingend "Task"-basiert.
        await Dispatcher.UIThread.InvokeAsync(action, priority);
    }

    public static Task InvokeAsync(Func<Task> func)
        => InvokeAsync(func, DispatcherPriority.Normal);

    public static async Task InvokeAsync(Func<Task> func, DispatcherPriority priority)
    {
        if (func is null) throw new ArgumentNullException(nameof(func));

        if (Dispatcher.UIThread.CheckAccess())
        {
            await func().ConfigureAwait(false);
            return;
        }

        // Execute the async delegate on the UI thread, then await its completion.
        await Dispatcher.UIThread.InvokeAsync(func, priority);
    }
    
    public static Task<T> InvokeAsync<T>(Func<T> func)
        => InvokeAsync(func, DispatcherPriority.Normal);

    public static async Task<T> InvokeAsync<T>(Func<T> func, DispatcherPriority priority)
    {
        if (func is null) throw new ArgumentNullException(nameof(func));

        if (Dispatcher.UIThread.CheckAccess())
            return func();

        return await Dispatcher.UIThread.InvokeAsync(func, priority);
    }
}