<#
.SYNOPSIS
  Adds a migration to the master database for EVERY provider (SQL Server, PostgreSQL, MySQL, SQLite).

.EXAMPLE
  ./scripts/add-migration.ps1 AddUserPhone

Run it after changing an entity or MasterDbContext. Commit the generated files together with the code change.
The test MigrationsTests fails until all four providers have the migration.
#>
param([Parameter(Mandatory)][string]$Name)

$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    dotnet tool restore | Out-Null
    # Build first: the tool reads the model from the compiled code.
    dotnet build D2D.sln -nologo -v q
    if ($LASTEXITCODE) { throw 'Build failed' }

    foreach ($provider in 'SqlServer', 'PostgreSql', 'MySql', 'Sqlite') {
        Write-Host "== $provider"
        $project = "src/D2D.Migrations.$provider"
        dotnet ef migrations add $Name --project $project --startup-project $project --context MasterDbContext --output-dir Migrations --no-build
        if ($LASTEXITCODE) { throw "Adding the migration failed for $provider" }
    }
    Write-Host "Done. Review and commit the new files in src/D2D.Migrations.*/Migrations."
}
finally { Pop-Location }
