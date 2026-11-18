<#
.SYNOPSIS
Checks every module of the hub: its files, its description, that it compiles against Loom, and that it defines the module its description names.

.DESCRIPTION
For each folder in modules/:
  - the folder name is the id: lowercase letters, digits and dashes;
  - it holds only module.json, README.md and .cs files, each under 1 MB, with no subfolders;
  - module.json has id (equal to the folder name), name, author, description and version (such as 1.2.0);
  - its .cs files compile against Loom's Lokn.Core and the libraries Loom ships, as Loom compiles them;
  - the compiled module defines exactly one ILoknModule with a parameterless constructor, whose Id is the folder name.
With -Base, every module changed since that commit must also raise its version, so installed copies update.

Exits non-zero when any module fails.

.EXAMPLE
pwsh ci/check.ps1 -Loom ..\Lokn

.EXAMPLE
pwsh ci/check.ps1 -Loom .loom -Base origin/main
#>
param(
	# A checkout of the Loom repository, whose Core the modules compile against.
	[Parameter( Mandatory )] [string] $Loom,
	# The commit to compare against, such as a pull request's base; modules changed since then must raise their version.
	[string] $Base
)

$ErrorActionPreference = 'Stop'
$hub = Split-Path -Parent $PSScriptRoot
$loomRoot = ( Resolve-Path $Loom ).Path
$work = Join-Path ( [IO.Path]::GetTempPath() ) "loom-modules-check-$PID"
$failures = [Collections.Generic.List[string]]::new()
$allowed = '^(module\.json|README\.md|[^/\\]+\.cs)$'

# Modules compile against the same packages Loom ships, at the versions Loom uses.
[xml]$app = Get-Content ( Join-Path $loomRoot 'Lokn.csproj' ) -Raw
function Get-PackageVersion( [string] $Id ) { ( $app.Project.ItemGroup.PackageReference | Where-Object Include -EQ $Id | Select-Object -First 1 ).Version }
$webView = Get-PackageVersion 'Microsoft.Web.WebView2'
$windowsSdk = Get-PackageVersion 'Microsoft.Windows.SDK.NET.Ref'

Write-Host 'Building the inspector...'
dotnet build "$PSScriptRoot/Inspect/Inspect.csproj" -c Release -nologo -v q "-p:LoomRoot=$loomRoot" -o "$work/inspect" | Out-Host
if ( $LASTEXITCODE -ne 0 ) { Write-Host 'CHECK FAILED: the inspector did not build'; exit 1 }

$modules = @( Get-ChildItem "$hub/modules" -Directory | Sort-Object Name )
foreach ( $folder in $modules )
{
	$id = $folder.Name
	Write-Host "== $id"
	$problems = [Collections.Generic.List[string]]::new()
	if ( $id -notmatch '^[a-z0-9-]{1,64}$' ) { $problems.Add( 'the folder name must be lowercase letters, digits and dashes' ) }
	if ( Get-ChildItem $folder -Directory ) { $problems.Add( 'subfolders are not allowed' ) }
	foreach ( $file in Get-ChildItem $folder -File )
	{
		if ( $file.Name -notmatch $allowed -or $file.Name.StartsWith( '.' ) ) { $problems.Add( "file not allowed: $( $file.Name ) (only module.json, README.md and .cs files)" ) }
		if ( $file.Length -gt 1MB ) { $problems.Add( "file over 1 MB: $( $file.Name )" ) }
	}

	$description = $null
	try { $description = Get-Content "$folder/module.json" -Raw | ConvertFrom-Json }
	catch { $problems.Add( "module.json is missing or not JSON: $( $_.Exception.Message )" ) }
	if ( $description )
	{
		foreach ( $field in 'id', 'name', 'author', 'description', 'version' )
		{
			if ( -not "$( $description.$field )".Trim() ) { $problems.Add( "module.json needs '$field'" ) }
		}
		if ( $description.id -and $description.id -ne $id ) { $problems.Add( "module.json id '$( $description.id )' must be the folder name '$id'" ) }
		$version = $null
		if ( $description.version -and -not [Version]::TryParse( $description.version, [ref]$version ) ) { $problems.Add( "version '$( $description.version )' must look like 1.2.0" ) }
	}

	if ( -not ( Get-ChildItem $folder -Filter *.cs ) ) { $problems.Add( 'the module has no .cs files' ) }

	if ( $problems.Count -eq 0 )
	{
		$project = "$work/$id"
		New-Item -ItemType Directory $project -Force | Out-Null
		@"
<Project Sdk="Microsoft.NET.Sdk">
	<PropertyGroup>
		<TargetFramework>net10.0</TargetFramework>
		<LangVersion>14</LangVersion>
		<ImplicitUsings>enable</ImplicitUsings>
		<Nullable>enable</Nullable>
		<AssemblyName>LoomModule.$id</AssemblyName>
		<EnableDefaultCompileItems>false</EnableDefaultCompileItems>
	</PropertyGroup>
	<ItemGroup>
		<Compile Include="$( $folder.FullName )\*.cs" />
		<ProjectReference Include="$loomRoot\Core\Lokn.Core.csproj" />
		<PackageReference Include="Microsoft.Web.WebView2" Version="$webView" ExcludeAssets="all" GeneratePathProperty="true" PrivateAssets="all" />
		<Reference Include="`$(PkgMicrosoft_Web_WebView2)\lib_manual\netcoreapp3.0\Microsoft.Web.WebView2.Core.dll" />
		<PackageReference Include="Microsoft.Windows.SDK.NET.Ref" Version="$windowsSdk" ExcludeAssets="all" GeneratePathProperty="true" PrivateAssets="all" />
		<Reference Include="`$(PkgMicrosoft_Windows_SDK_NET_Ref)\lib\net9.0\Microsoft.Windows.SDK.NET.dll" />
		<Reference Include="`$(PkgMicrosoft_Windows_SDK_NET_Ref)\lib\net9.0\WinRT.Runtime.dll" />
	</ItemGroup>
</Project>
"@ | Set-Content "$project/Module.csproj"
		dotnet build "$project/Module.csproj" -c Release -nologo -v q -o "$project/out" | Out-Host
		if ( $LASTEXITCODE -ne 0 ) { $problems.Add( 'it does not compile against Loom' ) }
		else
		{
			dotnet "$work/inspect/Inspect.dll" "$project/out/LoomModule.$id.dll" $id | Out-Host
			if ( $LASTEXITCODE -ne 0 ) { $problems.Add( 'it does not define the module its description names' ) }
		}
	}

	if ( $Base -and $description.version )
	{
		git -C $hub diff --quiet $Base HEAD -- "modules/$id"
		if ( $LASTEXITCODE -eq 1 )
		{
			$previous = git -C $hub show "$( $Base ):modules/$id/module.json" 2>$null
			if ( $LASTEXITCODE -eq 0 )
			{
				$before = ( $previous -join "`n" | ConvertFrom-Json ).version
				$beforeVersion = $null
				$version = $null
				if ( [Version]::TryParse( $before, [ref]$beforeVersion ) -and [Version]::TryParse( $description.version, [ref]$version ) -and $version -le $beforeVersion )
				{
					$problems.Add( "it changed, so its version must rise above $before" )
				}
			}
		}
	}

	foreach ( $problem in $problems ) { $failures.Add( "$( $id ): $problem" ) }
	Write-Host ( $( if ( $problems.Count -eq 0 ) { "OK $id" } else { "FAILED $id" } ) )
}

Remove-Item $work -Recurse -Force -ErrorAction Ignore
if ( $failures.Count -gt 0 )
{
	$failures | ForEach-Object { Write-Host "  $_" }
	Write-Host "CHECK FAILED: $( $failures.Count ) problem(s)"
	exit 1
}

Write-Host "CHECK OK ($( $modules.Count ) modules)"
