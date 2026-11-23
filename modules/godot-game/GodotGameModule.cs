using Win32Exception = System.ComponentModel.Win32Exception;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using Goo;
using Lokn.Core;

public sealed class GodotGameModule : ILoknModule
{
	internal static readonly Setting<string> Executable = new( "godot-game.executable", "Godot executable",
		"Full path to the Windows Godot executable. If blank, use GODOT_EXECUTABLE.", defaultValue: "", category: "Godot Game" );
	internal static readonly Setting<string> ProjectFolder = new( "godot-game.project", "Godot project folder",
		"Full path to the folder that contains project.godot. If blank, use GODOT_PROJECT.", defaultValue: "", category: "Godot Game" );

	public string Id => "godot-game";
	public string Title => "Godot Game";

	public void Register( ModuleBuilder builder ) =>
		builder.Setting( Executable ).Setting( ProjectFolder )
			.Panel( "play", "Godot Game", context => new GodotGamePanel( context ) )
			.Command( "start", "Play Godot Game", context =>
			{
				if ( context.App.Panels.FindContent<GodotGamePanel>() is { } panel && context.App.Panels.IsShown( panel.Id ) )
					panel.Content.Start();
				else
					context.App.Panels.Open( "godot-game.play", input: true );
			} );
}

internal sealed class GodotGamePanel( PanelContext context ) : PanelContent
{
	private readonly State<string> _status = new( "Play the local project inside Loom." );
	private readonly State<bool> _running = new( false );
	private readonly State<bool> _ready = new( false );
	private Process? _game;
	private Size? _requestedSize;
	private bool _embedded;
	private bool _placing;
	private bool _placementFailed;
	private nint _host;
	private nint _child;
	private Rectangle? _bounds;
	private long _started;
	internal PanelContext Context => context;

	public override Blob Build()
	{
		var theme = Theme.Current;
		return new Container
		{
			FlexGrow = 1, MinWidth = 0, MinHeight = 0,
			Children =
			[
				new Row
				{
					Padding = theme.Spacing, Gap = theme.Spacing, AlignItems = AlignItems.Center,
					Children =
					[
						theme.BuildButton( "Play", () => context.Run( Start ), _ready.Value && !_running.Value ),
						theme.BuildButton( "Stop", () => context.Run( Stop ), _running.Value ),
						new Text { Content = _status.Value, Color = theme.Muted },
					],
				},
				Cell.Mount<GodotGamePanel, GodotGameView>( "game", this ),
			],
		};
	}

	internal void Place( nint owner, Rectangle? bounds )
	{
		if ( !OperatingSystem.IsWindows() )
		{
			_status.Value = "The Godot Game panel requires Windows.";
			return;
		}
		if ( _host == 0 && owner != 0 && bounds is not null )
		{
			// An intermediate native child clips the game to the panel and gives --wid a stable parent.
			_host = Native.CreateWindowEx( 0, "STATIC", null, 0x46000000, 0, 0, 1, 1, owner, 0, 0, 0 );
			if ( _host == 0 )
			{
				_status.Value = new Win32Exception( Marshal.GetLastWin32Error() ).Message;
				return;
			}
			if ( context.Input is true ) context.Post( Start );
			_ready.Value = true;
		}
		if ( _host == 0 ) return;

		var visibilityChanged = (bounds is null) != (_bounds is null);
		if ( bounds != _bounds )
		{
			if ( bounds is { } area &&
				!Native.SetWindowPos( _host, 0, area.X, area.Y, area.Width, area.Height, Native.KeepZOrder | Native.NoActivate ) )
			{
				_status.Value = "Cannot position the Godot panel.";
				return;
			}
			_bounds = bounds;
			if ( visibilityChanged ) UpdateVisibility();
		}

		if ( _game is not { } game ) return;
		if ( game.HasExited )
		{
			var code = game.ExitCode;
			ReleaseGame();
			_status.Value = code == 0 ? "Game stopped." : $"Godot exited with code {code}.";
			return;
		}
		if ( _child == 0 )
		{
			_child = Native.FindGameWindow( game.Id, _host );
			if ( _child == 0 && Stopwatch.GetElapsedTime( _started ).TotalSeconds > 30 )
				_status.Value = "Godot has not opened a game window. Stop to try again.";
		}
		ApplyPlacement();
	}

	private void UpdateVisibility() => Native.ShowWindow( _host, _embedded && _bounds is not null ? 8 : 0 );

	private void ApplyPlacement()
	{
		if ( _placing || _placementFailed || _child == 0 || _bounds is not { Size: var size } || context.Lifetime.IsCancellationRequested ) return;
		if ( _requestedSize == size ) return;
		_placing = true;
		_requestedSize = size;
		var child = _child;
		var host = _host;
		var attach = !_embedded;
		// The native parent moves and clips the game during layout. Only the game size needs a cross-process update.
		_ = Task.Run( () =>
		{
			if ( context.Lifetime.IsCancellationRequested ) return;
			var embedded = !attach || Native.Embed( child, host );
			var positioned = embedded &&
				Native.SetWindowPos( child, 0, 0, 0, size.Width, size.Height, Native.KeepZOrder | Native.NoActivate | Native.Show );
			context.Post( () =>
			{
				_placing = false;
				if ( _child == child && (attach || !positioned) )
				{
					_placementFailed = !positioned;
					_embedded = positioned;
					UpdateVisibility();
					_status.Value = positioned ? "Click the game to play. Esc opens its menu."
						: embedded ? "Cannot resize Godot. Stop to try again." : "Cannot embed Godot. Stop to try again.";
				}
				ApplyPlacement();
			} );
		} );
	}

	internal void Start()
	{
		if ( _game is not null || _bounds is not { } size ) return;
		var engine = context.App.Settings.Get( GodotGameModule.Executable ).Trim();
		if ( engine.Length == 0 ) engine = Environment.GetEnvironmentVariable( "GODOT_EXECUTABLE" ) ?? "";
		if ( !Path.IsPathFullyQualified( engine ) || !File.Exists( engine ) )
		{
			_status.Value = "Set a valid Godot executable in Settings → Godot Game.";
			return;
		}
		var project = context.App.Settings.Get( GodotGameModule.ProjectFolder ).Trim();
		if ( project.Length == 0 ) project = Environment.GetEnvironmentVariable( "GODOT_PROJECT" ) ?? "";
		if ( !Path.IsPathFullyQualified( project ) || !File.Exists( Path.Combine( project, "project.godot" ) ) )
		{
			_status.Value = "Set the folder that contains project.godot in Settings → Godot Game.";
			return;
		}
		var start = new ProcessStartInfo( engine ) { WorkingDirectory = project, UseShellExecute = false };
		foreach ( var arg in new[] { "--path", project, "--single-window", "--wid", _host.ToInt64().ToString( CultureInfo.InvariantCulture ), "--resolution", $"{size.Width}x{size.Height}" } )
			start.ArgumentList.Add( arg );
		try { _game = Process.Start( start ) ?? throw new InvalidOperationException( "Godot did not start." ); }
		catch ( Exception error ) when ( error is Win32Exception or InvalidOperationException )
		{
			_status.Value = error.Message;
			return;
		}
		_started = Stopwatch.GetTimestamp();
		_running.Value = true;
		_status.Value = "Starting Godot…";
	}

	private void Stop()
	{
		if ( _game is not { } game || game.HasExited ) return;
		if ( _child != 0 && Native.PostMessage( _child, 0x10, 0, 0 ) )
			_status.Value = "Stopping Godot…";
		else
			Kill( game );
	}

	private void ReleaseGame()
	{
		_game?.Dispose();
		_game = null;
		_child = 0;
		_requestedSize = null;
		_placementFailed = false;
		_embedded = false;
		_running.Value = false;
		Native.ShowWindow( _host, 0 );
	}

	private static void Kill( Process game )
	{
		try { if ( !game.HasExited ) game.Kill(); }
		catch ( InvalidOperationException ) when ( game.HasExited ) { }
	}

	public override void Closed()
	{
		if ( _game is { } game )
		{
			Stop();
			_game = null;
			_ = ReapAsync( game );
		}
		if ( _host != 0 ) Native.DestroyWindow( _host );
	}

	private static async Task ReapAsync( Process game )
	{
		using ( game )
		using ( var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( 5 ) ) )
		{
			try { await game.WaitForExitAsync( timeout.Token ); }
			catch ( OperationCanceledException ) { Kill( game ); }
		}
	}
}

internal sealed class GodotGameView : GuardedCell<GodotGamePanel>
{
	private readonly ElementHandle _area = new();
	private nint _owner;

	protected override Blob BuildView( GodotGamePanel input ) => new Container { Handle = _area, FlexGrow = 1, MinWidth = 0, MinHeight = 0 };

	protected override void Mounted()
	{
		_area.MetricsChanged += MetricsChanged;
		Input.Context.App.NativeSurfaceOcclusionChanged += Update;
		Every( 50, Update );
		_ = GetOwnerAsync();
	}

	private async Task GetOwnerAsync()
	{
		try
		{
			var lease = await Host.AcquireNativeDialogOwnerAsync( Lifetime );
			if ( lease.Owner is not { } owner ) return;
			using ( owner ) _owner = owner.Handle;
			if ( !Lifetime.IsCancellationRequested ) Update();
		}
		catch ( Exception error ) when ( error is OperationCanceledException or InvalidOperationException or ObjectDisposedException ) { }
	}

	private void MetricsChanged( ElementMetrics metrics ) => Update();

	private void Update()
	{
		if ( _owner == 0 || !Host.IsOpen || Input.Context.Lifetime.IsCancellationRequested ) return;
		var box = _area.BorderBox;
		var app = Input.Context.App;
		if ( !_area.IsMounted || box.Width < 1 || box.Height < 1 || !app.Panels.IsShown( Input.Context.Id ) || app.NativeSurfacesOccluded )
		{
			Input.Place( _owner, null );
			return;
		}
		var scale = Host.NativeWidth > 0 && Native.GetClientRect( _owner, out var client ) ? Host.UiScale * (client.Right - client.Left) / Host.NativeWidth : Host.UiScale;
		Input.Place( _owner, Rectangle.FromLTRB( (int)Math.Round( box.X * scale ), (int)Math.Round( box.Y * scale ),
			(int)Math.Round( (box.X + box.Width) * scale ), (int)Math.Round( (box.Y + box.Height) * scale ) ) );
	}

	protected override void Unmounting()
	{
		_area.MetricsChanged -= MetricsChanged;
		Input.Context.App.NativeSurfaceOcclusionChanged -= Update;
		if ( !Input.Context.Lifetime.IsCancellationRequested ) Input.Place( _owner, null );
		_owner = 0;
	}
}

internal static class Native
{
	[StructLayout( LayoutKind.Sequential )]
	internal struct Rect { internal int Left, Top, Right, Bottom; }

	[DllImport( "user32.dll", CharSet = CharSet.Unicode, SetLastError = true )]
	internal static extern nint CreateWindowEx( uint exStyle, string className, string? name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param );
	[DllImport( "user32.dll" )] internal static extern bool GetClientRect( nint window, out Rect rect );
	[DllImport( "user32.dll" )] internal static extern bool SetWindowPos( nint window, nint after, int x, int y, int width, int height, uint flags );
	[DllImport( "user32.dll" )] internal static extern bool ShowWindow( nint window, int command );
	[DllImport( "user32.dll" )] internal static extern bool PostMessage( nint window, uint message, nint wParam, nint lParam );
	[DllImport( "user32.dll" )] internal static extern bool DestroyWindow( nint window );
	[DllImport( "user32.dll", EntryPoint = "GetWindowLongW" )] private static extern int GetWindowLong( nint window, int index );
	[DllImport( "user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true )] private static extern int SetWindowLong( nint window, int index, int value );
	[DllImport( "user32.dll", SetLastError = true )] private static extern nint SetParent( nint window, nint parent );
	[DllImport( "user32.dll" )] private static extern nint GetAncestor( nint window, uint flags );
	[DllImport( "user32.dll" )] private static extern nint GetWindow( nint window, uint command );
	[DllImport( "user32.dll", CharSet = CharSet.Unicode )] private static extern int GetClassName( nint window, System.Text.StringBuilder name, int capacity );
	private delegate bool EnumCallback( nint window, nint data );
	[DllImport( "user32.dll" )] private static extern bool EnumWindows( EnumCallback callback, nint data );
	[DllImport( "user32.dll" )] private static extern uint GetWindowThreadProcessId( nint window, out uint pid );

	internal const uint KeepZOrder = 0x04, NoActivate = 0x10, Show = 0x40;
	private const int StyleIndex = -16;
	private const int ChildStyle = 0x40000000, PopupStyle = unchecked((int)0x80000000);

	internal static bool Embed( nint child, nint host )
	{
		var style = GetWindowLong( child, StyleIndex );
		if ( SetWindowLong( child, StyleIndex, (style & ~PopupStyle) | ChildStyle ) == 0 && Marshal.GetLastWin32Error() != 0 )
			return false;
		var parented = GetAncestor( child, 1 ) == host || SetParent( child, host ) != 0 || Marshal.GetLastWin32Error() == 0;
		// Retain Godot's popup style for mouse activation. The native parent still supplies panel-relative coordinates.
		var restored = SetWindowLong( child, StyleIndex, style ) != 0 || Marshal.GetLastWin32Error() == 0;
		return parented && restored;
	}

	internal static nint FindGameWindow( int processId, nint host )
	{
		nint found = 0;
		var name = new System.Text.StringBuilder( 32 );
		var root = GetAncestor( host, 2 );
		EnumWindows( ( window, _ ) =>
		{
			GetWindowThreadProcessId( window, out var pid );
			if ( pid != processId ) return true;
			// Windows can promote the owner of a popup from a child HWND to its top-level ancestor.
			var owner = GetWindow( window, 4 );
			if ( owner == 0 || (owner != host && owner != root) ) return true;
			if ( GetClassName( window, name, name.Capacity ) != 6 || !name.ToString().Equals( "Engine", StringComparison.Ordinal ) ) return true;
			found = window;
			return false;
		}, 0 );
		return found;
	}
}
