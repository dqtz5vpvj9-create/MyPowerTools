using MyPowerTools.Platform.Android;

namespace MyPowerTools.Platform.Android.Tests;

public sealed class AndroidExternalStoragePreferencesTests
{
    [Theory]
    [InlineData("content://com.android.externalstorage.documents/tree/primary%3ADownload%2FMPT", "内部存储 / Download / MPT")]
    [InlineData("content://com.android.externalstorage.documents/tree/primary%3ADocuments", "内部存储 / Documents")]
    [InlineData("content://com.android.externalstorage.documents/tree/ABCD-1234%3AExports%2FMPT", "外部存储 (ABCD-1234) / Exports / MPT")]
    [InlineData("content://com.android.providers.downloads.documents/tree/downloads", "系统下载目录 / Download")]
    [InlineData("", AndroidExternalStorageConfig.DefaultMediaStoreDisplayPath)]
    public void Formats_saf_tree_uri_into_human_readable_path(string uri, string expected)
    {
        Assert.Equal(expected, AndroidExternalStoragePreferences.FormatTreeUriDisplayPath(uri));
    }

    [Fact]
    public void Saves_and_loads_preferences_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mpt-ext-prefs-{Guid.NewGuid():N}.json");
        try
        {
            var initial = AndroidExternalStoragePreferences.Load(path);
            Assert.False(initial.SetupCompleted);
            Assert.Equal(AndroidExternalStorageConfig.ModeUnset, initial.Mode);

            var saved = new AndroidExternalStorageConfig(
                AndroidExternalStorageConfig.ModeSaf,
                "content://com.android.externalstorage.documents/tree/primary%3ADownload%2FMPT",
                "内部存储 / Download / MPT",
                SetupCompleted: true,
                UpdatedAtUtc: DateTimeOffset.UtcNow);
            AndroidExternalStoragePreferences.Save(saved, path);

            var reloaded = AndroidExternalStoragePreferences.Load(path);
            Assert.True(reloaded.SetupCompleted);
            Assert.Equal(AndroidExternalStorageConfig.ModeSaf, reloaded.Mode);
            Assert.Equal(saved.TreeUri, reloaded.TreeUri);
            Assert.Equal("内部存储 / Download / MPT", reloaded.DisplayPath);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
