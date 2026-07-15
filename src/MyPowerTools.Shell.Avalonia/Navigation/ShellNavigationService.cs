namespace MyPowerTools.Shell.Avalonia.Navigation;

public sealed class ShellNavigationService
{
    private readonly Stack<ShellRoute> _backStack = new();

    public ShellRoute Current { get; private set; } = ShellRoute.Home;

    public bool CanGoBack => _backStack.Count > 0;

    public event Action<ShellRoute>? RouteChanged;

    public void Navigate(ShellRoute route, bool addToHistory = true)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route == Current)
        {
            return;
        }

        if (addToHistory)
        {
            _backStack.Push(Current);
        }

        Current = route;
        RouteChanged?.Invoke(route);
    }

    public bool TryGoBack()
    {
        if (_backStack.Count == 0)
        {
            return false;
        }

        Current = _backStack.Pop();
        RouteChanged?.Invoke(Current);
        return true;
    }

    public void Reset(ShellRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        _backStack.Clear();
        Current = route;
        RouteChanged?.Invoke(route);
    }
}
