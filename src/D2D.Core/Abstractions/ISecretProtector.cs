namespace D2D.Core.Abstractions;

/// <summary>Encrypts secrets (connection strings, channel secrets, tokens) before they are stored.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
