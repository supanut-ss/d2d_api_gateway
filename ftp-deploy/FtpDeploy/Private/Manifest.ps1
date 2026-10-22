# The manifest is a local JSON file of {relativePath: sha256} for files this machine already verified on the server.
# A matching hash lets a file be skipped after one cheap SIZE call; it records OUR upload history, not the server's
# state, which is what keeps it safe on a host that has shown "ghost success" behavior (see Send-FtpFile's post-upload
# size check, which is what earns a file its entry). It is saved after every verified upload, so a failed run keeps
# the progress it already confirmed.

function Import-FtpManifest {
    $script:Manifest = @{}
    $path = $script:Session.ManifestPath
    if (-not $path -or -not (Test-Path $path)) { return }

    try {
        $loaded = Get-Content -Path $path -Raw | ConvertFrom-Json
        foreach ($prop in $loaded.PSObject.Properties) { $script:Manifest[$prop.Name] = $prop.Value }
        Write-Host "Loaded manifest: $($script:Manifest.Count) known-good files from $path"
    }
    catch {
        Write-Warning "Could not read manifest at '$path' - starting fresh: $($_.Exception.Message)"
    }
}

function Save-FtpManifest {
    $path = $script:Session.ManifestPath
    if (-not $path) { return }

    $dir = Split-Path $path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $script:Manifest | ConvertTo-Json | Set-Content -Path $path -Encoding utf8
}

function Register-VerifiedUpload {
    param([string]$RelativePath, [string]$Hash)

    if (-not $script:Session.ManifestPath) { return }
    $script:Manifest[$RelativePath] = $Hash
    Save-FtpManifest
}
