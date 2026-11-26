using System.Reflection;
using System.Xml.Linq;
using MyPowerTools.Android.Files;

namespace AndroidFileOpen.Tests;

/// <summary>
/// Parses the real provider paths resource and checks it against the code that declares the same
/// boundary. The resource is what AndroidX FileProvider enforces at runtime, so a mismatch here
/// would either hand out a URI the provider refuses or open a directory the launcher never intended.
/// </summary>
public sealed class SharedFilesResourceTests
{
    private static readonly string ResourcePath = ResolveResourcePath();
    private static readonly XDocument Resource = Load();

    [Fact]
    public void TheResourceFileNameMatchesTheContract()
    {
        Assert.Equal(MptSharedFileContract.PathsResourceFileName, Path.GetFileName(ResourcePath));
        Assert.Equal(MptSharedFileContract.PathsResourceReference, "@xml/" + Path.GetFileNameWithoutExtension(ResourcePath));
    }

    [Fact]
    public void TheRootElementIsThePathsDocumentFileProviderExpects()
    {
        Assert.NotNull(Resource.Root);
        Assert.Equal("paths", Resource.Root!.Name.LocalName);
        Assert.Equal("", Resource.Root.Name.NamespaceName);
    }

    [Fact]
    public void EveryDeclaredRootMatchesTheSpec()
    {
        var declared = DeclaredRoots();
        var expected = SharedFileRootSpec.All
            .Select(root => (root.XmlElementName, Normalize(root.RelativePath), root.Name))
            .ToArray();

        Assert.Equal(expected.Length, declared.Count);
        foreach (var (element, path, name) in expected)
        {
            Assert.Contains((element, path, name), declared);
        }
    }

    [Fact]
    public void NoExternallyVisibleRootIsDeclared()
    {
        string[] forbidden = ["root-path", "external-path", "external-files-path", "external-cache-path", "external-media-path"];

        foreach (var element in Resource.Root!.Elements())
        {
            Assert.DoesNotContain(element.Name.LocalName, forbidden);
            Assert.Contains(element.Name.LocalName, new[] { "files-path", "cache-path" });
        }
    }

    [Fact]
    public void EveryDeclaredPathIsARelativeDirectoryInsideItsBaseDirectory()
    {
        foreach (var element in Resource.Root!.Elements())
        {
            var name = element.Attribute("name");
            var path = element.Attribute("path");

            // FileProvider reads the attribute with a null namespace, so android:path would be
            // silently ignored and the root would fall back to the whole files directory.
            Assert.NotNull(name);
            Assert.NotNull(path);
            Assert.Equal("", name!.Name.NamespaceName);
            Assert.Equal("", path!.Name.NamespaceName);

            var value = path.Value;
            Assert.False(string.IsNullOrWhiteSpace(value));
            Assert.False(Path.IsPathRooted(value));
            Assert.NotEqual(".", value);
            Assert.NotEqual("/", value);
            Assert.DoesNotContain("..", value, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", value, StringComparison.Ordinal);
            Assert.DoesNotContain("//", value, StringComparison.Ordinal);
            Assert.EndsWith("/", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RootNamesAreUniqueBecauseFileProviderKeysRootsByName()
    {
        var names = Resource.Root!.Elements().Select(element => element.Attribute("name")!.Value).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void NoDeclaredRootCoversSettingsLogsOrTheAssistantStateFile()
    {
        string[] sensitive =
        [
            "MyPowerTools/settings",
            "MyPowerTools/logs",
            "MyPowerTools/packages",
            "MyPowerTools/state/modules/file-transfer/data/assistant",
        ];

        foreach (var (_, path, _) in DeclaredRoots())
        {
            foreach (var location in sensitive)
            {
                var covers = string.Equals(path, location, StringComparison.Ordinal) ||
                             location.StartsWith(path + "/", StringComparison.Ordinal);
                Assert.False(covers, $"{path} would expose {location}");
            }
        }
    }

    [Fact]
    public void TheAuthorityAndMetadataNamesMatchTheAndroidXContract()
    {
        Assert.Equal("android.support.FILE_PROVIDER_PATHS", MptSharedFileContract.PathsMetadataName);
        Assert.Equal(".mptfiles", MptSharedFileContract.ProviderAuthoritySuffix);
        Assert.Equal(
            MptSharedFileContract.ProviderAuthorityTemplate.Replace("${applicationId}", "com.example.app", StringComparison.Ordinal),
            MptSharedFileContract.AuthorityFor("com.example.app"));
        Assert.Equal("com.mypowertools.android.mptfiles", MptSharedFileContract.AuthorityFor("com.mypowertools.android"));
        Assert.Throws<ArgumentException>(() => MptSharedFileContract.AuthorityFor(" "));
    }

    private static List<(string Element, string Path, string Name)> DeclaredRoots() =>
        Resource.Root!
            .Elements()
            .Select(element => (
                element.Name.LocalName,
                Normalize(element.Attribute("path")?.Value ?? ""),
                element.Attribute("name")?.Value ?? ""))
            .ToList();

    private static string Normalize(string path) => path.Trim().TrimEnd('/');

    private static XDocument Load() => XDocument.Load(ResourcePath);

    private static string ResolveResourcePath()
    {
        var metadata = typeof(SharedFilesResourceTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "AndroidSharedFilesResource")
            ?.Value;
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            return Path.GetFullPath(metadata);
        }

        // Fallback: the project copies the same file next to the test assembly.
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", MptSharedFileContract.PathsResourceFileName);
    }
}
