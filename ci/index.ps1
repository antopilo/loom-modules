<#
.SYNOPSIS
Writes index.json: every hub module with its description and the SHA-256 of each file, at one commit.

.DESCRIPTION
Loom reads index.json to list the hub's modules, then downloads each file of a module from that commit
and checks it against its hash. The Publish workflow uploads the file to the release tagged 'index'.
Run it on a checkout that converts no line endings, so the hashes match the files GitHub serves.

.EXAMPLE
pwsh ci/index.ps1 -Commit (git rev-parse HEAD) -Output index.json
#>
param(
	# The commit Loom downloads module files from.
	[Parameter( Mandatory )] [string] $Commit,
	# Where to write the index.
	[string] $Output = 'index.json'
)

$ErrorActionPreference = 'Stop'
$hub = Split-Path -Parent $PSScriptRoot
$modules = foreach ( $folder in Get-ChildItem "$hub/modules" -Directory | Sort-Object Name )
{
	$description = Get-Content "$folder/module.json" -Raw | ConvertFrom-Json
	[ordered]@{
		id = $folder.Name
		name = $description.name
		author = $description.author
		description = $description.description
		version = $description.version
		files = @( Get-ChildItem $folder -File | Sort-Object Name | ForEach-Object {
			[ordered]@{ path = $_.Name; sha256 = ( Get-FileHash $_.FullName -Algorithm SHA256 ).Hash }
		} )
	}
}

[ordered]@{ commit = $Commit; modules = @( $modules ) } | ConvertTo-Json -Depth 6 | Set-Content $Output -NoNewline
Write-Host "INDEX OK ($( @( $modules ).Count ) modules at $Commit): $Output"
