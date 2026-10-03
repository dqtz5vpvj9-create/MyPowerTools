using Microsoft.Data.Sqlite;

namespace MyPowerTools.Tests;

public sealed class InputMonitorSqliteVersionTests
{
    [Fact]
    public void Native_sqlite_contains_the_CVE_2025_6965_fix()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select sqlite_version()";
        var version = Version.Parse((string)command.ExecuteScalar()!);
        Assert.True(version >= new Version(3, 50, 2), $"Loaded vulnerable SQLite version {version}");
    }
}
