# Single FTP operations: requests, status codes, directories, size, delete, listing.

function New-FtpRequest {
    param(
        [string]$Uri,
        [string]$Method
    )

    $request = [System.Net.FtpWebRequest]::Create($Uri)
    $request.Method = $Method
    $request.Credentials = New-Object System.Net.NetworkCredential($script:Session.Username, $script:Session.Password)
    $request.UsePassive = $true
    $request.UseBinary = $true
    # KeepAlive lets .NET reuse the control connection across sequential requests instead of a full TCP handshake +
    # login per file/directory-check/verification call, which was the dominant cost of a ~340 file deploy. The retry
    # logic already tolerates a dropped connection, so a host that handles reuse badly degrades to retries rather
    # than silent failure.
    $request.KeepAlive = $true
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 120000
    return $request
}

function Invoke-FtpCommand {
    param([string]$Uri, [string]$Method)

    $request = New-FtpRequest -Uri $Uri -Method $Method
    $response = $request.GetResponse()
    $response.Close()
}

# FTP status code of a failed request (null when the failure had no server response, e.g. a dropped connection).
function Get-FtpStatusCode {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)

    $response = $ErrorRecord.Exception.Response
    if (-not $response) { return $null }
    $statusCode = [int]$response.StatusCode
    $response.Close()
    return $statusCode
}

function Test-FileUnavailable {
    param($StatusCode)

    $null -ne $StatusCode -and $StatusCode -eq [int][System.Net.FtpStatusCode]::ActionNotTakenFileUnavailable
}

function Test-RemoteDirectoryExists {
    param([string]$DirectoryPath)

    if ([string]::IsNullOrWhiteSpace($DirectoryPath)) { return $true }

    try {
        Invoke-FtpCommand -Uri (Get-FtpUri "$($DirectoryPath.Trim('/'))/") -Method ([System.Net.WebRequestMethods+Ftp]::ListDirectory)
        return $true
    }
    catch {
        return $false
    }
}

function Ensure-RemoteDirectory {
    param([string]$DirectoryPath)

    if ([string]::IsNullOrWhiteSpace($DirectoryPath)) { return }

    # Almost every file in a deploy shares the same 1-2 directories: once one is confirmed this run, never re-verify
    # it. This host does not tolerate hundreds of rapid sequential FTP connections, so the churn was itself a cause
    # of transient failures.
    if ($script:EnsuredDirs.Contains($DirectoryPath)) { return }

    $current = ""
    foreach ($part in $DirectoryPath.Trim('/').Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $current = if ($current) { "$current/$part" } else { $part }

        if ($script:EnsuredDirs.Contains($current)) { continue }
        if ($script:Session.DryRun) { continue }

        $attempts = 3
        for ($attempt = 1; $attempt -le $attempts; $attempt++) {
            try {
                Invoke-FtpCommand -Uri (Get-FtpUri $current) -Method ([System.Net.WebRequestMethods+Ftp]::MakeDirectory)
                Write-Host "Created Directory: $current" -ForegroundColor Gray
                [void]$script:EnsuredDirs.Add($current)
                break
            }
            catch [System.Net.WebException] {
                # 550 means "already exists" on most servers; otherwise ask the server whether it is there.
                if ((Test-FileUnavailable (Get-FtpStatusCode $_)) -or (Test-RemoteDirectoryExists -DirectoryPath $current)) {
                    [void]$script:EnsuredDirs.Add($current)
                    break
                }
                if ($attempt -lt $attempts) {
                    Write-Warning "Transient error ensuring directory '$current' (attempt $attempt/$attempts): $($_.Exception.Message) - retrying"
                    Start-Sleep -Milliseconds (1500 * $attempt)
                    continue
                }
                throw "Failed to create remote directory '$current': $($_.Exception.Message)"
            }
        }
    }
    [void]$script:EnsuredDirs.Add($DirectoryPath)
}

# Remote size in bytes, or -1 when the file does not exist (or the server refuses SIZE).
function Get-RemoteFileSize {
    param([string]$Uri)

    try {
        $request = New-FtpRequest -Uri $Uri -Method ([System.Net.WebRequestMethods+Ftp]::GetFileSize)
        $response = $request.GetResponse()
        $size = $response.ContentLength
        $response.Close()
        return $size
    }
    catch {
        return -1
    }
}

function Remove-RemoteFile {
    param([string]$Uri)

    try {
        Invoke-FtpCommand -Uri $Uri -Method ([System.Net.WebRequestMethods+Ftp]::DeleteFile)
        return $true
    }
    catch {
        return $false
    }
}

# Names (files and directories) directly inside a remote folder; $Path = '' is the login root.
function Get-RemoteEntryNames {
    param([string]$Path = '')

    $trimmed = $Path.Trim('/')
    $uri = if ($trimmed) { Get-FtpUri "$trimmed/" } else { Get-FtpUri }

    $request = New-FtpRequest -Uri $uri -Method ([System.Net.WebRequestMethods+Ftp]::ListDirectory)
    $response = $request.GetResponse()
    $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
    $lines = $reader.ReadToEnd() -split "`r?`n"
    $reader.Close()
    $response.Close()

    foreach ($line in $lines) {
        $normalized = $line.Trim().Replace('\', '/').TrimEnd('/')
        if ($normalized) { ($normalized -split '/')[-1] }
    }
}
