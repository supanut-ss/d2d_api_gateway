@{
    # --- Connection (required). Copy this file to ftp-deploy.settings.psd1 (git-ignored) and fill it in. ---
    # Use single quotes: a password with $ or ` would otherwise be altered.
    Server     = 'ftp.example.com'      # host or host:port, no ftp:// needed
    Username   = 'CHANGE_ME'
    Password   = 'CHANGE_ME'

    # Folder on the server to upload into, relative to the FTP login root. '' = the root itself.
    RemotePath = ''

    # --- Optional ---
    # Remember which files were already verified on the server and skip them next time (path relative to this file).
    # ManifestPath = '.cache\manifest.json'

    # Files/folders (relative to the uploaded folder) that are never uploaded.
    # ExcludePaths = @('logs', 'appsettings.Production.json')

    # Delete stale files directly in the remote root before uploading (folders are never touched).
    # PruneRemoteRootFiles = $true

    # Extensions that are always re-uploaded without a size check.
    # AlwaysUploadExtensions = @('.html', '.htm', '.dll', '.exe', '.json')

    # Files never overwritten in place: staged, SHA-256 verified, then renamed (a DLL that IIS keeps open).
    # VerifiedStagingFiles = @('Microsoft.AspNetCore.Server.IIS.dll')

    # Root files that pruning must keep.
    # ProtectedRootFiles = @('app_offline.htm')
}
