using System.IO;

namespace GangioKeet.Client.Configuration;

public static class ApiCredential
{
    public static string? Resolve(string? environmentVariable, Func<string, string?> readEnvironment)
    {
        if (string.IsNullOrWhiteSpace(environmentVariable))
        {
            return null;
        }

        string? value = readEnvironment(environmentVariable);
        if (string.IsNullOrEmpty(value))
        {
            throw new ConfigException(
                $"Api key environment variable '{environmentVariable}' is not set or is empty.");
        }

        return value;
    }
}
