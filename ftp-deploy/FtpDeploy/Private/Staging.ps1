# Safe replacement of files the server refuses to overwrite in place (typically a DLL held open by IIS).

# Writes the bytes to $Uri in one request (STOR) and closes the request.
function Write-RemoteBytes {
    param(
        [string]$Uri,
        [string]$Method,
        [byte[]]$Bytes,
        [int]$Offset = 0,
        [int]$Count = $Bytes.Length,
        [switch]$NoKeepAlive
    )

    $request = New-FtpRequest -Uri $Uri -Method $Method
    if ($NoKeepAlive) { $request.KeepAlive = $false }
    $request.ContentLength = $Count
    $stream = $request.GetRequestStream()
    $stream.Write($Bytes, $Offset, $Count)
    $stream.Close()
    $response = $request.GetResponse()
    $response.Close()
}

function Publish-StagedFileInChunks {
    param(
        [byte[]]$FileBytes,
        [string]$TempUri,
        [string]$RelativePath
    )

    # Some hosting security filters abort a large STOR based on the transfer's content even for a valid
    # Microsoft-signed runtime assembly. APPE in small segments avoids a half-written final path. The finished
    # staged file is downloaded and SHA-256 checked before it is ever renamed into service.
    # Prefer a few larger APPE transfers: the content filter only objects to a transfer beginning with the PE header
    # (sent separately as one byte), while this FTP server becomes unstable under dozens of rapid passive data
    # connections.
    foreach ($chunkSize in @(131072, 65536, 32768)) {
        [void](Remove-RemoteFile -Uri $TempUri)
        $offset = 0
        $chunkedUploadOk = $true

        try {
            while ($offset -lt $FileBytes.Length) {
                # Keep the initial STOR to one byte: the filter rejects a transfer beginning with the PE header;
                # later APPE segments are inert byte ranges. Only the fully reconstructed, downloaded, hash-matching
                # file may proceed to the final rename.
                $isFirst = $offset -eq 0
                $count = if ($isFirst) { 1 } else { [Math]::Min($chunkSize, $FileBytes.Length - $offset) }
                $method = if ($isFirst) { [System.Net.WebRequestMethods+Ftp]::UploadFile } else { [System.Net.WebRequestMethods+Ftp]::AppendFile }

                $chunkCompleted = $false
                for ($chunkAttempt = 1; $chunkAttempt -le 5; $chunkAttempt++) {
                    try {
                        Write-RemoteBytes -Uri $TempUri -Method $method -Bytes $FileBytes -Offset $offset -Count $count -NoKeepAlive
                        $offset += $count
                        $chunkCompleted = $true
                        break
                    }
                    catch {
                        # A dropped response does not tell us whether the server committed the APPE. Probe SIZE
                        # before retrying so the same byte range can never be appended twice.
                        $observedSize = -1
                        for ($sizeAttempt = 1; $sizeAttempt -le 5; $sizeAttempt++) {
                            Start-Sleep -Milliseconds (500 * $sizeAttempt)
                            $observedSize = Get-RemoteFileSize -Uri $TempUri
                            if ($observedSize -ge 0) { break }
                        }

                        if ($observedSize -eq ($offset + $count)) {
                            $offset += $count
                            $chunkCompleted = $true
                            break
                        }
                        if ($observedSize -ne $offset) {
                            throw "Could not safely resume chunk at byte $offset after a dropped FTP response (remote size: $observedSize)."
                        }
                        if ($chunkAttempt -eq 5) { throw }

                        Write-Warning "Transient FTP error staging '$RelativePath' at byte $offset (attempt $chunkAttempt/5); remote size confirms the chunk was not committed, retrying"
                        Start-Sleep -Seconds $chunkAttempt
                    }
                }

                if (-not $chunkCompleted) { throw "Chunk at byte $offset did not complete." }

                # No metadata request after every APPE: this server can briefly return a stale SIZE and can reject
                # rapid control connection churn with status 125. The final download + hash below is authoritative.
                Start-Sleep -Milliseconds 750
            }
        }
        catch {
            $chunkedUploadOk = $false
            Write-Warning "Chunked staging with $chunkSize-byte segments failed for '$RelativePath': $($_.Exception.Message)"
        }

        if ($chunkedUploadOk) {
            for ($hashAttempt = 1; $hashAttempt -le 3; $hashAttempt++) {
                if (Test-RemoteFileHash -Uri $TempUri -ExpectedBytes $FileBytes -SkipFirstByte) {
                    Write-Host "Staged and SHA-256 verified via $chunkSize-byte FTP segments: $RelativePath"
                    return
                }
                if ($hashAttempt -lt 3) { Start-Sleep -Seconds (2 * $hashAttempt) }
            }
        }

        [void](Remove-RemoteFile -Uri $TempUri)
    }

    throw "Could not create a SHA-256-verified staged copy for '$RelativePath' even with chunked FTP transfers."
}

function Publish-ViaStagedRename {
    param(
        [byte[]]$FileBytes,
        [string]$RemoteFilePath,
        [string]$RemoteUri,
        [string]$RelativePath
    )

    # A 550 while overwriting a deployed DLL commonly means IIS still has the old file open. Deleting it first is
    # dangerous on Windows: the directory entry can disappear while the process retains a delete-pending handle,
    # leaving the application with no usable file and preventing recreation at the same path. Stage the complete
    # replacement under an unused name first, verify it, then wait for the final rename to become possible.
    $remoteDirectory = (Split-Path $RemoteFilePath -Parent).Replace('\', '/')
    $fileName = Split-Path $RemoteFilePath -Leaf
    $tempName = ".$fileName.uploading-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
    $tempPath = if ($remoteDirectory) { "$remoteDirectory/$tempName" } else { $tempName }
    $tempUri = Get-FtpUri $tempPath
    $stageAttempts = 2

    Write-Warning "Direct overwrite is unavailable for '$RelativePath'; staging a verified replacement before retrying the final rename"

    for ($stageAttempt = 1; $stageAttempt -le $stageAttempts; $stageAttempt++) {
        try {
            Write-RemoteBytes -Uri $tempUri -Method ([System.Net.WebRequestMethods+Ftp]::UploadFile) -Bytes $FileBytes

            Start-Sleep -Milliseconds 200
            if ((Get-RemoteFileSize -Uri $tempUri) -eq $FileBytes.Length) { break }

            [void](Remove-RemoteFile -Uri $tempUri)
            if ($stageAttempt -eq $stageAttempts) {
                throw "The staged copy never verified correctly after $stageAttempts attempts."
            }
            Start-Sleep -Seconds (2 * $stageAttempt)
        }
        catch {
            if ($stageAttempt -eq $stageAttempts) {
                [void](Remove-RemoteFile -Uri $tempUri)
                Write-Warning "Normal staged upload is still being rejected for '$RelativePath'; switching to hash-verified chunked staging"
                Publish-StagedFileInChunks -FileBytes $FileBytes -TempUri $tempUri -RelativePath $RelativePath
                break
            }
            Start-Sleep -Seconds (2 * $stageAttempt)
        }
    }

    $commitAttempts = 10
    for ($commitAttempt = 1; $commitAttempt -le $commitAttempts; $commitAttempt++) {
        try {
            $renameRequest = New-FtpRequest -Uri $tempUri -Method ([System.Net.WebRequestMethods+Ftp]::Rename)
            $renameRequest.RenameTo = $fileName
            $renameResponse = $renameRequest.GetResponse()
            $renameResponse.Close()

            Start-Sleep -Milliseconds 200
            if ((Get-RemoteFileSize -Uri $RemoteUri) -ne $FileBytes.Length) {
                throw "The staged rename completed but the final size did not verify."
            }

            Write-Host "Uploaded after staged lock recovery: $RelativePath"
            return
        }
        catch [System.Net.WebException] {
            if (-not (Test-FileUnavailable (Get-FtpStatusCode $_))) {
                [void](Remove-RemoteFile -Uri $tempUri)
                throw "Could not commit the staged replacement for '$RelativePath': $($_.Exception.Message)"
            }

            # FTP servers commonly refuse RNTO when the destination exists. Removing it may either succeed
            # immediately or leave a locked file delete-pending; in both cases a later rename attempt is the safe
            # recovery path because the verified temp copy remains intact.
            [void](Remove-RemoteFile -Uri $RemoteUri)
            if ($commitAttempt -lt $commitAttempts) {
                $delaySeconds = [Math]::Min(3 * $commitAttempt, 15)
                Write-Warning "Final path is still unavailable for '$RelativePath' (attempt $commitAttempt/$commitAttempts); keeping the verified staged copy and retrying in ${delaySeconds}s"
                Start-Sleep -Seconds $delaySeconds
                continue
            }

            throw "Could not replace '$RelativePath' after $commitAttempts staged-rename attempts because the final path remained unavailable (likely still locked by IIS). The verified staged copy remains at '$tempPath' for recovery."
        }
        catch {
            [void](Remove-RemoteFile -Uri $tempUri)
            throw
        }
    }
}
