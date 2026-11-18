using Goo;
using Lokn.Core;
using Windows.Media.Control;

namespace LoomModules.NowPlaying;

/// <summary>Describes what a media player plays now, as Windows reports it.</summary>
/// <param name="App">The player's app identifier, such as <c>Spotify.exe</c>.</param>
/// <param name="Title">The track's title.</param>
/// <param name="Artist">The track's artist.</param>
/// <param name="IsPlaying">Whether it plays now rather than being paused.</param>
/// <param name="CanPrevious">Whether the player accepts going back.</param>
/// <param name="CanNext">Whether the player accepts skipping ahead.</param>
/// <param name="CanSeek">Whether the player accepts a new position.</param>
/// <param name="Position">The position when the player last reported it.</param>
/// <param name="Duration">The track's length, or zero when unknown.</param>
/// <param name="ReportedAt">When the player reported <paramref name="Position"/>.</param>
internal sealed record MediaInfo( string App, string Title, string Artist, bool IsPlaying, bool CanPrevious, bool CanNext, bool CanSeek,
	TimeSpan Position, TimeSpan Duration, DateTimeOffset ReportedAt )
{
	/// <summary>Gets the position now, moved on by the time passed while playing.</summary>
	internal TimeSpan CurrentPosition
	{
		get
		{
			var position = IsPlaying ? Position + (DateTimeOffset.Now - ReportedAt) : Position;
			return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
		}
	}

	/// <summary>Gets whether the player is Spotify.</summary>
	internal bool IsSpotify => App.Contains( "Spotify", StringComparison.OrdinalIgnoreCase );
}

/// <summary>Follows Windows' media sessions, the ones the volume flyout shows, and controls the chosen one; no player account or sign-in is needed.</summary>
/// <remarks>Windows raises its events on worker threads; every change reaches the views on the UI thread through the module's context.</remarks>
internal sealed class MediaWatcher : IDisposable
{
	/// <summary>Stores the module's context, which posts to the UI thread.</summary>
	private readonly ModuleContext _context;

	/// <summary>Reports whether any player may show, or only Spotify.</summary>
	private readonly Func<bool> _isAnyPlayer;

	/// <summary>Counts published readings; reading it in a build rebuilds the reader on each one, since Goo state holds no null.</summary>
	private readonly State<int> _version = new( 0 );

	/// <summary>Holds what the chosen player plays now, or null when nothing does; changed only on the UI thread.</summary>
	private MediaInfo? _info;

	/// <summary>Holds the cover of the playing track, or null when it has none; changed only on the UI thread.</summary>
	private ImageSource? _cover;

	/// <summary>Holds the cover's average color, or null when the track has no cover; changed only on the UI thread.</summary>
	private Color? _tint;

	/// <summary>Stores Windows' session manager once it is ready.</summary>
	private GlobalSystemMediaTransportControlsSessionManager? _manager;

	/// <summary>Stores the session shown, whose events the watcher follows.</summary>
	private GlobalSystemMediaTransportControlsSession? _session;

	/// <summary>Counts cover loads, so an older, slower one never replaces a newer one.</summary>
	private int _coverVersion;

	/// <summary>Reports whether the watcher was disposed.</summary>
	private volatile bool _isDisposed;

	/// <summary>Starts following the media sessions.</summary>
	/// <param name="context">The module's context.</param>
	/// <param name="isAnyPlayer">Reads whether players other than Spotify may show.</param>
	internal MediaWatcher( ModuleContext context, Func<bool> isAnyPlayer )
	{
		_context = context;
		_isAnyPlayer = isAnyPlayer;
		_ = StartAsync();
	}

	/// <summary>Gets what plays now, or null; reading it in a build rebuilds the reader on change.</summary>
	internal MediaInfo? Info => _version.Value >= 0 ? _info : null;

	/// <summary>Gets the track's cover, or null; reading it in a build rebuilds the reader on change.</summary>
	internal ImageSource? Cover => _version.Value >= 0 ? _cover : null;

	/// <summary>Gets the cover's average color, or null; reading it in a build rebuilds the reader on change.</summary>
	internal Color? Tint => _version.Value >= 0 ? _tint : null;

	/// <summary>Picks the session again, such as after the player setting changed.</summary>
	internal void Refresh() => _ = Task.Run( UpdateSession );

	/// <summary>Plays or pauses.</summary>
	internal void TogglePlay() => Send( session => session.TryTogglePlayPauseAsync().AsTask() );

	/// <summary>Goes to the previous track.</summary>
	internal void Previous() => Send( session => session.TrySkipPreviousAsync().AsTask() );

	/// <summary>Goes to the next track.</summary>
	internal void Next() => Send( session => session.TrySkipNextAsync().AsTask() );

	/// <summary>Moves to a position in the track.</summary>
	internal void Seek( TimeSpan position ) => Send( session => session.TryChangePlaybackPositionAsync( position.Ticks ).AsTask() );

	/// <summary>Stops following the sessions.</summary>
	public void Dispose()
	{
		_isDisposed = true;
		if ( _manager is { } manager )
		{
			manager.SessionsChanged -= UpdateSessions;
			manager.CurrentSessionChanged -= UpdateCurrent;
		}
		Follow( null );
		_cover?.Dispose();
	}

	/// <summary>Opens Windows' session manager and follows its sessions.</summary>
	private async Task StartAsync()
	{
		try
		{
			var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
			if ( _isDisposed )
				return;
			_manager = manager;
			manager.SessionsChanged += UpdateSessions;
			manager.CurrentSessionChanged += UpdateCurrent;
			UpdateSession();
		}
		catch ( Exception error ) when ( error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or PlatformNotSupportedException )
		{
			_context.Post( () => _context.App.ReportError( "Now Playing", new InvalidOperationException( $"Windows media sessions are not available: {error.Message}", error ) ) );
		}
	}

	/// <summary>Picks a session again when players start or stop.</summary>
	private void UpdateSessions( GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args ) => UpdateSession();

	/// <summary>Picks a session again when Windows chooses another current one.</summary>
	private void UpdateCurrent( GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args ) => UpdateSession();

	/// <summary>Shows Spotify when it runs, else the current session when any player may show.</summary>
	private void UpdateSession()
	{
		if ( _manager is not { } manager || _isDisposed )
			return;

		var sessions = manager.GetSessions();
		var session = sessions.FirstOrDefault( item => item.SourceAppUserModelId.Contains( "Spotify", StringComparison.OrdinalIgnoreCase ) )
			?? (_isAnyPlayer() ? manager.GetCurrentSession() : null);
		if ( !ReferenceEquals( session, _session ) && session?.SourceAppUserModelId != _session?.SourceAppUserModelId )
			Follow( session );
		_ = UpdateInfoAsync( loadsCover: true );
	}

	/// <summary>Follows one session's events, leaving the previous one's.</summary>
	private void Follow( GlobalSystemMediaTransportControlsSession? session )
	{
		if ( _session is { } old )
		{
			old.MediaPropertiesChanged -= UpdateMedia;
			old.PlaybackInfoChanged -= UpdatePlayback;
			old.TimelinePropertiesChanged -= UpdateTimeline;
		}
		_session = session;
		if ( session is null )
			return;
		session.MediaPropertiesChanged += UpdateMedia;
		session.PlaybackInfoChanged += UpdatePlayback;
		session.TimelinePropertiesChanged += UpdateTimeline;
	}

	/// <summary>Reads a new track and its cover.</summary>
	private void UpdateMedia( GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args ) => _ = UpdateInfoAsync( loadsCover: true );

	/// <summary>Reads a play, pause or control change.</summary>
	private void UpdatePlayback( GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args ) => _ = UpdateInfoAsync( loadsCover: false );

	/// <summary>Reads a new position.</summary>
	private void UpdateTimeline( GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args ) => _ = UpdateInfoAsync( loadsCover: false );

	/// <summary>Reads the session's track, state and timeline, and its cover and tint when asked, then shows them on the UI thread.</summary>
	private async Task UpdateInfoAsync( bool loadsCover )
	{
		var session = _session;
		if ( session is null )
		{
			Publish( null, null, null, loadsCover: true );
			return;
		}

		try
		{
			var media = await session.TryGetMediaPropertiesAsync();
			var playback = session.GetPlaybackInfo();
			var timeline = session.GetTimelineProperties();
			var controls = playback.Controls;
			var info = new MediaInfo( session.SourceAppUserModelId, media.Title ?? "", media.Artist ?? "",
				playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
				controls.IsPreviousEnabled, controls.IsNextEnabled, controls.IsPlaybackPositionEnabled,
				timeline.Position, timeline.EndTime - timeline.StartTime, timeline.LastUpdatedTime );
			ImageSource? cover = null;
			Color? tint = null;
			if ( loadsCover && media.Thumbnail is { } thumbnail )
			{
				using ( var stream = (await thumbnail.OpenReadAsync()).AsStreamForRead() )
					cover = ImageSource.Decode( stream );
				using var raw = await thumbnail.OpenReadAsync();
				tint = await ComputeTintAsync( raw );
			}
			Publish( info.Title.Length == 0 ? null : info, cover, tint, loadsCover );
		}
		catch ( Exception error ) when ( error is System.Runtime.InteropServices.COMException or InvalidOperationException or FormatException or NotSupportedException or IOException )
		{
			// The player closed or sent no readable cover between two events; the next event shows its state.
		}
	}

	/// <summary>Finds a cover's average color by letting Windows scale it to a few pixels.</summary>
	private static async Task<Color> ComputeTintAsync( Windows.Storage.Streams.IRandomAccessStream stream )
	{
		const uint Side = 8;
		var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync( stream );
		var data = await decoder.GetPixelDataAsync( Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
			new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = Side, ScaledHeight = Side },
			Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage );
		var pixels = data.DetachPixelData();
		long red = 0, green = 0, blue = 0;
		var count = pixels.Length / 4;
		for ( var index = 0; index < pixels.Length; index += 4 )
		{
			red += pixels[index];
			green += pixels[index + 1];
			blue += pixels[index + 2];
		}
		return count == 0 ? Theme.Current.Raised : Color.Parse( $"#{red / count:x2}{green / count:x2}{blue / count:x2}" );
	}

	/// <summary>Shows a reading on the UI thread, replacing the cover and tint only when this reading loaded them and no newer load did.</summary>
	private void Publish( MediaInfo? info, ImageSource? cover, Color? tint, bool loadsCover )
	{
		var version = loadsCover ? Interlocked.Increment( ref _coverVersion ) : 0;
		if ( !_context.Post( () =>
		{
			if ( _isDisposed )
			{
				cover?.Dispose();
				return;
			}
			_info = info;
			if ( loadsCover && version == _coverVersion )
			{
				// Mounted images keep their own lease, so the old cover can go at once.
				_cover?.Dispose();
				_cover = cover;
				_tint = tint;
			}
			else
				cover?.Dispose();
			_version.Value++;
		} ) )
			cover?.Dispose();
	}

	/// <summary>Sends a command to the shown session without waiting for the player.</summary>
	private void Send( Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command )
	{
		if ( _session is not { } session )
			return;
		_ = Task.Run( async () =>
		{
			try
			{
				await command( session );
			}
			catch ( Exception error ) when ( error is System.Runtime.InteropServices.COMException or InvalidOperationException )
			{
				// The player closed before it took the command.
			}
		} );
	}
}
