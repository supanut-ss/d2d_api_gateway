function Read-RemoteBytes {
    param([string]$Uri, [int]$Offset = 0)

    $request = New-FtpRequest -Uri $Uri -Method ([System.Net.WebRequestMethods+Ftp]::DownloadFile)
    if ($Offset -gt 0) { $request.ContentOffset = $Offset }
    $response = $request.GetResponse()
    $memory = New-Object System.IO.MemoryStream
    try {
        $response.GetResponseStream().CopyTo($memory)
        return , $memory.ToArray()
    }
    finally {
        $response.Close()
        $memory.Dispose()
    }
}

# Downloads the remote file and compares its SHA-256 with the expected bytes.
# -SkipFirstByte retrieves from byte 1 (the first byte is taken from the expected data): see Publish-StagedFileInChunks.
function Test-RemoteFileHash {
    param(
        [string]$Uri,
        [byte[]]$ExpectedBytes,
        [switch]$SkipFirstByte
    )

    $remoteBytes = $null
    $useOffsetDownload = $SkipFirstByte.IsPresent
    if (-not $useOffsetDownload) {
        try { $remoteBytes = Read-RemoteBytes -Uri $Uri }
        catch { $useOffsetDownload = $true }
    }

    if ($useOffsetDownload) {
        # The same host filter that rejects a full STOR can reset a RETR that begins with this PE header.
        # Retrieve from byte 1 instead; byte 0 was written and size-verified as its own one-byte STOR before any APPE.
        try {
            if ($ExpectedBytes.Length -lt 2 -or (Get-RemoteFileSize -Uri $Uri) -ne $ExpectedBytes.Length) { return $false }

            $remainder = Read-RemoteBytes -Uri $Uri -Offset 1
            if ($remainder.Length -ne ($ExpectedBytes.Length - 1)) { return $false }

            $remoteBytes = New-Object byte[] $ExpectedBytes.Length
            $remoteBytes[0] = $ExpectedBytes[0]
            [Array]::Copy($remainder, 0, $remoteBytes, 1, $remainder.Length)
        }
        catch {
            return $false
        }
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha256.ComputeHash($ExpectedBytes)) -eq [BitConverter]::ToString($sha256.ComputeHash($remoteBytes))
    }
    finally {
        $sha256.Dispose()
    }
}
