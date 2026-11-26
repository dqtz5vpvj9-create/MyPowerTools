using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>
/// One loaded copy of the two real sources the phone pages share: the tool catalog (with the single
/// favorites/recent store behind it) and the device snapshot. Pages refresh on navigation or on an
/// explicit user action — there is no timer and no polling.
/// </summary>
public sealed class MobileShellData
{
    private readonly MobileToolCatalogService _catalog;
    private readonly IMobileDeviceService _devices;
    private readonly SemaphoreSlim _libraryGate = new(1, 1);
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private MobileToolLibrary? _library;
    private MobileDeviceSnapshot? _snapshot;

    public MobileShellData(MobileToolCatalogService catalog, IMobileDeviceService devices)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
    }

    public MobileToolLibrary? Library => _library;
    public MobileDeviceSnapshot? Snapshot => _snapshot;
    public string LibraryError { get; private set; } = "";
    public string DeviceError { get; private set; } = "";

    /// <summary>Tools the user marked as favorites, read from the live cards.</summary>
    public IReadOnlyList<MobileToolEntry> Favorites => _library is null
        ? []
        : _library.Entries.Where(entry => entry.Card?.IsFavorite == true).ToArray();

    public IReadOnlyList<MobileToolEntry> RecentTools(int take) =>
        _library is null ? [] : _catalog.RecentEntries(_library, take);

    public async Task<MobileToolLibrary?> GetLibraryAsync(bool force, CancellationToken cancellationToken = default)
    {
        if (!force && _library is not null)
        {
            return _library;
        }

        await _libraryGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!force && _library is not null)
            {
                return _library;
            }

            LibraryError = "";
            _library = await _catalog.LoadAsync(cancellationToken).ConfigureAwait(true);
            return _library;
        }
        catch (Exception ex)
        {
            LibraryError = ex.Message;
            return _library;
        }
        finally
        {
            _libraryGate.Release();
        }
    }

    public async Task<MobileDeviceSnapshot?> GetSnapshotAsync(bool force, CancellationToken cancellationToken = default)
    {
        if (!force && _snapshot is not null)
        {
            return _snapshot;
        }

        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!force && _snapshot is not null)
            {
                return _snapshot;
            }

            DeviceError = "";
            _snapshot = await _devices.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
            return _snapshot;
        }
        catch (Exception ex)
        {
            DeviceError = ex.Message;
            return _snapshot;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    /// <summary>Device actions (pairing, removal, checks) change the snapshot; drop the cached copy.</summary>
    public void InvalidateSnapshot() => _snapshot = null;

    /// <summary>Favorites and recents are written through the cards; reload to see another writer's change.</summary>
    public void InvalidateLibrary() => _library = null;
}
