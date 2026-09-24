namespace AgyLogger.Cli.Services.Proxy;

using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// CLI command handler for managing the local Root Certificate Authority (CA).
/// </summary>
public static class CaCommandHandler
{
    public static Command CreateCommand()
    {
        var caCommand = new Command("ca", "Manage the local Root Certificate Authority (CA) for HTTPS MitM interception");

        caCommand.AddCommand(CreateStatusCommand());
        caCommand.AddCommand(CreateTrustCommand());
        caCommand.AddCommand(CreateUntrustCommand());
        caCommand.AddCommand(CreateExportCommand());

        return caCommand;
    }

    public static Command CreateStatusCommand()
    {
        var command = new Command("status", "Check the status of the local Root CA and OS trust store");

        var caDirOption = new Option<string?>(
            aliases: ["--ca-dir"],
            description: "Custom CA storage directory (default: ~/.agylogs/ca)");

        var storeOption = new Option<string?>(
            aliases: ["--store"],
            description: "Target certificate store name (default: Root)");

        command.AddOption(caDirOption);
        command.AddOption(storeOption);

        command.SetHandler(context =>
        {
            var caDir = context.ParseResult.GetValueForOption(caDirOption);
            var store = context.ParseResult.GetValueForOption(storeOption);
            var exitCode = ExecuteStatus(caDir, store);
            context.ExitCode = exitCode;
        });

        return command;
    }

    public static Command CreateTrustCommand()
    {
        var command = new Command("trust", "Install the local Root CA certificate into the CurrentUser trust store");

        var caDirOption = new Option<string?>(
            aliases: ["--ca-dir"],
            description: "Custom CA storage directory (default: ~/.agylogs/ca)");

        var storeOption = new Option<string?>(
            aliases: ["--store"],
            description: "Target certificate store name (default: Root)");

        command.AddOption(caDirOption);
        command.AddOption(storeOption);

        command.SetHandler(context =>
        {
            var caDir = context.ParseResult.GetValueForOption(caDirOption);
            var store = context.ParseResult.GetValueForOption(storeOption);
            var exitCode = ExecuteTrust(caDir, store);
            context.ExitCode = exitCode;
        });

        return command;
    }

    public static Command CreateUntrustCommand()
    {
        var command = new Command("untrust", "Remove the local Root CA certificate from the CurrentUser trust store");

        var caDirOption = new Option<string?>(
            aliases: ["--ca-dir"],
            description: "Custom CA storage directory (default: ~/.agylogs/ca)");

        var storeOption = new Option<string?>(
            aliases: ["--store"],
            description: "Target certificate store name (default: Root)");

        command.AddOption(caDirOption);
        command.AddOption(storeOption);

        command.SetHandler(context =>
        {
            var caDir = context.ParseResult.GetValueForOption(caDirOption);
            var store = context.ParseResult.GetValueForOption(storeOption);
            var exitCode = ExecuteUntrust(caDir, store);
            context.ExitCode = exitCode;
        });

        return command;
    }

    public static Command CreateExportCommand()
    {
        var command = new Command("export", "Export the public Root CA certificate in PEM format");

        var pathArg = new Argument<string?>(
            name: "path",
            getDefaultValue: () => null,
            description: "Destination file path for the exported PEM certificate (optional)");

        var outputOption = new Option<string?>(
            aliases: ["--output", "-o", "--out"],
            description: "Destination file path for the exported PEM certificate (default: ~/.agylogs/ca/ca.crt)");

        var caDirOption = new Option<string?>(
            aliases: ["--ca-dir"],
            description: "Custom CA storage directory (default: ~/.agylogs/ca)");

        command.AddArgument(pathArg);
        command.AddOption(outputOption);
        command.AddOption(caDirOption);

        command.SetHandler(context =>
        {
            var path = context.ParseResult.GetValueForArgument(pathArg);
            var optOut = context.ParseResult.GetValueForOption(outputOption);
            var caDir = context.ParseResult.GetValueForOption(caDirOption);
            var exitCode = ExecuteExport(path ?? optOut, caDir);
            context.ExitCode = exitCode;
        });

        return command;
    }

    public static int ExecuteStatus(
        string? caDir = null,
        string? storeName = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        var outWriter = output ?? Console.Out;
        var errWriter = error ?? Console.Error;

        var resolvedDir = caDir ?? CertificateAuthority.DefaultStorageDirectory;
        var pfxPath = Path.Combine(resolvedDir, "ca.pfx");
        var crtPath = Path.Combine(resolvedDir, "ca.crt");
        var targetStore = storeName ?? CertificateAuthority.DefaultStoreName;

        outWriter.WriteLine("AgyLogger Root CA Status");
        outWriter.WriteLine(new string('-', 24));

        using var ca = CertificateAuthority.TryLoad(
            storageDirectory: resolvedDir,
            storeName: targetStore,
            storeLocation: StoreLocation.CurrentUser);

        if (ca is null)
        {
            if (File.Exists(pfxPath))
            {
                errWriter.WriteLine($"Error reading Root CA from '{pfxPath}': Certificate file could not be loaded.");
                return 1;
            }

            outWriter.WriteLine("Status:          Not Initialized");
            outWriter.WriteLine($"Storage Dir:     {resolvedDir}");
            outWriter.WriteLine($"PFX File:        Not found ({pfxPath})");

            var storeHasCert = IsCertificateInStore(targetStore, CertificateAuthority.DefaultCaCommonName);
            outWriter.WriteLine($"Trust Store:     {(storeHasCert ? "Installed (orphaned)" : "Not Installed")} (CurrentUser\\{targetStore})");
            outWriter.WriteLine();
            outWriter.WriteLine("To generate and install the Root CA, run:");
            outWriter.WriteLine("  agy-logger ca trust");
            return 0;
        }

        var cert = ca.RootCertificate;
        var now = DateTimeOffset.UtcNow;
        var isExpired = now > cert.NotAfter;
        var isNotYetValid = now < cert.NotBefore;
        var validityLabel = isExpired ? "EXPIRED" : (isNotYetValid ? "NOT YET VALID" : "VALID");
        var isTrusted = ca.IsRootCertificateTrusted();
        var pemExists = File.Exists(crtPath);

        outWriter.WriteLine("Status:          Initialized");
        outWriter.WriteLine($"Subject:         {cert.Subject}");
        outWriter.WriteLine($"Thumbprint:      {cert.Thumbprint}");
        outWriter.WriteLine($"Valid From:      {cert.NotBefore.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({cert.NotBefore:u})");
        outWriter.WriteLine($"Valid Until:     {cert.NotAfter.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({cert.NotAfter:u}) [{validityLabel}]");
        outWriter.WriteLine($"Storage File:    {pfxPath}");
        outWriter.WriteLine($"PEM File:        {crtPath} {(pemExists ? "[Found]" : "[Missing - run 'agy-logger ca export']")}");
        outWriter.WriteLine($"Trust Store:     {(isTrusted ? "Trusted" : "Not Trusted")} (CurrentUser\\{targetStore})");

        if (!isTrusted)
        {
            outWriter.WriteLine();
            outWriter.WriteLine("To trust the Root CA in your user store, run:");
            outWriter.WriteLine("  agy-logger ca trust");
        }

        return 0;
    }

    public static int ExecuteTrust(
        string? caDir = null,
        string? storeName = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        var outWriter = output ?? Console.Out;
        var errWriter = error ?? Console.Error;
        var targetStore = storeName ?? CertificateAuthority.DefaultStoreName;

        try
        {
            using var ca = CertificateAuthority.GetOrCreateDefault(
                storageDirectory: caDir,
                storeName: targetStore,
                storeLocation: StoreLocation.CurrentUser);

            if (ca.IsRootCertificateTrusted())
            {
                outWriter.WriteLine("Root CA is already installed and trusted.");
                outWriter.WriteLine($"  Subject:    {ca.RootCertificate.Subject}");
                outWriter.WriteLine($"  Thumbprint: {ca.RootCertificate.Thumbprint}");
                outWriter.WriteLine($"  Store:      CurrentUser\\{targetStore}");
                return 0;
            }

            var success = ca.TrustRootCertificate();
            if (success)
            {
                outWriter.WriteLine("Successfully installed Root CA into trust store.");
                outWriter.WriteLine($"  Subject:    {ca.RootCertificate.Subject}");
                outWriter.WriteLine($"  Thumbprint: {ca.RootCertificate.Thumbprint}");
                outWriter.WriteLine($"  Store:      CurrentUser\\{targetStore}");
                outWriter.WriteLine();
                outWriter.WriteLine("For Node.js / Antigravity CLI, you can also set:");
                var crtPath = Path.Combine(caDir ?? CertificateAuthority.DefaultStorageDirectory, "ca.crt");
                outWriter.WriteLine($"  export NODE_EXTRA_CA_CERTS=\"{crtPath}\"");
                return 0;
            }
            else
            {
                errWriter.WriteLine("Failed to install Root CA into trust store.");
                errWriter.WriteLine($"  Target Store: CurrentUser\\{targetStore}");
                errWriter.WriteLine("  Ensure you have permission to modify the certificate store and accept any OS security prompts.");
                return 1;
            }
        }
        catch (Exception ex)
        {
            errWriter.WriteLine($"Error during trust installation: {ex.Message}");
            return 1;
        }
    }

    public static int ExecuteUntrust(
        string? caDir = null,
        string? storeName = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        var outWriter = output ?? Console.Out;
        var errWriter = error ?? Console.Error;
        var targetStore = storeName ?? CertificateAuthority.DefaultStoreName;

        try
        {
            using var ca = CertificateAuthority.TryLoad(
                storageDirectory: caDir,
                storeName: targetStore,
                storeLocation: StoreLocation.CurrentUser);

            if (ca is not null)
            {
                if (!ca.IsRootCertificateTrusted())
                {
                    outWriter.WriteLine("Root CA is not currently installed in the trust store.");
                    outWriter.WriteLine($"  Subject:    {ca.RootCertificate.Subject}");
                    outWriter.WriteLine($"  Thumbprint: {ca.RootCertificate.Thumbprint}");
                    outWriter.WriteLine($"  Store:      CurrentUser\\{targetStore}");
                    return 0;
                }

                var success = ca.UntrustRootCertificate();
                if (success)
                {
                    outWriter.WriteLine("Successfully removed Root CA from trust store.");
                    outWriter.WriteLine($"  Subject:    {ca.RootCertificate.Subject}");
                    outWriter.WriteLine($"  Thumbprint: {ca.RootCertificate.Thumbprint}");
                    outWriter.WriteLine($"  Store:      CurrentUser\\{targetStore}");
                    return 0;
                }
                else
                {
                    errWriter.WriteLine("Failed to remove Root CA from trust store.");
                    return 1;
                }
            }

            // If no local CA file exists on disk, check store for orphaned certificates matching common name
            using var store = new X509Store(targetStore, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(
                X509FindType.FindBySubjectName,
                CertificateAuthority.DefaultCaCommonName,
                validOnly: false);

            if (matches.Count == 0)
            {
                outWriter.WriteLine($"No Root CA certificates found in CurrentUser\\{targetStore}.");
                return 0;
            }

            var removed = 0;
            foreach (var cert in matches)
            {
                store.Remove(cert);
                outWriter.WriteLine($"Removed certificate: {cert.Subject} ({cert.Thumbprint})");
                cert.Dispose();
                removed++;
            }

            outWriter.WriteLine($"Successfully removed {removed} certificate(s) from CurrentUser\\{targetStore}.");
            return 0;
        }
        catch (Exception ex)
        {
            errWriter.WriteLine($"Error during untrust operation: {ex.Message}");
            return 1;
        }
    }

    public static int ExecuteExport(
        string? outputPath = null,
        string? caDir = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        var outWriter = output ?? Console.Out;
        var errWriter = error ?? Console.Error;

        var targetPath = outputPath ?? Path.Combine(
            caDir ?? CertificateAuthority.DefaultStorageDirectory,
            "ca.crt");

        try
        {
            using var ca = CertificateAuthority.GetOrCreateDefault(storageDirectory: caDir);
            ca.ExportRootCertificatePem(targetPath);

            outWriter.WriteLine("Root CA certificate exported successfully (PEM format).");
            outWriter.WriteLine($"  Destination: {Path.GetFullPath(targetPath)}");
            outWriter.WriteLine($"  Subject:     {ca.RootCertificate.Subject}");
            outWriter.WriteLine($"  Thumbprint:  {ca.RootCertificate.Thumbprint}");
            return 0;
        }
        catch (Exception ex)
        {
            errWriter.WriteLine($"Failed to export Root CA certificate to '{targetPath}': {ex.Message}");
            return 1;
        }
    }

    private static bool IsCertificateInStore(string storeName, string subjectName)
    {
        try
        {
            using var store = new X509Store(storeName, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            var matches = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, validOnly: false);
            var exists = matches.Count > 0;
            foreach (var c in matches)
            {
                c.Dispose();
            }
            return exists;
        }
        catch
        {
            return false;
        }
    }
}