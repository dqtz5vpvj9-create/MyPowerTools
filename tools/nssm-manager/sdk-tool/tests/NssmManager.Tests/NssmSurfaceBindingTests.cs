using System.Text.RegularExpressions;
using NssmManager.Contracts;
using NssmManager.Tool;

namespace NssmManager.Tests;

/// <summary>
/// Reflection bindings fail silently at runtime when the ViewModel member does not exist,
/// so the XAML is checked against the actual surface members instead of being trusted.
/// </summary>
public sealed class NssmSurfaceBindingTests
{
    // Bound inside the ListBox ItemTemplate, whose x:DataType is NssmServiceSnapshot.
    private static readonly string[] SnapshotTemplateBindings = ["DisplayName", "Name", "State", "Application"];

    [Fact]
    public void every_view_binding_resolves_to_a_view_model_member()
    {
        var bindings = ReadBindings();

        var unresolved = bindings
            .Where(name => !SnapshotTemplateBindings.Contains(name, StringComparer.Ordinal))
            .Where(name => typeof(NssmManagerViewModel).GetProperty(name) is null)
            .ToArray();
        Assert.Empty(unresolved);

        foreach (var name in SnapshotTemplateBindings)
        {
            Assert.NotNull(typeof(NssmServiceSnapshot).GetProperty(name));
        }
    }

    [Fact]
    public void high_risk_actions_are_gated_by_the_confirmation_surface()
    {
        var axaml = ReadView();
        Assert.Contains("Command=\"{Binding ConfirmPendingCommand}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanConfirmPending}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding ImpactConfirmed}\"", axaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasPendingConfirmation}\"", axaml, StringComparison.Ordinal);
        Assert.DoesNotContain("第一次点击时直接执行", axaml, StringComparison.Ordinal);
    }

    private static string[] ReadBindings() => Regex
        .Matches(ReadView(), @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)")
        .Select(match => match.Groups[1].Value)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string ReadView() => File.ReadAllText(Path.Combine(
        SdkToolRoot(), "src", "NssmManager.Tool", "NssmManagerView.axaml"));

    private static string SdkToolRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var manifest = Path.Combine(current.FullName, "tool.json");
            if (File.Exists(manifest))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the nssm-manager sdk-tool root.");
    }
}
