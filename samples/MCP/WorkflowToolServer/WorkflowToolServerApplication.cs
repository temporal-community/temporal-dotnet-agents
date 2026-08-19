using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.AspNetCore;
using Temporalio.Client;

namespace TemporalCommunity.Samples.Mcp.WorkflowToolServer;

/// <summary>Registers and maps the ordinary authenticated MCP server used by this sample.</summary>
public static class WorkflowToolServerApplication
{
    public static IServiceCollection AddWorkflowToolServer(
        this IServiceCollection services,
        ITemporalClient temporalClient)
    {
        services.AddSingleton(temporalClient);
        services.AddSingleton<IWorkflowOperationLedger, InMemoryWorkflowOperationLedger>();
        services.AddSingleton<WorkflowOperationService>();
        services
            .AddAuthentication(SampleBearerAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SampleBearerAuthenticationHandler>(
                SampleBearerAuthenticationHandler.SchemeName,
                _ => { });
        services.AddAuthorization(options => options.AddPolicy(
            WorkflowToolServerConstants.StartPolicy,
            policy => policy.RequireAuthenticatedUser().RequireClaim("scope", "workflow:start")));
        services
            .AddMcpServer()
            .WithHttpTransport()
            .AddAuthorizationFilters()
            .WithTools<WorkflowOperationTools>();
        return services;
    }

    public static IEndpointConventionBuilder MapWorkflowToolServer(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app.MapMcp("/mcp").RequireAuthorization();
    }
}
