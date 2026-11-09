using System.Reflection;
using Avalonia;
using Avalonia.Platform;

namespace Retromind.Tests.TestInfrastructure;

// A cursor has no window in these tests; keep host integration tests independent
// of X11/Wayland without adding a headless platform package.
public class ManagedCursorFactory : DispatchProxy
{
    internal static void EnsureRegistered()
    {
        // Platform registration is a private Avalonia 12 API. Keep this shim
        // local to host tests; it supplies no real cursor or windowing backend.
        var locator = typeof(AvaloniaLocator).GetProperty("CurrentMutable",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var binding = locator.GetType().GetMethod("Bind")!.MakeGenericMethod(typeof(ICursorFactory))
            .Invoke(locator, null)!;
        var factory = DispatchProxy.Create<ICursorFactory, ManagedCursorFactory>();
        var register = binding.GetType().GetMethod("ToConstant")!;
        if (register.IsGenericMethodDefinition) register = register.MakeGenericMethod(factory.GetType());
        register.Invoke(binding, [factory]);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => DispatchProxy.Create<ICursorImpl, ManagedCursor>();
    public class ManagedCursor : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
