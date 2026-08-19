using System.Diagnostics;
using System.Text;
using MatBu.Models;

namespace MatBu.Services;

public sealed class SmbClientService(ILogger<SmbClientService> logger)
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<string>> ListDirectoriesAsync(
        string location,
        string? relativePath,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var root = SmbPath.Parse(location);
        var normalized = NormalizeRelativePath(relativePath, allowEmpty: true);
        var directory = string.Join('/', new[] { root.Directory?.Replace('\\', '/').Trim('/'), normalized }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var browsed = root with { Directory = string.IsNullOrWhiteSpace(directory) ? null : directory };
        var result = await ExecuteAsync(browsed, credential, "ls", TestTimeout, cancellationToken);
        if (!result.Success) throw new IOException(DescribeFailure(browsed, "Ordnerauflistung", result.Details, credential is not null));
        var folders = new List<string>();
        foreach (var line in result.Details.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(?<name>.+?)\s+(?<attr>[A-Z]*D[A-Z]*)\s+\d+\s+(?:Mon|Tue|Wed|Thu|Fri|Sat|Sun)\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim();
            if (name is "." or ".." || string.IsNullOrWhiteSpace(name)) continue;
            folders.Add(name);
        }
        return folders.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<GatewayObjectTestResult> TestAsync(
        string location,
        ObjectDirection direction,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var smbPath = SmbPath.Parse(location);
            var listResult = await ExecuteAsync(smbPath, credential, "ls", TestTimeout, cancellationToken);
            if (!listResult.Success)
                return FailedTest(smbPath, "Lesetest", listResult, credential is not null, stopwatch.ElapsedMilliseconds);

            if (direction == ObjectDirection.Source)
                return new GatewayObjectTestResult(true, $"SMB-Verbindung erfolgreich. {smbPath.Summary} · Lesen möglich.", stopwatch.ElapsedMilliseconds);

            var localTestFile = Path.Combine(Path.GetTempPath(), $"matbu-smb-test-{Guid.NewGuid():N}.tmp");
            var remoteTestFile = $".matbu-test-{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(localTestFile, "MatBu SMB write test", cancellationToken);
                var uploadResult = await ExecuteAsync(
                    smbPath,
                    credential,
                    $"put {Quote(localTestFile)} {Quote(remoteTestFile)}",
                    TestTimeout,
                    cancellationToken);
                if (!uploadResult.Success)
                    return FailedTest(smbPath, "Schreibtest", uploadResult, credential is not null, stopwatch.ElapsedMilliseconds);

                var deleteResult = await ExecuteAsync(
                    smbPath,
                    credential,
                    $"del {Quote(remoteTestFile)}",
                    TestTimeout,
                    cancellationToken);
                if (!deleteResult.Success)
                    return FailedTest(smbPath, "Löschtest", deleteResult, credential is not null, stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                TryDelete(localTestFile);
            }

            var capabilities = direction == ObjectDirection.Both ? "Lesen, Schreiben und Löschen" : "Schreiben und Löschen";
            return new GatewayObjectTestResult(true, $"SMB-Verbindung erfolgreich. {smbPath.Summary} · {capabilities} möglich.", stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException ex)
        {
            return new GatewayObjectTestResult(false, ex.Message, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMB connection test failed for {Location}", location);
            return new GatewayObjectTestResult(false, $"SMB-Test konnte nicht ausgeführt werden: {ex.Message}", stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task CreateArchiveAsync(
        string location,
        (string Username, string Password)? credential,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await CreateArchiveAsync(location, credential, output, cancellationToken, null);
    }

    public async Task CreateArchiveAsync(
        string location,
        (string Username, string Password)? credential,
        Stream output,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? includedPaths = null)
    {
        var smbPath = SmbPath.Parse(location);
        var authFile = await CreateAuthenticationFileAsync(credential, cancellationToken);
        Process? process = null;
        try
        {
            var arguments = new List<string> { "-Tc", "-" };
            arguments.AddRange(SourceSelection.Normalize(includedPaths ?? []).Select(path => path.Replace('/', '\\')));
            process = CreateProcess(smbPath, authFile, arguments.ToArray());
            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TransferTimeout);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);

            await WaitForExitAsync(process, timeout.Token, cancellationToken, TransferTimeout);
            var error = (await errorTask).Trim();
            if (process.ExitCode != 0)
                throw new IOException(DescribeFailure(smbPath, "Archivierung", error, credential is not null));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Die SMB-Archivierung hat das Zeitlimit von {FormatTimeout(TransferTimeout)} überschritten.");
        }
        finally
        {
            StopProcess(process);
            TryDelete(authFile);
        }
    }

    public async Task UploadFileAsync(
        string location,
        string localFile,
        string remoteName,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(localFile))
            throw new FileNotFoundException("Die lokale SMB-Quelldatei wurde nicht gefunden.", localFile);
        if (string.IsNullOrWhiteSpace(remoteName) || remoteName.IndexOfAny(['/', '\\', '\r', '\n']) >= 0)
            throw new ArgumentException("Der SMB-Zieldateiname ist ungültig.", nameof(remoteName));

        var smbPath = SmbPath.Parse(location);
        var partialName = remoteName + ".partial";
        var upload = await ExecuteAsync(
            smbPath,
            credential,
            $"reput {Quote(localFile)} {Quote(partialName)}",
            TransferTimeout,
            cancellationToken);
        if (!upload.Success)
            throw new IOException(DescribeFailure(smbPath, "Upload", upload.Details, credential is not null));

        // Keep the previous complete file until the resumable .partial file is fully uploaded.
        // This closes the delete/rename crash window on SMB servers that cannot replace on rename.
        var replacementBackup = remoteName + ".matbu-previous";
        _ = await ExecuteAsync(smbPath, credential, $"del {Quote(replacementBackup)}", TestTimeout, cancellationToken);
        var movePrevious = await ExecuteAsync(
            smbPath,
            credential,
            $"rename {Quote(remoteName)} {Quote(replacementBackup)}",
            TestTimeout,
            cancellationToken);
        var hadPrevious = movePrevious.Success;
        if (!hadPrevious && !IsNotFound(movePrevious.Details))
            throw new IOException(DescribeFailure(smbPath, "Vorbereitung des atomaren Upload-Abschlusses", movePrevious.Details, credential is not null));

        var rename = await ExecuteAsync(smbPath, credential, $"rename {Quote(partialName)} {Quote(remoteName)}", TestTimeout, cancellationToken);
        if (!rename.Success)
        {
            if (hadPrevious)
                _ = await ExecuteAsync(smbPath, credential, $"rename {Quote(replacementBackup)} {Quote(remoteName)}", TestTimeout, CancellationToken.None);
            throw new IOException(DescribeFailure(smbPath, "Abschluss des Uploads", rename.Details, credential is not null));
        }
        if (hadPrevious)
            _ = await ExecuteAsync(smbPath, credential, $"del {Quote(replacementBackup)}", TestTimeout, cancellationToken);
    }

    public async Task<long> SyncPartialFileAsync(
        string location,
        string localFile,
        string remoteName,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(localFile))
            throw new FileNotFoundException("Local SMB upload source was not found.", localFile);
        if (string.IsNullOrWhiteSpace(remoteName) || remoteName.IndexOfAny(['/', '\\', '\r', '\n']) >= 0)
            throw new ArgumentException("Invalid SMB target file name.", nameof(remoteName));

        var smbPath = SmbPath.Parse(location);
        var partialName = remoteName + ".partial";
        var upload = await ExecuteAsync(
            smbPath,
            credential,
            $"reput {Quote(localFile)} {Quote(partialName)}",
            TransferTimeout,
            cancellationToken);
        if (!upload.Success)
            throw new IOException(DescribeFailure(smbPath, "Streaming upload", upload.Details, credential is not null));
        return new FileInfo(localFile).Length;
    }

    public async Task FinalizePartialFileAsync(
        string location,
        string remoteName,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remoteName) || remoteName.IndexOfAny(['/', '\\', '\r', '\n']) >= 0)
            throw new ArgumentException("Invalid SMB target file name.", nameof(remoteName));

        var smbPath = SmbPath.Parse(location);
        var partialName = remoteName + ".partial";
        var replacementBackup = remoteName + ".matbu-previous";
        _ = await ExecuteAsync(smbPath, credential, $"del {Quote(replacementBackup)}", TestTimeout, cancellationToken);
        var movePrevious = await ExecuteAsync(
            smbPath,
            credential,
            $"rename {Quote(remoteName)} {Quote(replacementBackup)}",
            TestTimeout,
            cancellationToken);
        var hadPrevious = movePrevious.Success;
        if (!hadPrevious && !IsNotFound(movePrevious.Details))
            throw new IOException(DescribeFailure(smbPath, "Streaming finalize preparation", movePrevious.Details, credential is not null));

        var rename = await ExecuteAsync(smbPath, credential, $"rename {Quote(partialName)} {Quote(remoteName)}", TestTimeout, cancellationToken);
        if (!rename.Success)
        {
            if (hadPrevious)
                _ = await ExecuteAsync(smbPath, credential, $"rename {Quote(replacementBackup)} {Quote(remoteName)}", TestTimeout, CancellationToken.None);
            throw new IOException(DescribeFailure(smbPath, "Streaming upload finalize", rename.Details, credential is not null));
        }
        if (hadPrevious)
            _ = await ExecuteAsync(smbPath, credential, $"del {Quote(replacementBackup)}", TestTimeout, cancellationToken);
    }

    public async Task DeleteUploadPartialAsync(
        string location,
        string remoteName,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var smbPath = SmbPath.Parse(location);
        var result = await ExecuteAsync(smbPath, credential, $"del {Quote(remoteName + ".partial")}", TestTimeout, cancellationToken);
        if (!result.Success && !IsNotFound(result.Details))
            throw new IOException(DescribeFailure(smbPath, "Streaming checkpoint cleanup", result.Details, credential is not null));
    }

    public async Task DownloadFileAsync(
        string location,
        string remoteName,
        string localFile,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(remoteName) || remoteName.IndexOfAny(['/', '\\', '\r', '\n']) >= 0)
            throw new ArgumentException("Der SMB-Quelldateiname ist ungültig.", nameof(remoteName));

        var directory = Path.GetDirectoryName(localFile);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var smbPath = SmbPath.Parse(location);
        var download = await ExecuteAsync(
            smbPath,
            credential,
            $"reget {Quote(remoteName)} {Quote(localFile)}",
            TransferTimeout,
            cancellationToken);
        if (!download.Success)
            throw new IOException(DescribeFailure(smbPath, "Download", download.Details, credential is not null));
    }

    public async Task EnsureDirectoryAsync(
        string location,
        string? relativeDirectory,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRelativePath(relativeDirectory, allowEmpty: true);
        if (string.IsNullOrEmpty(normalized)) return;
        var smbPath = SmbPath.Parse(location);
        var current = "";
        foreach (var segment in normalized.Split('/'))
        {
            current = string.IsNullOrEmpty(current) ? segment : $"{current}/{segment}";
            var result = await ExecuteAsync(smbPath, credential, $"mkdir {Quote(current)}", TestTimeout, cancellationToken);
            if (result.Success || IsAlreadyExists(result.Details)) continue;
            throw new IOException(DescribeFailure(smbPath, $"Anlegen des Ordners '{current}'", result.Details, credential is not null));
        }
    }

    public async Task<bool> RelativeFileExistsAsync(
        string location,
        string relativePath,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var (directory, fileName) = SplitRelativeFile(relativePath);
        var smbPath = SmbPath.Parse(AppendDirectory(location, directory));
        var result = await ExecuteAsync(smbPath, credential, $"ls {Quote(fileName)}", TestTimeout, cancellationToken);
        return result.Success;
    }

    public async Task UploadRelativeFileAsync(
        string location,
        string localFile,
        string relativePath,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken,
        bool skipIfExists = false)
    {
        var (directory, fileName) = SplitRelativeFile(relativePath);
        await EnsureDirectoryAsync(location, directory, credential, cancellationToken);
        var childLocation = AppendDirectory(location, directory);
        if (skipIfExists && await RelativeFileExistsAsync(location, relativePath, credential, cancellationToken)) return;
        await UploadFileAsync(childLocation, localFile, fileName, credential, cancellationToken);
    }

    public Task DownloadRelativeFileAsync(
        string location,
        string relativePath,
        string localFile,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var (directory, fileName) = SplitRelativeFile(relativePath);
        return DownloadFileAsync(AppendDirectory(location, directory), fileName, localFile, credential, cancellationToken);
    }

    public async Task DeleteRelativeFileAsync(
        string location,
        string relativePath,
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        var (directory, fileName) = SplitRelativeFile(relativePath);
        var smbPath = SmbPath.Parse(AppendDirectory(location, directory));
        var result = await ExecuteAsync(smbPath, credential, $"del {Quote(fileName)}", TestTimeout, cancellationToken);
        if (!result.Success && !IsNotFound(result.Details))
            throw new IOException(DescribeFailure(smbPath, $"Löschen der Datei '{relativePath}'", result.Details, credential is not null));
    }

    private async Task<SmbCommandResult> ExecuteAsync(
        SmbLocation smbPath,
        (string Username, string Password)? credential,
        string command,
        TimeSpan timeoutValue,
        CancellationToken cancellationToken)
    {
        var authFile = await CreateAuthenticationFileAsync(credential, cancellationToken);
        Process? process = null;
        try
        {
            process = CreateProcess(smbPath, authFile, "-c", command);
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutValue);
            await WaitForExitAsync(process, timeout.Token, cancellationToken, timeoutValue);
            var details = JoinDetails(await outputTask, await errorTask);
            return new SmbCommandResult(process.ExitCode == 0, process.ExitCode, details);
        }
        finally
        {
            StopProcess(process);
            TryDelete(authFile);
        }
    }

    private static Process CreateProcess(SmbLocation smbPath, string? authFile, params string[] commandArguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "smbclient",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("-d0");
        process.StartInfo.ArgumentList.Add(smbPath.Share);
        if (authFile is null)
        {
            process.StartInfo.ArgumentList.Add("-N");
        }
        else
        {
            process.StartInfo.ArgumentList.Add("-A");
            process.StartInfo.ArgumentList.Add(authFile);
        }

        if (!string.IsNullOrWhiteSpace(smbPath.Directory))
        {
            process.StartInfo.ArgumentList.Add("-D");
            process.StartInfo.ArgumentList.Add(smbPath.Directory);
        }

        foreach (var argument in commandArguments)
            process.StartInfo.ArgumentList.Add(argument);
        return process;
    }

    private static async Task<string?> CreateAuthenticationFileAsync(
        (string Username, string Password)? credential,
        CancellationToken cancellationToken)
    {
        if (credential is null)
            return null;
        if (credential.Value.Username.IndexOfAny(['\r', '\n']) >= 0 || credential.Value.Password.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("SMB-Benutzername und Passwort dürfen keine Zeilenumbrüche enthalten.");

        var (username, domain) = SplitUsername(credential.Value.Username);
        var builder = new StringBuilder()
            .Append("username = ").AppendLine(username)
            .Append("password = ").AppendLine(credential.Value.Password);
        if (!string.IsNullOrWhiteSpace(domain))
            builder.Append("domain = ").AppendLine(domain);

        var authFile = Path.Combine(Path.GetTempPath(), $"matbu-smb-{Guid.NewGuid():N}.auth");
        await File.WriteAllTextAsync(authFile, builder.ToString(), cancellationToken);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(authFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return authFile;
    }

    private static (string Username, string? Domain) SplitUsername(string value)
    {
        var username = value.Trim();
        var separator = username.IndexOf('\\');
        if (separator <= 0 || separator == username.Length - 1)
            return (username, null);
        return (username[(separator + 1)..], username[..separator]);
    }

    private static (string Directory, string FileName) SplitRelativeFile(string value)
    {
        var normalized = NormalizeRelativePath(value, allowEmpty: false);
        var separator = normalized.LastIndexOf('/');
        return separator < 0
            ? ("", normalized)
            : (normalized[..separator], normalized[(separator + 1)..]);
    }

    private static string NormalizeRelativePath(string? value, bool allowEmpty)
    {
        var normalized = (value ?? "").Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(normalized))
        {
            if (allowEmpty) return "";
            throw new ArgumentException("Der relative SMB-Pfad darf nicht leer sein.", nameof(value));
        }
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.IndexOfAny(['\r', '\n', '\0']) >= 0))
            throw new ArgumentException("Der relative SMB-Pfad enthält ein ungültiges Segment.", nameof(value));
        return string.Join('/', segments);
    }

    private static string AppendDirectory(string root, string? relativeDirectory)
    {
        var normalized = NormalizeRelativePath(relativeDirectory, allowEmpty: true);
        return string.IsNullOrEmpty(normalized)
            ? root
            : root.TrimEnd('\\', '/') + "\\" + normalized.Replace('/', '\\');
    }

    private static bool IsAlreadyExists(string value) =>
        value.Contains("OBJECT_NAME_COLLISION", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("existiert bereits", StringComparison.OrdinalIgnoreCase);

    private static bool IsNotFound(string value) =>
        value.Contains("OBJECT_NAME_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("NO_SUCH_FILE", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("NT_STATUS_NO_SUCH_FILE", StringComparison.OrdinalIgnoreCase);

    private static async Task WaitForExitAsync(Process process, CancellationToken timeoutToken, CancellationToken callerToken, TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync(timeoutToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Der SMB-Vorgang hat das Zeitlimit von {FormatTimeout(timeout)} überschritten.");
        }
    }

    private static GatewayObjectTestResult FailedTest(
        SmbLocation smbPath,
        string operation,
        SmbCommandResult result,
        bool hasCredential,
        long durationMs) => new(false, DescribeFailure(smbPath, operation, result.Details, hasCredential), durationMs);

    private static string DescribeFailure(SmbLocation smbPath, string operation, string details, bool hasCredential)
    {
        var normalized = details.ToUpperInvariant();
        var hint = normalized switch
        {
            var value when value.Contains("NT_STATUS_LOGON_FAILURE") || value.Contains("NT_STATUS_WRONG_PASSWORD")
                => "Benutzername, Passwort oder Domain ist falsch.",
            var value when value.Contains("NT_STATUS_ACCOUNT_DISABLED")
                => "Das SMB-Benutzerkonto ist deaktiviert.",
            var value when value.Contains("NT_STATUS_PASSWORD_EXPIRED")
                => "Das SMB-Passwort ist abgelaufen.",
            var value when value.Contains("NT_STATUS_ACCESS_DENIED") && !hasCredential
                => "Die Freigabe erlaubt keinen Gastzugriff. Hinterlege einen SMB-Benutzernamen und ein Passwort.",
            var value when value.Contains("NT_STATUS_ACCESS_DENIED")
                => "Die Anmeldung war möglich, aber dem Benutzer fehlen Rechte auf Freigabe oder Unterordner.",
            var value when value.Contains("NT_STATUS_BAD_NETWORK_NAME")
                => $"Die Freigabe '{smbPath.ShareName}' existiert auf dem Server nicht. Der Teil direkt hinter dem Servernamen muss der Freigabename sein.",
            var value when value.Contains("NT_STATUS_OBJECT_PATH_NOT_FOUND") || value.Contains("NT_STATUS_OBJECT_NAME_NOT_FOUND") || value.Contains("NT_STATUS_NO_SUCH_FILE")
                => "Der angegebene Unterordner wurde nicht gefunden.",
            var value when value.Contains("CONNECTION REFUSED") || value.Contains("NT_STATUS_NETWORK_UNREACHABLE") || value.Contains("NT_STATUS_HOST_UNREACHABLE")
                => "Der SMB-Server ist aus dieser MatBu-Instanz nicht erreichbar. Prüfe Netzwerk, DNS und TCP-Port 445.",
            var value when value.Contains("NT_STATUS_IO_TIMEOUT") || value.Contains("TIMED OUT")
                => "Die SMB-Verbindung hat nicht rechtzeitig geantwortet.",
            _ => "Der SMB-Server hat den Vorgang abgelehnt."
        };

        var technical = Compact(details);
        return string.IsNullOrWhiteSpace(technical)
            ? $"SMB-{operation} fehlgeschlagen. {smbPath.Summary}. {hint}"
            : $"SMB-{operation} fehlgeschlagen. {smbPath.Summary}. {hint} Technisch: {technical}";
    }

    private static string JoinDetails(string output, string error) =>
        string.Join(' ', new[] { output.Trim(), error.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string Compact(string value)
    {
        var compact = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= 500 ? compact : compact[..500] + "…";
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
    private static string FormatTimeout(TimeSpan timeout) => timeout.TotalMinutes >= 1 ? $"{timeout.TotalMinutes:0} Minuten" : $"{timeout.TotalSeconds:0} Sekunden";

    private static void StopProcess(Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
        process.Dispose();
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record SmbCommandResult(bool Success, int ExitCode, string Details);
}
