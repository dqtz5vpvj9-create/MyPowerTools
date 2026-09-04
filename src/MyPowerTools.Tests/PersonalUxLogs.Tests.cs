using System.Collections.Specialized;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Tests;

public sealed class PersonalUxLogsTests
{
    [Fact]
    public void Refresh_keeps_query_level_wrapping_collection_and_unchanged_row_identity()
    {
        var retained = new LogLineViewModel("10:00", "Error", "network timeout");
        var current = new LogsViewModel("first", [], [retained, new("10:01", "Info", "ready")])
        { SearchText = "network", LevelFilter = "Error", WrapLines = false };
        var collection = current.Lines;
        var resets = 0;
        collection.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
        var next = new LogsViewModel("second", [],
            [new("10:00", "Error", "network timeout"), new("10:02", "Error", "network recovered"), new("10:03", "Info", "network ready")]);
        current.RefreshFrom(next);
        Assert.Equal("network", current.SearchText);
        Assert.Equal("Error", current.LevelFilter);
        Assert.False(current.WrapLines);
        Assert.Same(collection, current.Lines);
        Assert.Same(retained, current.Lines[0]);
        Assert.Equal(2, current.Lines.Count);
        Assert.Equal("second", current.Subtitle);
        Assert.Equal(0, resets);
        Assert.DoesNotContain("network ready", current.CopyText);
    }

    [Fact]
    public void Failed_refresh_keeps_data_and_successful_refresh_clears_error()
    {
        var vm = new LogsViewModel("module", [], [new("10:00", "Info", "existing")]);
        vm.ReportRefreshFailure("offline");
        Assert.True(vm.HasError);
        Assert.Single(vm.Lines);
        vm.RefreshFrom(new LogsViewModel("module", [], [new("10:01", "Info", "new")]));
        Assert.False(vm.HasError);
        Assert.Equal("new", Assert.Single(vm.Lines).Message);
    }
}
