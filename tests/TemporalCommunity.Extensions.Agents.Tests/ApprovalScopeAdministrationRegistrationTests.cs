using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TemporalCommunity.Extensions.Agents.Tests;

public sealed class ApprovalScopeAdministrationRegistrationTests
{
    [Fact]
    public void NormalAgentRegistration_DoesNotRegisterAdministrativeCapability()
    {
        var services = new ServiceCollection();

        services.AddTemporalAgentProxies(
            options => options.AddAgentProxy("support"),
            taskQueue: "support");

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(ITemporalAgentApprovalScopeAdministration));
    }

    [Fact]
    public void ExplicitRegistration_AddsAdministrativeCapabilityOnce()
    {
        var services = new ServiceCollection();

        services.AddTemporalAgentApprovalScopeAdministration();
        services.AddTemporalAgentApprovalScopeAdministration();

        var descriptor = Assert.Single(
            services,
            item => item.ServiceType == typeof(ITemporalAgentApprovalScopeAdministration));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
    }
}
