# Removes files found directly in the remote root that are not present directly in the local root. Intentionally
# root-only: runtime files such as versioned mscordaccore DLLs otherwise accumulate across .NET updates and can exhaust
# small hosting quotas, while persistent subfolders (logs, data) stay untouched. Protected root files (app_offline.htm)
# are never removed.
function Remove-StaleRemoteRootFiles {
    param([string]$LocalRoot)

    $keep = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    Get-ChildItem -Path $LocalRoot -File | ForEach-Object { [void]$keep.Add($_.Name) }
    foreach ($name in $script:Session.ProtectedRootFiles) { [void]$keep.Add($name) }

    $remoteRoot = $script:Session.RemotePath.Trim('/')
    $rootPrefix = if ($remoteRoot) { Get-FtpUri "$remoteRoot/" } else { Get-FtpUri }

    foreach ($name in Get-RemoteEntryNames -Path $remoteRoot) {
        if ($name -eq '.' -or $name -eq '..' -or $keep.Contains($name)) { continue }

        $itemUri = "$rootPrefix$name"
        # SIZE fails for directories on this server: leave all directories (and so persistent logs/data) untouched.
        if ((Get-RemoteFileSize -Uri $itemUri) -lt 0) { continue }

        if ($script:Session.DryRun) {
            Write-Host "[DryRun] Prune stale remote root file: $name"
            continue
        }

        if (-not (Remove-RemoteFile -Uri $itemUri)) {
            throw "Failed to prune stale remote root file '$name'. Deployment stopped before uploading to avoid exhausting the hosting quota mid-run."
        }
        Write-Host "Pruned stale remote root file: $name" -ForegroundColor DarkYellow
    }
}
