// Loads a compiled hub module and checks that it defines exactly one module, as Loom requires, with the id its folder has.
// Usage: Inspect <module.dll> <expected id>
using System.Reflection;
using System.Runtime.Loader;
using Lokn.Core;

if ( args is not [var path, var expected] )
{
	Console.Error.WriteLine( "Usage: Inspect <module.dll> <expected id>" );
	return 2;
}

var file = Path.GetFullPath( path );
var folder = Path.GetDirectoryName( file )!;
// Libraries the module compiled against, such as the Windows SDK projection, sit beside it; Loom ships them.
AssemblyLoadContext.Default.Resolving += ( context, name ) => Path.Combine( folder, name.Name + ".dll" ) is var candidate && File.Exists( candidate ) ? context.LoadFromAssemblyPath( candidate ) : null;
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath( file );

Type[] types;
try
{
	types = assembly.GetTypes();
}
catch ( ReflectionTypeLoadException error )
{
	Console.Error.WriteLine( $"A type does not load: {error.LoaderExceptions.FirstOrDefault()?.Message}" );
	return 1;
}

var modules = types.Where( type => !type.IsAbstract && typeof( ILoknModule ).IsAssignableFrom( type ) && type.GetConstructor( Type.EmptyTypes ) is not null ).ToArray();
if ( modules.Length != 1 )
{
	Console.Error.WriteLine( $"A module must define exactly one ILoknModule with a parameterless constructor; found {modules.Length}." );
	return 1;
}

var module = (ILoknModule)Activator.CreateInstance( modules[0] )!;
if ( module.Id != expected )
{
	Console.Error.WriteLine( $"The module's Id is '{module.Id}', but its folder is '{expected}'. They must be equal." );
	return 1;
}

Console.WriteLine( $"Defines {modules[0].FullName}: {module.Title} ({module.Id})" );
return 0;
