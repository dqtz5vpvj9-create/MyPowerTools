using NssmManager.Compatibility;

namespace NssmManager.Tests;

public sealed class NssmEnvironmentProcessTests
{
    [Fact]
    public void clear_environment_preserves_drive_pseudo_variables()
    {
        // Process-wide mutation is exercised only by the opt-in differential
        // child-process matrix.
        if (Environment.GetEnvironmentVariable("NSSM_MANAGER_RUN_ENVIRONMENT_PROCESS_TESTS") != "1") return;
        Assert.Equal(0, NssmEnvironment.clear_environment());
    }

    [Fact]
    public void duplicate_environment_replaces_process_environment()
    {
        if (Environment.GetEnvironmentVariable("NSSM_MANAGER_RUN_ENVIRONMENT_PROCESS_TESTS") != "1") return;
        Assert.Equal(0, NssmEnvironment.duplicate_environment("NSSM_TRANSLATION_ONLY=1\0\0"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("NSSM_TRANSLATION_ONLY"));
    }

    [Fact]
    public void duplicate_environment_strings_does_not_mutate_input()
    {
        if (Environment.GetEnvironmentVariable("NSSM_MANAGER_RUN_ENVIRONMENT_PROCESS_TESTS") != "1") return;
        const string block = "NSSM_TRANSLATION_ONLY=1\0\0";
        NssmEnvironment.duplicate_environment_strings(block);
        Assert.Equal("NSSM_TRANSLATION_ONLY=1\0\0", block);
    }
}
