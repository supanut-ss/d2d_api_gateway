# Uploads one file and does not return until it is verified on the server (or throws).

function ConvertTo-RemoteFilePath {
    param([string]$RelativePath)

    $path = "$($script:Session.RemotePath)/$RelativePath".Replace('\', '/')
    while ($path.Contains('//')) { $path = $path.Replace('//', '/') }
    return $path.TrimStart('/')
}

# Decides from the local manifest whether the server still has this exact content.
# Returns 'unchanged' (skip), 'divergent' (server differs: upload, no need to ask again) or 'unknown'.
function Get-ManifestVerdict {
    param([string]$SourceFile, [string]$RelativePath, [string]$LocalHash, [string]$RemoteUri)

    if ($script:Manifest[$RelativePath] -ne $LocalHash) { return 'unknown' }

    # A local hash match is a CANDIDATE for skipping, never the final word: it only proves our own source has not
    # changed since we last verified this exact content landed. It says nothing about another machine, another
    # deploy or manual server-side editing since. Confirm against the server's current size (a cheap metadata call,
    # not a content transfer): this is what makes the manifest safe when more than one machine deploys to a host.
    try {
        $request = New-FtpRequest -Uri $RemoteUri -Method ([System.Net.WebRequestMethods+Ftp]::GetFileSize)
        $response = $request.GetResponse()
        $remoteSize = $response.ContentLength
        $response.Close()

        if ($remoteSize -eq (Get-Item $SourceFile).Length) { return 'unchanged' }
        Write-Warning "Manifest says '$RelativePath' is unchanged, but the server's copy is a different size (another deploy touched it?) - re-uploading instead of trusting the local record"
    }
    catch {
        Write-Warning "Manifest says '$RelativePath' is unchanged, but could not confirm against the server ($($_.Exception.Message)) - re-uploading instead of trusting the local record"
    }
    return 'divergent'
}

function Send-FtpFile {
    param(
        [string]$SourceFile,
        [string]$RelativePath
    )

    $remoteFilePath = ConvertTo-RemoteFilePath $RelativePath
    $remoteDirectory = (Split-Path $remoteFilePath -Parent).Replace('\', '/')
    $remoteUri = Get-FtpUri $remoteFilePath
    $useManifest = [bool]$script:Session.ManifestPath

    $requiresVerifiedStaging = Test-ListContains $script:Session.VerifiedStagingFiles $RelativePath
    $verifiedStagingBytes = if ($requiresVerifiedStaging) { [System.IO.File]::ReadAllBytes($SourceFile) } else { $null }

    $remoteConfirmedDivergent = $false
    $localHash = $null

    if ($useManifest) {
        $localHash = (Get-FileHash -Path $SourceFile -Algorithm SHA256).Hash

        switch (Get-ManifestVerdict -SourceFile $SourceFile -RelativePath $RelativePath -LocalHash $localHash -RemoteUri $remoteUri) {
            'unchanged' {
                Write-Host "Skipped (verified unchanged on server): $RelativePath" -ForegroundColor DarkGray
                return
            }
            'divergent' { $remoteConfirmedDivergent = $true }
            default {
                # A machine may have no local manifest yet although this exact file was already verified on the
                # shared server by another machine. Repair the manifest from a full remote hash comparison and
                # avoid an upload that would trigger the host's content filter.
                if ($requiresVerifiedStaging -and (Test-RemoteFileHash -Uri $remoteUri -ExpectedBytes $verifiedStagingBytes -SkipFirstByte)) {
                    Register-VerifiedUpload -RelativePath $RelativePath -Hash $localHash
                    Write-Host "Skipped (remote SHA-256 match; manifest repaired): $RelativePath" -ForegroundColor DarkGray
                    return
                }
            }
        }
    }

    # Skip the pre-upload size check for extensions that force an upload regardless (for a self-contained .NET
    # publish that is ~340 of 343 files, i.e. ~340 wasted SIZE round-trips per full deploy), and when the manifest
    # check already decided this file must be re-uploaded.
    $shouldAlwaysUpload = Test-HasExtension $script:Session.AlwaysUploadExtensions $RelativePath
    if (-not $shouldAlwaysUpload -and -not $remoteConfirmedDivergent) {
        $remoteSize = Get-RemoteFileSize -Uri $remoteUri
        if ($remoteSize -ge 0 -and $remoteSize -eq (Get-Item $SourceFile).Length) {
            Write-Host "Skipped (Size Match): $RelativePath" -ForegroundColor Gray
            return
        }
    }

    Ensure-RemoteDirectory -DirectoryPath $remoteDirectory

    if ($script:Session.DryRun) {
        Write-Host "[DryRun] Upload: $RelativePath"
        return
    }

    if (-not (Test-Path $SourceFile)) { return }

    $fileBytes = if ($requiresVerifiedStaging) { $verifiedStagingBytes } else { [System.IO.File]::ReadAllBytes($SourceFile) }

    # Never issue STOR directly against this final path. A rejected transfer on this host can remove the previously
    # good DLL before returning 550.
    if ($requiresVerifiedStaging) {
        Publish-ViaStagedRename -FileBytes $fileBytes -RemoteFilePath $remoteFilePath -RemoteUri $remoteUri -RelativePath $RelativePath
        Register-VerifiedUpload -RelativePath $RelativePath -Hash $localHash
        return
    }

    $maxAttempts = 5
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            Write-RemoteBytes -Uri $remoteUri -Method ([System.Net.WebRequestMethods+Ftp]::UploadFile) -Bytes $fileBytes

            # This server has shown a "ghost success" pattern: STOR returns a clean success response but the file is
            # later missing or truncated (seen twice - a core .exe and a managed .dll both vanished after being
            # reported "Uploaded" with no error). Never trust the FTP response alone: verify the byte count landed.
            Start-Sleep -Milliseconds 150
            if ((Get-RemoteFileSize -Uri $remoteUri) -eq $fileBytes.Length) {
                Write-Host "Uploaded: $RelativePath"
                Register-VerifiedUpload -RelativePath $RelativePath -Hash $localHash
                return
            }

            if ($attempt -lt $maxAttempts) {
                Write-Warning "Post-upload verification failed for '$RelativePath' (server accepted the write but the file is missing/wrong size afterward) - retrying"
                Start-Sleep -Seconds (2 * $attempt)
                continue
            }
            throw "Upload failed for '$RelativePath' -> '$remoteUri': server accepted the write $maxAttempts times but the file never verified correctly afterward (ghost-success pattern). This file is NOT reliably on the server - do not treat this run as successful."
        }
        catch [System.Net.WebException] {
            # A direct overwrite can fail with 550 while IIS still holds a DLL. Never delete-and-immediately-recreate
            # the final path: on Windows that can leave it delete-pending and make the outage worse. Upload and
            # verify a sibling temp file, then retry the final rename safely.
            if (Test-FileUnavailable (Get-FtpStatusCode $_)) {
                Publish-ViaStagedRename -FileBytes $fileBytes -RemoteFilePath $remoteFilePath -RemoteUri $remoteUri -RelativePath $RelativePath
                Register-VerifiedUpload -RelativePath $RelativePath -Hash $localHash
                return
            }

            if (-not (Wait-BeforeRetry -Attempt $attempt -MaxAttempts $maxAttempts -RelativePath $RelativePath -Reason $_.Exception.Message)) {
                throw "Upload failed for '$RelativePath' -> '$remoteUri' after $maxAttempts attempts: $($_.Exception.Message)"
            }
        }
        catch {
            if (-not (Wait-BeforeRetry -Attempt $attempt -MaxAttempts $maxAttempts -RelativePath $RelativePath -Reason $_.Exception.Message)) {
                throw "Upload failed for '$RelativePath' -> '$remoteUri' after $maxAttempts attempts: $($_.Exception.Message)"
            }
        }
    }
}

# Transient connection drop/timeout (no FTP status code, e.g. "connection closed" / "operation timed out"): back off
# and retry instead of failing the whole run, since this link drops FTP data connections occasionally under sustained
# sequential transfers. Returns $false when the attempts are used up.
function Wait-BeforeRetry {
    param([int]$Attempt, [int]$MaxAttempts, [string]$RelativePath, [string]$Reason)

    if ($Attempt -ge $MaxAttempts) { return $false }

    $delaySeconds = 1.5 * $Attempt
    Write-Warning "Transient upload error for '$RelativePath' (attempt $Attempt/$MaxAttempts): $Reason - retrying in ${delaySeconds}s"
    Start-Sleep -Milliseconds ($delaySeconds * 1000)
    return $true
}
