# ftp-deploy

Verified FTP uploads for shared hosts (Plesk / IIS) that drop connections, lock DLLs or report uploads that never landed.
Self-contained: copy this whole folder into another project, fill in one settings file, run.
Needs only Windows PowerShell 5.1 (tested there; PowerShell 7 not tried), no other dependencies.

## Set up (once per project)

1. Copy the `ftp-deploy` folder into the project.
2. Copy `ftp-deploy.settings.example.psd1` to `ftp-deploy.settings.psd1` and fill in `Server`, `Username`, `Password` (and `RemotePath` if you upload into a sub folder).
   The settings file and `.cache/` are git-ignored by the `.gitignore` in this folder.

## Upload a folder

```powershell
.\ftp-deploy\Upload-Ftp.ps1 -LocalPath .\publish              # upload and verify
.\ftp-deploy\Upload-Ftp.ps1 -LocalPath .\publish -DryRun      # show what would happen, change nothing
.\ftp-deploy\Upload-Ftp.ps1 -LocalPath .\publish -RemotePath site -PruneRemoteRootFiles
```

Every setting can be overridden on the command line (`-Server`, `-Username`, `-Password`, `-RemotePath`, `-ExcludePaths`, `-ManifestPath`, `-SettingsPath`).
For CI use the environment variables `FTP_DEPLOY_SERVER`, `FTP_DEPLOY_USERNAME`, `FTP_DEPLOY_PASSWORD` instead of a settings file.

## Use it from your own deploy script

```powershell
Import-Module .\ftp-deploy\FtpDeploy -Force
$ftp = Get-FtpSettings                                   # reads ftp-deploy.settings.psd1
Test-FtpRemoteFolder -Settings $ftp -Name 'my-site'      # guard against a wrong target folder
Invoke-FtpSync -Settings $ftp -LocalPath .\offline -RemotePath 'my-site'              # app_offline.htm
Invoke-FtpSync -Settings $ftp -LocalPath .\publish -RemotePath 'my-site' -ManifestPath .\.cache\manifest.json
Remove-FtpRemoteFile -Settings $ftp -Path 'my-site/app_offline.htm'                   # back online
```

`deploy-single-host.core.ps1` in the D2D repository is a complete example: publish, take the app offline, upload, bring it online, health-check.
Every command throws on failure, so a script with `$ErrorActionPreference = 'Stop'` stops at the first problem.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `Server`, `Username`, `Password` | required | FTP login. `Server` may include a port (`host:2121`). |
| `RemotePath` | `''` | Folder to upload into, relative to the login root. |
| `ManifestPath` | none | Local JSON of files already verified on the server; they are skipped after one SIZE call. Relative paths resolve against the settings file. |
| `ExcludePaths` | none | Files or folders (relative to the uploaded folder) that are never uploaded. |
| `PruneRemoteRootFiles` | `$false` | Delete stale files directly in the remote root before uploading. Folders are never touched. |
| `AlwaysUploadExtensions` | `.html .htm .dll .exe .json` | Re-uploaded without a size pre-check. |
| `VerifiedStagingFiles` | `Microsoft.AspNetCore.Server.IIS.dll` | Never overwritten in place (see below). |
| `ProtectedRootFiles` | `app_offline.htm` | Never pruned. |

## What makes it safe

- **Every upload is verified.** After a write the file size is read back; a server that says "OK" but lost the file is retried and finally reported as a failure.
- **Locked files.** When the server refuses to overwrite (550, typically a DLL held by IIS), the replacement is uploaded under a temporary name, verified, then renamed over the old file with retries. The old file is never deleted first.
- **Files a host filter rejects.** Files in `VerifiedStagingFiles` are staged in small chunks (the first byte on its own) and SHA-256 checked before the rename.
- **Resumable.** With a manifest, a failed run keeps what it already verified; the manifest is saved after each file. A manifest hit is only trusted after the server's size confirms it.
- **Retries** with back-off for dropped connections; the same byte range is never appended twice.

## Layout

```
ftp-deploy/
  Upload-Ftp.ps1                        command line entry point
  ftp-deploy.settings.example.psd1      copy to ftp-deploy.settings.psd1
  FtpDeploy/
    FtpDeploy.psm1                      loads Private\ and Public\
    Public/                             Get-FtpSettings, Invoke-FtpSync, Test-FtpRemoteFolder, Remove-FtpRemoteFile
    Private/                            Remote (FTP calls), Staging, Verify, Manifest, SendFile, Prune, Exclude, Session
```
