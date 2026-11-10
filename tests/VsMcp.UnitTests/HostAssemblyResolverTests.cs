using System;
using System.Reflection;
using VsMcp.Infrastructure;
using Xunit;

public sealed class HostAssemblyResolverTests
{
    [Fact]
    public void Resolves_a_JIT_bind_without_a_requesting_assembly()
    {
        Assert.Same(typeof(System.Text.Json.JsonElement).Assembly,
            HostAssemblyResolver.Resolve(null, new ResolveEventArgs(Identity("10.0.0.13").FullName)));
    }

    [Fact]
    public void Ignores_identifiable_requests_outside_the_extension_directory()
    {
        Assert.Null(HostAssemblyResolver.Resolve(null,
            new ResolveEventArgs(Identity("10.0.0.13").FullName, typeof(string).Assembly)));
        Assert.Null(HostAssemblyResolver.Resolve(null, new ResolveEventArgs(Identity("10.0.0.13").FullName,
            System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DynamicCaller"),
                System.Reflection.Emit.AssemblyBuilderAccess.Run))));
    }

    [Theory]
    [InlineData("10.0.0.3")]
    [InlineData("10.0.0.10")]
    public void Uses_the_host_policy_instead_of_a_fixed_patch_version(string version)
    {
        var requested = Identity("10.0.0.13");
        var host = HostAssemblyResolver.GetHostIdentity(requested, baseline =>
        {
            Assert.Equal(new Version(10, 0, 0, 0), new AssemblyName(baseline).Version);
            return Identity(version).FullName;
        });
        Assert.Equal(new Version(version), host.Version);
        Assert.Equal("System.Text.Json", host.Name);
    }

    [Theory]
    [InlineData("10.0.0.13", "10.0.0.13")]
    [InlineData("10.0.0.3", "10.0.0.10")]
    [InlineData("10.0.0.0", "10.0.0.0")]
    [InlineData("10.0.0.13", "11.0.0.0")]
    public void Does_not_retry_a_host_bind_or_cross_major_versions(string requested, string host)
    {
        Assert.Null(HostAssemblyResolver.GetHostIdentity(Identity(requested), _ => Identity(host).FullName));
    }

    [Theory]
    [InlineData("Other.Dependency", "10.0.0.13", "cc7b13ffcd2ddd51", "neutral")]
    [InlineData("System.Text.Json", "11.0.0.0", "cc7b13ffcd2ddd51", "neutral")]
    [InlineData("System.Text.Json", "10.0.0.13", "b03f5f7f11d50a3a", "neutral")]
    [InlineData("System.Text.Json", "10.0.0.13", "cc7b13ffcd2ddd51", "en-US")]
    public void Ignores_unrecognized_identities(string name, string version, string token, string culture)
    {
        Assert.Null(HostAssemblyResolver.GetHostIdentity(
            new AssemblyName($"{name}, Version={version}, Culture={culture}, PublicKeyToken={token}"),
            _ => throw new Exception("Must not consult host policy for unrelated identities.")));
    }

    private static AssemblyName Identity(string version) =>
        new AssemblyName($"System.Text.Json, Version={version}, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51");
}
