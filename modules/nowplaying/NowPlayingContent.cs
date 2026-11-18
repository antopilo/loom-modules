using Goo;
using Lokn.Core;

namespace LoomModules.NowPlaying;

/// <summary>Draws the banner at the bottom of the sidebar: the cover, the title and artist, previous, play or pause and next, and the track's progress; nothing while no player plays.</summary>
internal sealed class NowPlayingContent( PanelContext context, MediaWatcher watcher ) : PanelContent
{
	/// <summary>Defines the cover's side in logical pixels.</summary>
	private const int CoverSize = 28;

	/// <summary>Defines a control button's side in logical pixels.</summary>
	private const int ButtonSize = 22;

	/// <summary>Defines the Segoe Fluent Icons glyphs of the controls.</summary>
	private const string PreviousGlyph = "\uE892", PlayGlyph = "\uE768", PauseGlyph = "\uE769", NextGlyph = "\uE893", MusicGlyph = "\uE8D6";

	/// <summary>Registers the installed Windows icon font the controls use.</summary>
	internal static readonly FontSource IconFont = BuildIconFont();

	/// <summary>Defines how much of the cover's color tints the banner.</summary>
	private const double TintShare = 0.35;

	/// <summary>Defines how long the controls take to fade and slide in or out, in milliseconds.</summary>
	private const int ControlsMs = 180;

	/// <summary>Defines how far right the hidden controls rest, in logical pixels, so they slide in.</summary>
	private const double ControlsSlide = 16;

	/// <summary>Tracks whether the pointer is on the banner, which shows the controls over its right side.</summary>
	private readonly State<bool> _isHovered = new( false );

	/// <summary>Builds the banner, or nothing while no player plays: at rest the cover, the title and the progress on the cover's tint; hovered, the controls over the right side.</summary>
	public override Blob Build()
	{
		if ( watcher.Info is not { } info )
			return new Container();

		var theme = Theme.Current;
		var background = watcher.Tint is { } tint ? Mix( theme.Raised, tint, TintShare ) : theme.Raised;
		var children = new List<Blob>
		{
			new Row
			{
				AlignItems = AlignItems.Center,
				Gap = theme.Spacing * 0.75,
				Padding = new Edges( 4, 4, 4, 4 ),
				Children =
				[
					BuildCover(),
					new Container
					{
						FlexGrow = 1,
						FlexShrink = 1,
						MinWidth = 0,
						Children = [BuildLine( info.Title, theme.Text, theme.FontSize - 1 )],
					},
				],
			},
			Cell.Mount<ProgressInput, ProgressBar>( "progress", new( info, watcher.Seek ) ),
		};
		// The controls stay mounted and fade in while sliding from the right, so entering and leaving both animate;
		// hidden, they take no clicks. They cover the end of the title on the same tint, leaving the cover and the progress in view.
		var isHovered = _isHovered.Value;
		children.Add( new Row
		{
			Key = "controls",
			Position = PositionType.Absolute,
			Top = 0,
			Right = 0,
			Bottom = 4,
			AlignItems = AlignItems.Center,
			Gap = 2,
			Padding = new Edges( 0, 4, 0, theme.Spacing ),
			BackgroundColor = background,
			Opacity = isHovered ? 1 : 0,
			Transform = new PanelTransform { TranslateX = isHovered ? 0 : ControlsSlide },
			TransitionMs = ControlsMs,
			TransitionEasing = Easing.EaseOut,
			TransitionProperties = [TransitionProperty.Opacity, TransitionProperty.Transform, TransitionProperty.BackgroundColor],
			Children =
			[
				BuildButton( PreviousGlyph, "Previous track", info.CanPrevious, isHovered, watcher.Previous ),
				BuildButton( info.IsPlaying ? PauseGlyph : PlayGlyph, info.IsPlaying ? "Pause" : "Play", true, isHovered, watcher.TogglePlay ),
				BuildButton( NextGlyph, "Next track", info.CanNext, isHovered, watcher.Next ),
			],
		} );

		return new Container
		{
			AccessibilityRole = AccessibilityRole.Group,
			AccessibilityName = $"Now playing: {info.Title} by {info.Artist}",
			Margin = new Edges( 0, theme.Spacing, theme.Spacing / 2, theme.Spacing ),
			Overflow = Overflow.Hidden,
			BackgroundColor = background,
			BorderRadius = theme.Radius + 2,
			TransitionMs = 300,
			TransitionProperties = [TransitionProperty.BackgroundColor],
			OnPointerEnter = _ => context.Run( () => _isHovered.Value = true ),
			OnPointerLeave = _ => context.Run( () => _isHovered.Value = false ),
			Children = children,
		};
	}

	/// <summary>Mixes two colors, taking <paramref name="share"/> of the second.</summary>
	private static Color Mix( Color from, Color to, double share )
	{
		static int Channel( double a, double b, double share ) => (int)Math.Round( Math.Clamp( a + (b - a) * share, 0, 1 ) * 255 );
		return Color.Parse( $"#{Channel( from.R, to.R, share ):x2}{Channel( from.G, to.G, share ):x2}{Channel( from.B, to.B, share ):x2}" );
	}

	/// <summary>Builds the cover, or a music note while the track has none.</summary>
	private Blob BuildCover()
	{
		var theme = Theme.Current;
		return new Container
		{
			Width = CoverSize,
			Height = CoverSize,
			FlexShrink = 0,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			Overflow = Overflow.Hidden,
			BorderRadius = theme.Radius,
			BackgroundColor = theme.Hover,
			Children = watcher.Cover is { } cover
				? [new Image { Source = cover, Fit = ImageFit.Cover, Width = Length.Percent( 100 ), Height = Length.Percent( 100 ) }]
				: [new Text { Content = MusicGlyph, FontFamily = IconFont.Family, FontSize = 16, Color = theme.Muted }],
		};
	}

	/// <summary>Builds one line of text, cut with an ellipsis when it does not fit.</summary>
	private static Text BuildLine( string text, Color color, double size ) => new()
	{
		Content = text,
		Color = color,
		FontSize = size,
		TextWrap = TextWrap.NoWrap,
		TextTrimming = TextTrimming.Ellipsis,
	};

	/// <summary>Builds a round control button, dimmed when the player does not accept it, and taking clicks only while <paramref name="isShown"/>.</summary>
	private Button BuildButton( string glyph, string name, bool isEnabled, bool isShown, Action run )
	{
		var theme = Theme.Current;
		var isActive = isEnabled && isShown;
		void Run() => context.Run( run );
		return new Button
		{
			Width = ButtonSize,
			Height = ButtonSize,
			AlignItems = AlignItems.Center,
			JustifyContent = JustifyContent.Center,
			BorderRadius = ButtonSize / 2,
			BackgroundColor = Color.Transparent,
			Color = isEnabled ? theme.Text : theme.Muted,
			Opacity = isEnabled ? 1 : 0.4,
			Cursor = isActive ? Cursor.Pointer : Cursor.Default,
			Focusable = isActive,
			AccessibilityName = name,
			HoverStyle = isActive ? new Style { BackgroundColor = theme.Hover } : null,
			OnClick = isActive ? Run : null,
			KeyBindings = isActive ? [new( Key.Enter, KeyModifiers.None, Run ), new( Key.Space, KeyModifiers.None, Run )] : [],
			Children = [new Text { Content = glyph, FontFamily = IconFont.Family, FontSize = 11 }],
		};
	}

	/// <summary>Registers Segoe Fluent Icons from the Windows fonts folder.</summary>
	private static FontSource BuildIconFont()
	{
		var font = FontSource.FromPath( "Lokn Now Playing Icons", 400, italic: false,
			Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.Fonts ), "SegoeIcons.ttf" ) );
		font.Register();
		return font;
	}
}

/// <summary>Describes the progress bar's track and how to seek in it.</summary>
/// <param name="Info">The playing track.</param>
/// <param name="Seek">Moves to a position, when the player accepts it.</param>
internal sealed record ProgressInput( MediaInfo Info, Action<TimeSpan> Seek );

/// <summary>Draws the progress as a hairline along the banner's bottom edge, clickable to seek; it moves on by itself every half second, so only it rebuilds while a track plays.</summary>
internal sealed class ProgressBar : GuardedCell<ProgressInput>
{
	/// <summary>Defines how often the bar moves on, in milliseconds.</summary>
	private const double TickMs = 500;

	/// <summary>Retains the bar, to turn a click into a position.</summary>
	private readonly ElementHandle _track = new();

	/// <summary>Counts ticks, so reading it rebuilds the bar on each one.</summary>
	private readonly State<int> _tick = new( 0 );

	/// <summary>Moves the bar on while it is shown.</summary>
	protected override void Mounted() => Every( TickMs, () => _tick.Value++ );

	/// <summary>Builds the hairline at the current position, with a slightly taller strip to click.</summary>
	protected override Blob BuildView( ProgressInput input )
	{
		_ = _tick.Value;
		var theme = Theme.Current;
		var info = input.Info;
		var position = info.CurrentPosition;
		var share = info.Duration > TimeSpan.Zero ? Math.Clamp( position / info.Duration, 0, 1 ) : 0;
		return new Container
		{
			Handle = _track,
			Height = 4,
			JustifyContent = JustifyContent.FlexEnd,
			Cursor = info.CanSeek ? Cursor.Pointer : Cursor.Default,
			AccessibilityRole = AccessibilityRole.ProgressBar,
			AccessibilityName = $"{Format( position )} of {Format( info.Duration )}",
			OnPointerDown = info.CanSeek && info.Duration > TimeSpan.Zero ? e => Seek( e, input ) : null,
			Children =
			[
				new Container
				{
					Height = 2,
					BackgroundColor = theme.Hover,
					Children = [new Container { Width = Length.Percent( share * 100 ), Height = 2, BackgroundColor = theme.Accent }],
				},
			],
		};
	}

	/// <summary>Moves to the position under the pointer.</summary>
	private void Seek( PointerEvent e, ProgressInput input )
	{
		var width = _track.BorderBox.Width;
		if ( width > 0 )
			input.Seek( input.Info.Duration * Math.Clamp( e.Position.X / width, 0, 1 ) );
	}

	/// <summary>Formats a time as minutes and seconds, or hours too for long tracks.</summary>
	private static string Format( TimeSpan time ) => time.TotalHours >= 1 ? time.ToString( @"h\:mm\:ss" ) : time.ToString( @"m\:ss" );
}
