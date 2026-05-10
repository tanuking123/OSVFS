using System.CommandLine;
using System.Runtime.Versioning;
using System.Text;
using OSVFS.ObjectStore;

namespace OSVFS.Credentials;

/// <summary>
/// Builds the <c>credentials</c> sub-command tree backing the <see cref="WindowsCredentialStore"/>.
/// Encapsulates the System.CommandLine wiring so <c>Program.cs</c> only deals with composition.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CredentialsCommandFactory
{
    /// <summary>
    /// Constructs the <c>credentials</c> sub-command and its <c>set/get/list/remove</c> children.
    /// IAM Identity Center (SSO) and `aws login` flows are handled directly by the AWS CLI;
    /// OSVFS picks up the resulting profiles via the SDK shared-profile chain.
    /// </summary>
    public static Command Build(IAwsCredentialStore store)
    {
        var credentials = new Command(
            "credentials",
            "Manage AWS credentials encrypted with DPAPI and stored in Windows Credential Manager. " +
            "For IAM Identity Center / SSO use 'aws configure sso' and reference the profile " +
            "from osvfs.toml; OSVFS resolves it through the SDK shared-profile chain.");

        credentials.Subcommands.Add(BuildSetCommand(store));
        credentials.Subcommands.Add(BuildGetCommand(store));
        credentials.Subcommands.Add(BuildRemoveCommand(store));
        credentials.Subcommands.Add(BuildListCommand(store));
        return credentials;
    }

    /// <summary>
    /// Builds <c>credentials set --profile &lt;name&gt;</c>. Missing access/secret keys are
    /// read interactively (the secret via masked input).
    /// </summary>
    private static Command BuildSetCommand(IAwsCredentialStore store)
    {
        var profile = new Option<string>("--profile")
        {
            Description = "Profile name to associate with the stored credential.",
            Required = true,
        };
        var accessKey = new Option<string?>("--access-key")
        {
            Description = "AWS access key ID. When omitted, the value is read from stdin.",
        };
        var secretKey = new Option<string?>("--secret-key")
        {
            Description = "AWS secret access key. When omitted, the value is read from stdin (masked).",
        };
        var sessionToken = new Option<string?>("--session-token")
        {
            Description = "Optional STS session token for temporary credentials.",
        };

        var command = new Command("set", "Save (or replace) AWS credentials for a profile.")
        {
            profile,
            accessKey,
            secretKey,
            sessionToken,
        };
        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profile)!;
            var ak = parseResult.GetValue(accessKey);
            var sk = parseResult.GetValue(secretKey);
            var st = parseResult.GetValue(sessionToken);

            if (string.IsNullOrEmpty(ak))
            {
                Console.Write("AWS Access Key ID: ");
                ak = Console.ReadLine();
            }
            if (string.IsNullOrEmpty(sk))
            {
                sk = ReadMasked("AWS Secret Access Key: ");
            }
            if (string.IsNullOrWhiteSpace(ak) || string.IsNullOrWhiteSpace(sk))
            {
                Console.Error.WriteLine("Access key and secret are both required.");
                return 1;
            }

            store.Save(profileName, new AwsCredential
            {
                AccessKeyId = ak.Trim(),
                SecretAccessKey = sk,
                SessionToken = string.IsNullOrEmpty(st) ? null : st,
            });
            Console.WriteLine($"Saved profile '{profileName}'.");
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Builds <c>credentials get --profile &lt;name&gt;</c>. Prints the access key id and
    /// session-token presence flag without ever revealing the secret.
    /// </summary>
    private static Command BuildGetCommand(IAwsCredentialStore store)
    {
        var profile = new Option<string>("--profile")
        {
            Description = "Profile name to inspect.",
            Required = true,
        };
        var command = new Command("get", "Print metadata for a stored profile (the secret is never echoed).")
        {
            profile,
        };
        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profile)!;
            var credential = store.Load(profileName);
            if (credential is null)
            {
                Console.Error.WriteLine($"No credential found for profile '{profileName}'.");
                return 1;
            }

            Console.WriteLine($"Profile:          {profileName}");
            Console.WriteLine($"AccessKeyId:      {credential.AccessKeyId}");
            Console.WriteLine($"SecretAccessKey:  (hidden, {credential.SecretAccessKey.Length} chars)");
            Console.WriteLine(
                $"SessionToken:     {(string.IsNullOrEmpty(credential.SessionToken) ? "(none)" : "(present)")}");
            Console.WriteLine(
                $"ExpiresAt:        {(credential.ExpiresAt is { } e ? e.ToString("u") : "(none)")}");
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Builds <c>credentials remove --profile &lt;name&gt;</c>.
    /// </summary>
    private static Command BuildRemoveCommand(IAwsCredentialStore store)
    {
        var profile = new Option<string>("--profile")
        {
            Description = "Profile name to delete.",
            Required = true,
        };
        var command = new Command("remove", "Delete the stored credential for a profile.")
        {
            profile,
        };
        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profile)!;
            if (store.Delete(profileName))
            {
                Console.WriteLine($"Removed profile '{profileName}'.");
                return 0;
            }
            Console.Error.WriteLine($"No credential found for profile '{profileName}'.");
            return 1;
        });
        return command;
    }

    /// <summary>
    /// Builds <c>credentials list</c>, printing every profile owned by OSVFS.
    /// </summary>
    private static Command BuildListCommand(IAwsCredentialStore store)
    {
        var command = new Command("list", "List every profile stored by OSVFS.");
        command.SetAction(_ =>
        {
            var profiles = store.List();
            if (profiles.Count == 0)
            {
                Console.WriteLine("(no profiles stored)");
                return 0;
            }
            foreach (var profile in profiles)
            {
                Console.WriteLine(profile);
            }
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Reads a line from stdin without echoing the characters. Backspace is honored so the
    /// user can correct typos; the prompt is written before the loop and a newline after.
    /// </summary>
    private static string ReadMasked(string prompt)
    {
        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();
                case ConsoleKey.Backspace:
                    if (buffer.Length > 0) buffer.Length--;
                    break;
                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                    }
                    break;
            }
        }
    }
}
