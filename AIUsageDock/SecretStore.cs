using Windows.Security.Credentials;

namespace AIUsageDock;

internal interface ISecretStore
{
    string? Read(string name);

    /// <summary>Saves <paramref name="value"/>, or removes the secret when it is null.</summary>
    void Write(string name, string? value);
}

/// <summary>
/// Windows Credential Manager (per-user, encrypted at rest), so API keys entered in
/// the settings page never land in settings.json.
/// </summary>
internal sealed class CredentialSecretStore : ISecretStore
{
    private const string Resource = "AIUsageDock";

    public string? Read(string name)
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, name);
            credential.RetrievePassword();
            return string.IsNullOrWhiteSpace(credential.Password) ? null : credential.Password;
        }
        catch (Exception)
        {
            // Retrieve throws when no credential exists.
            return null;
        }
    }

    public void Write(string name, string? value)
    {
        var vault = new PasswordVault();
        try
        {
            vault.Remove(vault.Retrieve(Resource, name));
        }
        catch (Exception)
        {
            // Nothing saved yet.
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            vault.Add(new PasswordCredential(Resource, name, value));
        }
    }
}
