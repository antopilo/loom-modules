using Lokn.Core;

namespace LoomModules.NowPlaying;

/// <summary>Shows what Spotify, or another player, plays now in a small control banner at the bottom of the sidebar.</summary>
/// <remarks>It reads Windows' media sessions, the ones the volume flyout shows, so it needs no Spotify account or sign-in; liking songs or choosing playlists would need Spotify's Web API and is not offered.</remarks>
public sealed class NowPlayingModule : ILoknModule
{
	/// <summary>Defines whether players other than Spotify show when Spotify is not running.</summary>
	internal static readonly Setting<bool> AnyPlayer = new(
		"nowplaying.any-player", "Any player", "Show other players, such as a video in the browser, when Spotify is not running.",
		true, "Now Playing" );

	/// <summary>Stores the session watcher while the module runs.</summary>
	private MediaWatcher? _watcher;

	/// <summary>Gets the module identifier.</summary>
	public string Id => "nowplaying";

	/// <summary>Gets the display name.</summary>
	public string Title => "Now Playing";

	/// <summary>Registers the setting and the banner at the bottom of the sidebar, after every other section.</summary>
	public void Register( ModuleBuilder builder ) => builder
		.Setting( AnyPlayer )
		.Slot( ShellSlots.Sidebar, 1000, context => new NowPlayingContent( context,
			_watcher ?? throw new InvalidOperationException( "The Now Playing module is not running." ) ) );

	/// <summary>Starts following the media sessions, picking the player again when the setting changes.</summary>
	public void Start( ModuleContext context )
	{
		_settings = context.App.Settings;
		_watcher = new MediaWatcher( context, () => context.App.Settings.Get( AnyPlayer ) );
		_settings.Changed += _watcher.Refresh;
	}

	/// <summary>Stores the settings the watcher follows, to leave them at stop.</summary>
	private SettingsService? _settings;

	/// <summary>Stops following the media sessions.</summary>
	public void Stop()
	{
		if ( _watcher is { } watcher )
		{
			if ( _settings is not null )
				_settings.Changed -= watcher.Refresh;
			watcher.Dispose();
		}
		_watcher = null;
	}
}
