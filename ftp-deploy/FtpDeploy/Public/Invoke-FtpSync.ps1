function Invoke-FtpSync {
    <#
    .SYNOPSIS
        Uploads a local folder to the FTP server and verifies every file. Throws on the first file that cannot be verified.
    .DESCRIPTION
        Parameters given here override the same-named entries of -Settings (from Get-FtpSettings).
        With a ManifestPath, files whose content this machine already verified on the server are skipped after one
        SIZE call. -PruneRemoteRootFiles removes stale files directly in the remote root (never folders).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [hashtable]$Settings,
        [Parameter(Mandatory)] [string]$LocalPath,
        [string]$RemotePath,
        [string[]]$ExcludePaths,
        [string]$ManifestPath,
        [switch]$PruneRemoteRootFiles,
        [switch]$DryRun
    )

    $ErrorActionPreference = 'Stop'

    $effective = $Settings.Clone()
    if ($PSBoundParameters.ContainsKey('RemotePath')) { $effective.RemotePath = $RemotePath }
    if ($PSBoundParameters.ContainsKey('ExcludePaths')) { $effective.ExcludePaths = $ExcludePaths }
    if ($PSBoundParameters.ContainsKey('ManifestPath')) { $effective.ManifestPath = $ManifestPath }
    if ($PruneRemoteRootFiles) { $effective.PruneRemoteRootFiles = $true }
    Assert-FtpConnection $effective

    if (-not (Test-Path -LiteralPath $LocalPath)) { throw "LocalPath not found: $LocalPath" }
    $localRoot = (Resolve-Path -LiteralPath $LocalPath).Path
    $files = @(Get-ChildItem -Path $localRoot -Recurse -File | Sort-Object FullName)
    if ($files.Count -eq 0) { throw "No files found in LocalPath: $localRoot" }

    $excluded = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $effective.ExcludePaths) {
        if (-not [string]::IsNullOrWhiteSpace($entry)) {
            [void]$excluded.Add((($entry.Trim().TrimStart('.') -replace '^[\\/]+', '').Replace('\', '/')))
        }
    }

    Start-FtpSession -Settings $effective -DryRun:$DryRun -LoadManifest

    Write-Host "Local Base Path: $localRoot"
    Write-Host "Uploading $($files.Count) files from '$localRoot' to 'ftp://$($effective.Server)/$($effective.RemotePath)'"
    if ($DryRun) { Write-Host "DryRun is enabled. No remote changes will be made." }
    if ($effective.PruneRemoteRootFiles) {
        Write-Host "Pruning stale files directly under the remote application root before upload..."
        Remove-StaleRemoteRootFiles -LocalRoot $localRoot
    }

    $uploaded = 0
    $skipped = 0
    foreach ($file in $files) {
        $relative = $file.FullName
        if ($relative.StartsWith($localRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relative = $relative.Substring($localRoot.Length).TrimStart('\').TrimStart('/')
        }
        $relative = $relative.Replace('\', '/')

        if (Test-Excluded -Excluded $excluded -RelativePath $relative) {
            $skipped++
            Write-Host "Excluded: $relative"
            continue
        }

        Send-FtpFile -SourceFile $file.FullName -RelativePath $relative
        $uploaded++
    }

    Write-Host "Upload complete. Uploaded/Checked: $uploaded, Excluded: $skipped"
    [pscustomobject]@{ Checked = $uploaded; Excluded = $skipped }
}
