using System.Runtime.InteropServices;
using DbDelta.Core.Abstractions;
using DbDelta.Persistence.Credentials;
using FluentAssertions;
using Xunit;

namespace DbDelta.Persistence.IntegrationTests.Credentials;

public class DpapiCredentialStoreTests
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Fact]
    public async Task Set_then_Get_returns_the_stored_secret()
    {
        if (!IsWindows)
        {
            return; // Skip on non-Windows hosts
        }

#pragma warning disable CA1416
        ICredentialStore store = new DpapiCredentialStore();
        string key = $"dbdelta:test:{System.Guid.NewGuid():N}";
        try
        {
            await store.SetSecretAsync(key, "Hello123!", System.Threading.CancellationToken.None);
            string? back = await store.GetSecretAsync(key, System.Threading.CancellationToken.None);
            back.Should().Be("Hello123!");
        }
        finally
        {
            await store.DeleteSecretAsync(key, System.Threading.CancellationToken.None);
        }
#pragma warning restore CA1416
    }

    [Fact]
    public async Task Get_unknown_key_returns_null()
    {
        if (!IsWindows)
        {
            return; // Skip on non-Windows hosts
        }

#pragma warning disable CA1416
        ICredentialStore store = new DpapiCredentialStore();
        string? back = await store.GetSecretAsync($"dbdelta:test:nokey-{System.Guid.NewGuid():N}", System.Threading.CancellationToken.None);
        back.Should().BeNull();
#pragma warning restore CA1416
    }

    [Fact]
    public async Task Delete_is_noop_when_key_absent()
    {
        if (!IsWindows)
        {
            return; // Skip on non-Windows hosts
        }

#pragma warning disable CA1416
        ICredentialStore store = new DpapiCredentialStore();
        System.Func<System.Threading.Tasks.Task> act = () => store.DeleteSecretAsync($"dbdelta:test:noop-{System.Guid.NewGuid():N}", System.Threading.CancellationToken.None);
        await act.Should().NotThrowAsync();
#pragma warning restore CA1416
    }
}
