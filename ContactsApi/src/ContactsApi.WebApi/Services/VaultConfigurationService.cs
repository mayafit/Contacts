/**
 * @fileoverview Background service to load secrets from Vault at startup
 * @module ContactsApi.WebApi.Services
 */

using ContactsApi.Infrastructure.Vault;
using ContactsApi.WebApi.Configuration;
using Microsoft.Extensions.Options;

namespace ContactsApi.WebApi.Services;

/// <summary>
/// Background service that loads secrets from Vault at application startup
/// </summary>
public class VaultConfigurationService : IHostedService
{
    private readonly IVaultClient _vaultClient;
    private readonly GoogleOAuthOptions _oauthOptions;
    private readonly ILogger<VaultConfigurationService> _logger;

    public VaultConfigurationService(
        IVaultClient vaultClient,
        IOptions<GoogleOAuthOptions> oauthOptions,
        ILogger<VaultConfigurationService> logger)
    {
        _vaultClient = vaultClient;
        _oauthOptions = oauthOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Loads secrets from Vault when the application starts
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Loading secrets from Vault...");

            // Load Google OAuth Client Secret from Vault
            var clientSecret = await _vaultClient.GetSecretAsync("google_client_secret");
            _oauthOptions.ClientSecret = clientSecret;

            _logger.LogInformation("Successfully loaded secrets from Vault");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load secrets from Vault. Application may not function correctly.");

            // In production, you might want to fail fast if secrets cannot be loaded
            // throw;

            // For development, we'll continue but log the error
            _logger.LogWarning("Continuing startup without Vault secrets. OAuth flow will not work.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
