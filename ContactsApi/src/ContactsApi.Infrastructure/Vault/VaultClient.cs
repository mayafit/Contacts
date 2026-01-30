/**
 * @fileoverview Vault client for retrieving secrets from HashiCorp Vault
 * @module ContactsApi.Infrastructure.Vault
 */

using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ContactsApi.Infrastructure.Vault;

/// <summary>
/// Client for interacting with HashiCorp Vault to retrieve secrets
/// </summary>
public class VaultClient : IVaultClient
{
    private readonly HttpClient _httpClient;
    private readonly string _vaultAddress;
    private readonly string _vaultToken;
    private readonly string _secretPath;

    public VaultClient(IConfiguration configuration)
    {
        _vaultAddress = configuration["Vault:Address"]
            ?? throw new InvalidOperationException("Vault:Address is not configured");
        _vaultToken = configuration["Vault:Token"]
            ?? throw new InvalidOperationException("Vault:Token is not configured");
        _secretPath = configuration["Vault:SecretPath"]
            ?? throw new InvalidOperationException("Vault:SecretPath is not configured");

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_vaultAddress)
        };
        _httpClient.DefaultRequestHeaders.Add("X-Vault-Token", _vaultToken);
    }

    /// <summary>
    /// Retrieves a secret value from Vault
    /// </summary>
    /// <param name="key">The key of the secret to retrieve</param>
    /// <returns>The secret value</returns>
    /// <exception cref="InvalidOperationException">Thrown when secret cannot be retrieved</exception>
    public async Task<string> GetSecretAsync(string key)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/v1/{_secretPath}");

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Failed to retrieve secret from Vault. Status: {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var vaultResponse = JsonSerializer.Deserialize<VaultSecretResponse>(content, options);

            if (vaultResponse?.Data?.Data == null)
            {
                throw new InvalidOperationException("Invalid response from Vault");
            }

            if (!vaultResponse.Data.Data.TryGetValue(key, out var secretElement))
            {
                throw new InvalidOperationException($"Secret key '{key}' not found in Vault");
            }

            return secretElement.GetString()
                ?? throw new InvalidOperationException($"Secret key '{key}' has null value");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Failed to connect to Vault at {_vaultAddress}", ex);
        }
    }

    /// <summary>
    /// Vault API response structure for KV v2 secrets
    /// </summary>
    private class VaultSecretResponse
    {
        public VaultData? Data { get; set; }
    }

    private class VaultData
    {
        public Dictionary<string, JsonElement>? Data { get; set; }
    }
}

/// <summary>
/// Interface for Vault client operations
/// </summary>
public interface IVaultClient
{
    /// <summary>
    /// Retrieves a secret value from Vault
    /// </summary>
    Task<string> GetSecretAsync(string key);
}
